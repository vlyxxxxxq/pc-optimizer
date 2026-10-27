using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO.Pipes;

namespace Rig.Services;

public sealed record CaptureDevice(string Id, string Name) { public override string ToString() => Name; }

internal sealed class CaptureAudio : IDisposable
{
    readonly List<WasapiCapture> _sources = [];
    readonly MMDeviceEnumerator _devices = new();
    readonly List<MMDevice> _selected = [];
    readonly MixingSampleProvider _mix = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
    readonly NamedPipeServerStream _pipe;
    readonly CancellationTokenSource _cancel = new();
    Task? _pump;
    int _disposed;
    public string PipePath { get; }
    public Exception? Error { get; private set; }
    public static IReadOnlyList<CaptureDevice> Devices(bool microphone)
    {
        using var devices = new MMDeviceEnumerator();
        var collection = devices.EnumerateAudioEndPoints(microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active);
        var result = new List<CaptureDevice> { new("", "Windows default device") };
        foreach (var device in collection) { result.Add(new(device.ID, device.FriendlyName)); device.Dispose(); }
        return result;
    }
    public CaptureAudio(AppSettings settings)
    {
        string name = "Rig-audio-" + Guid.NewGuid().ToString("N");
        PipePath = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            if (settings.CaptureSystemAudio) Add(false, settings.CaptureOutputDevice, (float)settings.CaptureSystemVolume);
            if (settings.CaptureMicrophone) Add(true, settings.CaptureMicDevice, (float)settings.CaptureMicVolume);
        }
        catch { Dispose(); throw; }
    }
    void Add(bool microphone, string? id, float volume)
    {
        var device = string.IsNullOrWhiteSpace(id) ? _devices.GetDefaultAudioEndpoint(microphone ? DataFlow.Capture : DataFlow.Render, microphone ? Role.Communications : Role.Multimedia) : _devices.GetDevice(id);
        _selected.Add(device);
        WasapiCapture source = microphone ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
        _sources.Add(source);
        var buffer = new BufferedWaveProvider(source.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true, ReadFully = true };
        source.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        source.RecordingStopped += (_, e) => { if (e.Exception is not null) Error = e.Exception; };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels != 2)
        {
            var stereo = new MultiplexingSampleProvider([samples], 2);
            stereo.ConnectInputToOutput(0, 0); stereo.ConnectInputToOutput(1, 1); samples = stereo;
        }
        if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
        _mix.AddMixerInput(new VolumeSampleProvider(samples) { Volume = volume });
    }
    public void Start()
    {
        foreach (var source in _sources) source.StartRecording();
        _pump = Task.Run(async () =>
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_cancel.Token);
                var floats = new float[960];
                var bytes = new byte[floats.Length * sizeof(float)];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long frame = 0;
                while (!_cancel.IsCancellationRequested)
                {
                    if (Error is not null) throw new IOException("Audio capture stopped. Check the selected devices.", Error);
                    _mix.Read(floats, 0, floats.Length);
                    for (int i = 0; i < floats.Length; i++) floats[i] = Math.Clamp(floats[i], -1, 1);
                    Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
                    await _pipe.WriteAsync(bytes, _cancel.Token);
                    int delay = (int)(++frame * 10 - clock.ElapsedMilliseconds);
                    if (delay > 0) await Task.Delay(delay, _cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_cancel.IsCancellationRequested) Error = error; }
        });
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel(); _pipe.Dispose();
        foreach (var source in _sources) { try { source.StopRecording(); } catch { } source.Dispose(); }
        foreach (var device in _selected) device.Dispose();
        _devices.Dispose();
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cancel.Dispose();
    }
}

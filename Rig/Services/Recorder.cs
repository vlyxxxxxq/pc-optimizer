using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rig.Services;

/// <summary>A screen area in physical pixels.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height);

/// <summary>
/// Records an area of the screen to MP4 (H.264 through Windows' own Media Foundation encoder, hardware
/// accelerated when the GPU offers it) or to an animated GIF. Video only, no sound.
/// </summary>
public sealed class ScreenRecorder
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    public const int MaxGifSeconds = 60;
    volatile bool _stop;
    Thread? _thread;
    readonly Stopwatch _clock = new();

    public bool IsRecording => _thread is not null;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? File { get; private set; }
    /// <summary>Raised on a background thread with the saved file, or null and the error.</summary>
    public event Action<string?, string?>? Finished;

    public static string Folder => Environment.GetEnvironmentVariable("RIG_TEST_RECORDINGS") is { Length: > 0 } test ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Rig");

    public void Start(ScreenArea area, bool gif, bool cursor, string label, int fps = 30)
    {
        if (_thread is not null) return;
        Directory.CreateDirectory(Folder);
        label = string.Concat(label.Split(Path.GetInvalidFileNameChars()));
        File = Path.Combine(Folder, $"{label} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.{(gif ? "gif" : "mp4")}");
        // The H.264 encoder needs even sizes; GIFs are scaled down to keep files reasonable.
        area = area with { Width = Math.Max(2, area.Width & ~1), Height = Math.Max(2, area.Height & ~1) };
        _stop = false;
        _thread = new Thread(() => Run(area, gif, cursor, gif ? 15 : fps, File)) { IsBackground = true, Name = "Screen recorder" };
        _thread.Start();
    }

    public void Stop() => _stop = true;

    /// <summary>
    /// Stops and waits until the file is finished (an MP4 is only playable once its index is written when the writer
    /// closes). Used on exit, restart, update and uninstall. False if it didn't finish in time.
    /// </summary>
    public bool StopAndWait(TimeSpan timeout)
    {
        var thread = _thread;
        _stop = true;
        return thread is null || thread.Join(timeout);
    }

    /// <summary>Recordings that failed to finish are written down here, since there may be no window left to say so.</summary>
    static void LogProblem(string file, string error)
    {
        try
        {
            var log = AppSettings.DataFile(Path.Combine("logs", "recordings.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            System.IO.File.AppendAllText(log, $"{DateTime.Now:s} {file}: {error}{Environment.NewLine}");
        }
        catch { }
    }

    void Run(ScreenArea area, bool gif, bool cursor, int fps, string file)
    {
        string? error = null;
        timeBeginPeriod(1);
        try
        {
            if (gif) RecordGif(area, cursor, fps, file);
            else RecordMp4(area, cursor, fps, file);
        }
        catch (Exception ex)
        {
            // Keep whatever was written: a partial file may still be useful or recoverable.
            error = ex.Message;
            LogProblem(file, ex.Message);
            try { if (new FileInfo(file) is { Exists: true, Length: 0 } empty) empty.Delete(); } catch { }
        }
        finally
        {
            timeEndPeriod(1);
            _clock.Reset();
            _thread = null;
        }
        Finished?.Invoke(error is null && System.IO.File.Exists(file) ? file : null, error);
    }

    /// <summary>Calls grab once per frame until stopped, skipping frames rather than falling behind.</summary>
    void Loop(int fps, Action<long> grab, TimeSpan? limit = null)
    {
        long interval = TimeSpan.TicksPerSecond / fps;
        _clock.Restart();
        long next = 0;
        while (!_stop && (limit is null || _clock.Elapsed < limit))
        {
            grab(_clock.Elapsed.Ticks);
            next += interval;
            long now = _clock.Elapsed.Ticks;
            if (next < now) next = now - now % interval + interval;
            int wait = (int)((next - now) / TimeSpan.TicksPerMillisecond);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    void RecordMp4(ScreenArea area, bool cursor, int fps, string file)
    {
        using var grabber = new ScreenGrabber(area, area.Width, area.Height, cursor);
        using var writer = new Mp4Writer(file, area.Width, area.Height, fps);
        long frame = TimeSpan.TicksPerSecond / fps;
        Loop(fps, t => { grabber.Grab(); writer.Write(grabber.Bits, t, frame); });
    }

    void RecordGif(ScreenArea area, bool cursor, int fps, string file)
    {
        // At most 960 px wide: screen recordings as GIFs get huge otherwise.
        double scale = Math.Min(1, 960.0 / area.Width);
        int w = Math.Max(2, (int)(area.Width * scale)), h = Math.Max(2, (int)(area.Height * scale));
        var frames = new BlockingCollection<(byte[] Pixels, long Time)>(boundedCapacity: 240);
        var encoder = new Thread(() =>
        {
            using var gif = new GifWriter(file, w, h);
            byte[]? previous = null;
            long previousTime = 0;
            foreach (var (pixels, time) in frames.GetConsumingEnumerable())
            {
                if (previous is not null && !pixels.AsSpan().SequenceEqual(previous))
                {
                    gif.AddFrame(previous, time - previousTime);
                    previous = null;
                }
                if (previous is null) { previous = pixels; previousTime = time; }
            }
            if (previous is not null) gif.AddFrame(previous, Math.Max(TimeSpan.TicksPerSecond / fps, _clock.Elapsed.Ticks - previousTime));
        }) { IsBackground = true, Name = "GIF encoder" };
        encoder.SetApartmentState(ApartmentState.STA);
        encoder.Start();
        try
        {
            using var grabber = new ScreenGrabber(area, w, h, cursor);
            int size = w * h * 4;
            Loop(fps, t =>
            {
                grabber.Grab();
                var pixels = new byte[size];
                Marshal.Copy(grabber.Bits, pixels, 0, size);
                frames.Add((pixels, t));
            }, TimeSpan.FromSeconds(MaxGifSeconds));
        }
        finally
        {
            frames.CompleteAdding();
            encoder.Join();
        }
    }
}

/// <summary>Copies an area of the screen (plus the cursor) into a top-down 32-bit DIB, optionally scaled.</summary>
sealed class ScreenGrabber : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);

    const uint SRCCOPY = 0x00CC0020;
    readonly ScreenArea _area;
    readonly int _w, _h;
    readonly bool _cursor, _scaled;
    readonly IntPtr _screen, _mem, _dib, _old;
    readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = [];

    public IntPtr Bits { get; }

    public ScreenGrabber(ScreenArea area, int width, int height, bool cursor)
    {
        _area = area; _w = width; _h = height; _cursor = cursor;
        _scaled = width != area.Width || height != area.Height;
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        _dib = CreateDIBSection(_screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("Couldn't set up screen capture.");
        Bits = bits;
        _old = SelectObject(_mem, _dib);
        if (_scaled) { SetStretchBltMode(_mem, 4); SetBrushOrgEx(_mem, 0, 0, IntPtr.Zero); } // HALFTONE: smooth downscaling
    }

    public void Grab()
    {
        if (_scaled) StretchBlt(_mem, 0, 0, _w, _h, _screen, _area.X, _area.Y, _area.Width, _area.Height, SRCCOPY);
        else BitBlt(_mem, 0, 0, _w, _h, _screen, _area.X, _area.Y, SRCCOPY);
        if (_cursor) DrawCursor();
        GdiFlush();
    }

    void DrawCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return; // hidden, e.g. in a game
        if (!_hotspots.TryGetValue(ci.hCursor, out var hot))
        {
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hot = (ii.xHotspot, ii.yHotspot);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            _hotspots[ci.hCursor] = hot;
        }
        double sx = (double)_w / _area.Width, sy = (double)_h / _area.Height;
        int size = (int)(32 * sx);
        DrawIconEx(_mem, (int)((ci.pt.X - _area.X - hot.X) * sx), (int)((ci.pt.Y - _area.Y - hot.Y) * sy), ci.hCursor,
            _scaled ? size : 0, _scaled ? size : 0, 0, IntPtr.Zero, 3); // DI_NORMAL
    }

    public void Dispose()
    {
        SelectObject(_mem, _old);
        DeleteObject(_dib);
        DeleteDC(_mem);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// <summary>
/// H.264 MP4 through Media Foundation's sink writer, which picks the GPU encoder when there is one and
/// converts our RGB frames itself. Called through the COM vtables directly to avoid any extra packages.
/// </summary>
unsafe sealed class Mp4Writer : IDisposable
{
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attributes, uint size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, IntPtr stream, IntPtr attributes, out IntPtr writer);

    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0"),
        Video = new("73646976-0000-0010-8000-00AA00389B71"), H264 = new("34363248-0000-0010-8000-00AA00389B71"),
        Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");

    readonly IntPtr _writer;
    readonly uint _stream;
    readonly int _length;
    bool _finished;

    static void* Fn(IntPtr p, int slot) => (*(void***)p)[slot];
    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    static void SetGuid(IntPtr a, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(a, 24))(a, &key, &value));
    static void SetUInt32(IntPtr a, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Fn(a, 21))(a, &key, value));
    static void SetUInt64(IntPtr a, Guid key, ulong value) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Fn(a, 22))(a, &key, value));

    public Mp4Writer(string file, int width, int height, int fps)
    {
        Check(MFStartup(0x20070, 0));
        _length = width * height * 4;
        try { _writer = Open(file, width, height, fps, hardware: true, out _stream); }
        catch { _writer = Open(file, width, height, fps, hardware: false, out _stream); } // some drivers refuse odd sizes
    }

    static IntPtr Open(string file, int width, int height, int fps, bool hardware, out uint stream)
    {
        IntPtr attrs = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero, writer = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attrs, 1));
            SetUInt32(attrs, HardwareTransforms, hardware ? 1u : 0u);
            Check(MFCreateSinkWriterFromURL(file, IntPtr.Zero, attrs, out writer));

            ulong size = ((ulong)width << 32) | (uint)height, rate = ((ulong)fps << 32) | 1, square = (1UL << 32) | 1;
            Check(MFCreateMediaType(out output));
            SetGuid(output, MajorType, Video);
            SetGuid(output, SubType, H264);
            // Roughly 0.15 bits per pixel per frame: crisp text without huge files (about 9 Mbps at 1080p30).
            SetUInt32(output, AvgBitrate, (uint)Math.Clamp(width * height * fps * 0.15, 2_000_000, 40_000_000));
            SetUInt32(output, InterlaceMode, 2); // progressive
            SetUInt64(output, FrameSize, size);
            SetUInt64(output, FrameRate, rate);
            SetUInt64(output, PixelAspect, square);
            uint index;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Fn(writer, 3))(writer, output, &index)); // AddStream

            Check(MFCreateMediaType(out input));
            SetGuid(input, MajorType, Video);
            SetGuid(input, SubType, Rgb32);
            SetUInt32(input, InterlaceMode, 2);
            SetUInt32(input, DefaultStride, (uint)(width * 4)); // positive: top-down rows, as the screen DIB has them
            SetUInt64(input, FrameSize, size);
            SetUInt64(input, FrameRate, rate);
            SetUInt64(input, PixelAspect, square);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Fn(writer, 4))(writer, index, input, IntPtr.Zero)); // SetInputMediaType
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(writer, 5))(writer)); // BeginWriting
            stream = index;
            var result = writer;
            writer = IntPtr.Zero;
            return result;
        }
        finally
        {
            foreach (var p in new[] { attrs, output, input, writer }) if (p != IntPtr.Zero) Marshal.Release(p);
        }
    }

    /// <summary>Writes one frame; time and duration in 100 ns units.</summary>
    public void Write(IntPtr pixels, long time, long duration)
    {
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)_length, out buffer));
            byte* data; uint max, current;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Fn(buffer, 3))(buffer, &data, &max, &current)); // Lock
            Buffer.MemoryCopy((void*)pixels, data, max, _length);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(buffer, 4))(buffer)); // Unlock
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Fn(buffer, 6))(buffer, (uint)_length)); // SetCurrentLength

            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(sample, 42))(sample, buffer)); // AddBuffer
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Fn(sample, 36))(sample, time)); // SetSampleTime
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Fn(sample, 38))(sample, duration)); // SetSampleDuration
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Fn(_writer, 6))(_writer, _stream, sample)); // WriteSample
        }
        finally
        {
            if (sample != IntPtr.Zero) Marshal.Release(sample);
            if (buffer != IntPtr.Zero) Marshal.Release(buffer);
        }
    }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        try { Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(_writer, 11))(_writer)); } // Finalize: writes the MP4 index
        finally
        {
            Marshal.Release(_writer);
            MFShutdown();
        }
    }
}

/// <summary>
/// Animated GIF: WPF's encoder makes each frame (with a palette picked from that frame's own colours), and the
/// frames are stitched into one looping file with their real timings.
/// </summary>
sealed class GifWriter : IDisposable
{
    readonly FileStream _out;
    readonly int _w, _h;
    byte[]? _last;

    public GifWriter(string file, int width, int height)
    {
        _w = width; _h = height;
        _out = new FileStream(file, FileMode.Create, FileAccess.Write);
        var header = new List<byte>("GIF89a"u8.ToArray());
        header.AddRange(BitConverter.GetBytes((ushort)width));
        header.AddRange(BitConverter.GetBytes((ushort)height));
        header.AddRange(new byte[] { 0, 0, 0 }); // no global palette: every frame brings its own
        header.AddRange(new byte[] { 0x21, 0xFF, 11 });
        header.AddRange("NETSCAPE2.0"u8.ToArray());
        header.AddRange(new byte[] { 3, 1, 0, 0, 0 }); // loop forever
        _out.Write(header.ToArray());
    }

    /// <summary>Adds a Bgr32 frame shown for the given time (100 ns units).</summary>
    public void AddFrame(byte[] pixels, long duration)
    {
        // Only the part that changed since the last frame is stored; the rest stays from before.
        var area = _last is null ? new System.Windows.Int32Rect(0, 0, _w, _h) : Changed(_last, pixels);
        _last = pixels;
        var src = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgr32, null, pixels, _w * 4);
        BitmapSource part = area.Width == _w && area.Height == _h ? src : new CroppedBitmap(src, area);
        var palette = new BitmapPalette(part, 256);
        var indexed = new FormatConvertedBitmap(part, PixelFormats.Indexed8, palette, 0);
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(indexed));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        int delay = (int)Math.Clamp(Math.Round(duration / 100_000.0), 2, 65535); // centiseconds
        _out.Write([0x21, 0xF9, 4, 0x04, (byte)delay, (byte)(delay >> 8), 0, 0]); // keep the previous frame underneath
        CopyImage(ms.ToArray(), area.X, area.Y);
    }

    /// <summary>The smallest rectangle containing every pixel that differs (at least 1×1).</summary>
    System.Windows.Int32Rect Changed(byte[] before, byte[] after)
    {
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(before);
        var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(after);
        int top = -1, bottom = -1, left = _w, right = -1;
        for (int y = 0; y < _h; y++)
        {
            var ra = a.Slice(y * _w, _w);
            var rb = b.Slice(y * _w, _w);
            if (ra.SequenceEqual(rb)) continue;
            if (top < 0) top = y;
            bottom = y;
            int l = 0; while (ra[l] == rb[l]) l++;
            int r = _w - 1; while (ra[r] == rb[r]) r--;
            left = Math.Min(left, l);
            right = Math.Max(right, r);
        }
        return top < 0 ? new System.Windows.Int32Rect(0, 0, 1, 1) : new System.Windows.Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>Copies the image block out of a one-frame GIF, moving its global palette onto the image.</summary>
    void CopyImage(byte[] gif, int x, int y)
    {
        int pos = 6;
        byte screenFlags = gif[pos + 4];
        pos += 7;
        byte[]? globalTable = null;
        int globalBits = screenFlags & 7;
        if ((screenFlags & 0x80) != 0)
        {
            int size = 3 << (globalBits + 1);
            globalTable = gif[pos..(pos + size)];
            pos += size;
        }
        while (pos < gif.Length)
        {
            byte block = gif[pos];
            if (block == 0x21) // extension: skip it and its sub-blocks
            {
                pos += 2;
                while (gif[pos] != 0) pos += gif[pos] + 1;
                pos++;
            }
            else if (block == 0x2C)
            {
                var descriptor = gif[pos..(pos + 10)];
                pos += 10;
                descriptor[1] = (byte)x; descriptor[2] = (byte)(x >> 8);
                descriptor[3] = (byte)y; descriptor[4] = (byte)(y >> 8);
                byte flags = descriptor[9];
                if ((flags & 0x80) == 0 && globalTable is not null)
                {
                    descriptor[9] = (byte)((flags & 0x40) | 0x80 | globalBits);
                    _out.Write(descriptor);
                    _out.Write(globalTable);
                }
                else
                {
                    _out.Write(descriptor);
                    if ((flags & 0x80) != 0)
                    {
                        int size = 3 << ((flags & 7) + 1);
                        _out.Write(gif, pos, size);
                        pos += size;
                    }
                }
                int start = pos++; // LZW minimum code size, then data sub-blocks up to the 0 terminator
                while (gif[pos] != 0) pos += gif[pos] + 1;
                pos++;
                _out.Write(gif, start, pos - start);
                return;
            }
            else break;
        }
        throw new InvalidDataException("Unexpected GIF frame.");
    }

    public void Dispose()
    {
        _out.WriteByte(0x3B);
        _out.Dispose();
    }
}

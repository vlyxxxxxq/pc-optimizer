using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public sealed class ClipEditorWindow : Window
{
    readonly string _source, _folder;
    readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Height = 340 };
    readonly Slider _position = new() { Minimum = 0, Margin = new Thickness(0, 10, 0, 6) };
    readonly Slider _in = new() { Minimum = 0 }, _out = new() { Minimum = 0 };
    readonly TextBlock _time = new(), _selection = new(), _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button _play = new() { Content = "Play", MinWidth = 80 }, _export = new() { Content = "Save trimmed clip" }, _gif = new() { Content = "Export GIF" }, _cancel = new() { Content = "Cancel export", Visibility = Visibility.Collapsed };
    readonly ProgressBar _progress = new() { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 8) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    MediaDetails? _details;
    CancellationTokenSource? _exportCancel;
    bool _playing, _seeking;
    public event Action<string>? Saved;
    public ClipEditorWindow(string source, string folder)
    {
        (_source, _folder) = (source, folder);
        Title = "Preview & trim · " + Path.GetFileName(source);
        Width = 900; Height = 760; MinWidth = 680; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(source), FontSize = 21, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = "Preview, choose the part to keep, and save a new copy. Your original stays intact.", Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border { Background = Brushes.Black, Child = _player, CornerRadius = new CornerRadius(10) });
        panel.Children.Add(_position);
        var playback = new WrapPanel(); playback.Children.Add(_play); playback.Children.Add(_time); _time.Margin = new Thickness(14, 8, 0, 0);
        var external = new Button { Content = "Open in default player", Margin = new Thickness(14, 0, 0, 0) }; external.Click += (_, _) => Shell.Open(source); playback.Children.Add(external); panel.Children.Add(playback);
        foreach (var (title, slider) in new[] { ("Trim start", _in), ("Trim end", _out) })
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 14, 0, 5) });
            System.Windows.Automation.AutomationProperties.SetName(slider, title); panel.Children.Add(slider);
        }
        panel.Children.Add(_selection); _selection.Margin = new Thickness(0, 8, 0, 12);
        var actions = new WrapPanel();
        foreach (var button in new[] { _export, _gif, _cancel }) { button.Margin = new Thickness(0, 0, 10, 0); actions.Children.Add(button); }
        panel.Children.Add(actions); panel.Children.Add(_progress); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(_position, "Playback position");
        _player.MediaFailed += (_, e) => { _status.Text = "Windows couldn't preview this video. You can open it in your player; trimming can still work. " + e.ErrorException.Message; };
        _player.MediaEnded += (_, _) => { _player.Pause(); _playing = false; _play.Content = "Play"; };
        _player.MediaOpened += (_, _) => { if (!_playing) _player.Pause(); };
        _play.Click += (_, _) =>
        {
            if (_playing) _player.Pause();
            else { if (_details is not null && _player.Position.TotalSeconds >= _details.Duration - .1) _player.Position = TimeSpan.Zero; _player.Play(); }
            _playing = !_playing; _play.Content = _playing ? "Pause" : "Play";
        };
        _position.PreviewMouseDown += (_, _) => _seeking = true;
        _position.PreviewMouseUp += (_, _) => { _player.Position = TimeSpan.FromSeconds(_position.Value); _seeking = false; };
        _position.PreviewKeyUp += (_, _) => _player.Position = TimeSpan.FromSeconds(_position.Value);
        _in.ValueChanged += (_, _) => SelectionChanged(); _out.ValueChanged += (_, _) => SelectionChanged();
        _export.Click += async (_, _) => await ExportAsync(false); _gif.Click += async (_, _) => await ExportAsync(true);
        _cancel.Click += (_, _) => _exportCancel?.Cancel();
        _clock.Tick += (_, _) =>
        {
            if (!_seeking) _position.Value = _player.Position.TotalSeconds;
            _time.Text = $"{_player.Position:mm\\:ss} / {TimeSpan.FromSeconds(_details?.Duration ?? 0):mm\\:ss}";
        };
        Loaded += async (_, _) =>
        {
            try
            {
                _details = await ReplayMedia.InspectAsync(source);
                _position.Maximum = _in.Maximum = _out.Maximum = _details.Duration; _out.Value = _details.Duration;
                _player.Source = new Uri(source); _player.Play(); _clock.Start(); SelectionChanged();
            }
            catch (Exception error) { _status.Text = error.Message; _export.IsEnabled = _gif.IsEnabled = false; }
        };
        Closed += (_, _) => { _exportCancel?.Cancel(); _clock.Stop(); _player.Close(); };
    }
    void SelectionChanged()
    {
        double duration = _out.Value - _in.Value;
        _selection.Text = duration > 0 ? $"Keep {_in.Value:0.0}s – {_out.Value:0.0}s · {duration:0.0} seconds" : "Trim end must be after trim start.";
        _export.IsEnabled = duration >= .1 && _exportCancel is null;
        _gif.IsEnabled = duration is >= .1 and <= 60 && _exportCancel is null;
        _gif.ToolTip = "GIFs have no sound and are limited to 60 seconds. Choose a shorter selection to enable.";
    }
    async Task ExportAsync(bool gif)
    {
        if (_exportCancel is not null || _out.Value <= _in.Value) return;
        _exportCancel = new CancellationTokenSource(); _export.IsEnabled = _gif.IsEnabled = false;
        _cancel.Visibility = _progress.Visibility = Visibility.Visible; _status.Text = "Saving a new copy…";
        try
        {
            string destination = Path.Combine(_folder, "Exports", Path.GetFileNameWithoutExtension(_source) + " trimmed " + Guid.NewGuid().ToString("N")[..6] + (gif ? ".gif" : ".mp4"));
            var file = await ReplayMedia.ExportAsync(_source, destination, _in.Value, _out.Value - _in.Value, 16, gif, _exportCancel.Token);
            _status.Text = "Saved " + Path.GetFileName(file); Saved?.Invoke(file);
        }
        catch (OperationCanceledException) { _status.Text = "Export cancelled. Your original is unchanged."; }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _exportCancel.Dispose(); _exportCancel = null; _cancel.Visibility = _progress.Visibility = Visibility.Collapsed; SelectionChanged(); }
    }
}

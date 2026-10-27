using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class HardwareView : UserControl
{
    bool _loaded;
    CancellationTokenSource? _cts;
    string _benchFolder = Path.GetTempPath();

    public HardwareView() => InitializeComponent();

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        // Disk choice: temp folder for the system drive, a root folder for other drives.
        var system = Path.GetPathRoot(Path.GetTempPath())!;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var folder = d.Name.Equals(system, StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : d.Name;
            var rb = new RadioButton { Content = d.Name.TrimEnd('\\'), Tag = folder, GroupName = "BenchDrive", Margin = new Thickness(0, 0, 8, 0),
                                       IsChecked = folder == _benchFolder };
            rb.Checked += (_, _) => _benchFolder = (string)rb.Tag;
            DriveChoice.Children.Add(rb);
        }
        RenderHistory();

        try
        {
            var report = await Hardware.LoadAsync();
            SpecList.ItemsSource = report.Sections;
            DriveList.ItemsSource = report.Drives;
            DrivesCard.Visibility = report.Drives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Couldn't read hardware info: {ex.Message}";
        }
    }

    void RenderHistory()
    {
        var history = Benchmark.History();
        HistoryList.ItemsSource = history.Take(5).ToList();
        HistoryTitle.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BenchButton.Content = "Cancel";
        BenchProgress.Visibility = Visibility.Visible;
        ScoreSingle.Text = ScoreMulti.Text = ScoreMem.Text = ScoreRead.Text = ScoreWrite.Text = "–";
        var previous = Benchmark.History().FirstOrDefault();
        var progress = new Progress<string>(s => BenchText.Text = s);
        try
        {
            BenchText.Text = "CPU: one core, then all cores…";
            var (single, multi) = await Task.Run(() => Benchmark.Cpu(ct), ct);
            ScoreSingle.Text = single.ToString("N0");
            ScoreMulti.Text = multi.ToString("N0");

            BenchText.Text = "Memory…";
            var mem = await Task.Run(() => Benchmark.Memory(ct), ct);
            ScoreMem.Text = mem.ToString("0.0");

            var folder = _benchFolder;
            var drive = Path.GetPathRoot(folder)!.TrimEnd('\\');
            var free = new DriveInfo(drive).AvailableFreeSpace;
            double read = 0, write = 0;
            if (free < 3L << 30)
                BenchText.Text = $"Skipped the disk test: {drive} has less than 3 GB free.";
            else
            {
                (read, write) = await Task.Run(() => Benchmark.Disk(folder, progress, ct), ct);
                ScoreRead.Text = read.ToString("N0");
                ScoreWrite.Text = write.ToString("N0");
            }

            var result = new BenchResult(DateTime.Now, single, multi, mem, read, write, drive);
            Benchmark.Save(result);
            BenchText.Text = previous is null
                ? "Done. Run it again after changes to see the difference."
                : $"Done. Compared with {previous.When}: CPU {Change(single, previous.CpuSingle)} single / {Change(multi, previous.CpuMulti)} multi, " +
                  $"memory {Change(mem, previous.MemoryGBs)}" +
                  (read > 0 && previous.DiskReadMBs > 0 && previous.Drive == drive ? $", disk read {Change(read, previous.DiskReadMBs)}" : "") + ".";
            RenderHistory();
        }
        catch (OperationCanceledException) { BenchText.Text = "Benchmark cancelled."; }
        catch (Exception ex) { BenchText.Text = $"Benchmark failed: {ex.Message}"; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BenchButton.Content = "Run benchmark";
            BenchProgress.Visibility = Visibility.Collapsed;
        }
    }

    static string Change(double now, double before) =>
        before <= 0 ? "n/a" : $"{(now - before) / before * 100:+0;-0;0}%";
}

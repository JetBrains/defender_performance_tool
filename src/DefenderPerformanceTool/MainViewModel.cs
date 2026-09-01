using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DefenderPerformanceTool.Mvvm;
using Lib;
using Microsoft.Win32;
using ScottPlot;

namespace DefenderPerformanceTool;

public class MainViewModel : ViewModelBase, IDisposable
{
    private const int TopN = 30;
    private const string DefaultWindowTitle = "Defender Performance Tool";

    // Stats dictionary bounds: pruned to the largest entries when exceeded, so long
    // sessions can't grow memory or RefreshStats() cost without limit.
    private const int MaxTrackedEntries = 10_000;
    private const int PruneToEntries = 1_000;

    // Central relay: both live and file listeners feed into this
    private readonly EventRelay _eventsRelay = new();

    // Event batching: the ETW processing thread appends here; a 250ms timer drains the
    // list on the UI thread. Without batching, RefreshStats() (O(n log n) sort +
    // ObservableCollection rebuild + tree rebuild) would fire for every event,
    // overwhelming the dispatcher queue under heavy scan load.
    private readonly List<EventInfo> _pendingEvents = new();
    private readonly DispatcherTimer _batchTimer;

    private readonly Plotter _plotter;
    private EtwListener? _liveListener;
    private EtwListener? _fileListener;
    private readonly DispatcherTimer _rawCountTimer;
    private readonly DispatcherTimer _cpuTimer;
    private TimeSpan? _cpuKernelBaseline;
    private TimeSpan? _cpuUserBaseline;

    // Stats (accessed only on the UI thread via the batch timer)
    private readonly Dictionary<string, double> _processTotals = new();
    private readonly Dictionary<string, double> _fileTotals = new();
    private double _totalScannedMs;

    private double _totalScannedSeconds;
    public double TotalScannedSeconds
    {
        get => _totalScannedSeconds;
        private set => Set(ref _totalScannedSeconds, value);
    }

    private long _totalEventsProcessed;
    public long TotalEventsProcessed
    {
        get => _totalEventsProcessed;
        private set => Set(ref _totalEventsProcessed, value);
    }

    private bool _cpuTimesAvailable;
    public bool CpuTimesAvailable
    {
        get => _cpuTimesAvailable;
        private set => Set(ref _cpuTimesAvailable, value);
    }

    private string _kernelTimeText = "";
    public string KernelTimeText
    {
        get => _kernelTimeText;
        private set => Set(ref _kernelTimeText, value);
    }

    private string _userTimeText = "";
    public string UserTimeText
    {
        get => _userTimeText;
        private set => Set(ref _userTimeText, value);
    }

    private string _totalCpuTimeText = "";
    public string TotalCpuTimeText
    {
        get => _totalCpuTimeText;
        private set => Set(ref _totalCpuTimeText, value);
    }

    private string _cpuStatusMessage = "";
    public string CpuStatusMessage
    {
        get => _cpuStatusMessage;
        private set => Set(ref _cpuStatusMessage, value);
    }

    private string? _cpuStatusTooltip;
    public string? CpuStatusTooltip
    {
        get => _cpuStatusTooltip;
        private set => Set(ref _cpuStatusTooltip, value);
    }

    private string _snapshotName = "";
    public string SnapshotName
    {
        get => _snapshotName;
        private set
        {
            if (Set(ref _snapshotName, value))
                WindowTitle = string.IsNullOrEmpty(value) ? DefaultWindowTitle : $"{DefaultWindowTitle} — {value}";
        }
    }

    private string _windowTitle = DefaultWindowTitle;
    public string WindowTitle
    {
        get => _windowTitle;
        private set => Set(ref _windowTitle, value);
    }

    public bool IsRunningAsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsNotRunningAsAdmin => !IsRunningAsAdmin;

    public ImageSource? UacShieldIcon { get; } = LoadUacShieldIcon();

    public ObservableCollection<ScanStat> TopProcesses { get; } = new ObservableCollection<ScanStat>();

    // Pause-on-hover for the top-processes grid (same behavior as the treemap): while
    // the view reports the mouse over the grid, list refreshes are deferred so the rows
    // don't change under the cursor, and the latest refresh runs when it leaves.
    private bool _topProcessesPaused;
    private bool _topProcessesRefreshPending;

    public bool TopProcessesPaused
    {
        get => _topProcessesPaused;
        set
        {
            var wasPaused = _topProcessesPaused;
            Set(ref _topProcessesPaused, value);
            if (wasPaused && !value && _topProcessesRefreshPending)
            {
                _topProcessesRefreshPending = false;
                SyncTopProcesses();
            }
        }
    }

    private ScanTreeNode? _filesTreeRoot;
    /// <summary>Root of the scanned-files directory tree that feeds the treemap view.</summary>
    public ScanTreeNode? FilesTreeRoot
    {
        get => _filesTreeRoot;
        private set => Set(ref _filesTreeRoot, value);
    }

    // Exposes the ScottPlot control for the View
    public WpfPlot PlotControl => _plotter.WpfPlot;

    public RelayCommand CopyHumanReadableCommand { get; }
    public RelayCommand CopyJsonCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenEtlFileCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand AddProcessExclusionCommand { get; }
    public RelayCommand OpenExclusionManagerCommand { get; }

    public MainViewModel(bool startLiveMonitoring = true)
    {
        _plotter = new Plotter(_eventsRelay);
        _eventsRelay.Received += OnEventReceived;

        _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _batchTimer.Tick += OnBatchTimerTick;
        _batchTimer.Start();

        _rawCountTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rawCountTimer.Tick += (_, __) =>
        {
            var source = _fileListener ?? _liveListener;
            if (source is not null)
                TotalEventsProcessed = source.RawEventCount;
        };
        _rawCountTimer.Start();

        // Live monitoring requires admin
        if (startLiveMonitoring && IsRunningAsAdmin)
        {
            _liveListener = new EtwListener();
            _liveListener.EventReceived += _eventsRelay.Publish;
            _liveListener.Start();
        }

        CopyHumanReadableCommand = new RelayCommand(CopyHumanReadable);
        CopyJsonCommand = new RelayCommand(CopyJson);
        ResetCommand = new RelayCommand(Reset);
        OpenEtlFileCommand = new RelayCommand(OpenEtlFile);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        AddProcessExclusionCommand = new RelayCommand(p => AddProcessExclusion((string)p!));
        OpenExclusionManagerCommand = new RelayCommand(OpenExclusionManager);

        _cpuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cpuTimer.Tick += (_, __) => PollCpuTimes();
        _cpuTimer.Start();
        PollCpuTimes();
    }

    /// <summary>Adds a Defender exclusion for the given process, asking for confirmation first.</summary>
    public void AddProcessExclusion(string processName) =>
        DefenderExclusions.AddProcessExclusion(processName);

    private void OpenExclusionManager()
    {
        var vm = new ExclusionManagerViewModel();
        var window = new ExclusionManagerWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    // Called on the ETW processing thread — only appends under lock.
    private void OnEventReceived(EventInfo info)
    {
        lock (_pendingEvents)
            _pendingEvents.Add(info);
    }

    private void OnBatchTimerTick(object? sender, EventArgs e)
    {
        List<EventInfo> batch;
        lock (_pendingEvents)
        {
            if (_pendingEvents.Count == 0) return;
            batch = new List<EventInfo>(_pendingEvents);
            _pendingEvents.Clear();
        }
        OnEventBatch(batch);
    }

    private void OnEventBatch(List<EventInfo> batch)
    {
        foreach (var info in batch)
        {
            _totalScannedMs += info.DurationMsec;

            if (!string.IsNullOrEmpty(info.Process))
            {
                _processTotals.TryGetValue(info.Process, out var existing);
                _processTotals[info.Process] = existing + info.DurationMsec;
            }

            if (!string.IsNullOrEmpty(info.FilePath))
            {
                // ETW reports NT device paths (\Device\HarddiskVolume3\…) — normalize to
                // DOS paths so the treemap actions (Explorer, exclusions) can use them.
                var filePath = DevicePathConverter.ToDosPath(info.FilePath);
                _fileTotals.TryGetValue(filePath, out var existing);
                _fileTotals[filePath] = existing + info.DurationMsec;
            }
        }

        PruneIfNeeded(_processTotals);
        PruneIfNeeded(_fileTotals);

        TotalScannedSeconds = _totalScannedMs / 1000.0;
        RefreshStats();
    }

    private static void PruneIfNeeded(Dictionary<string, double> totals)
    {
        if (totals.Count <= MaxTrackedEntries) return;

        var survivors = totals.OrderByDescending(kvp => kvp.Value)
                              .Take(PruneToEntries)
                              .ToList();
        totals.Clear();
        foreach (var kvp in survivors)
            totals[kvp.Key] = kvp.Value;
    }

    private void PollCpuTimes()
    {
        switch (MsMpEngCpuInfo.Query())
        {
            case CpuTimesSuccess s:
                if (_cpuKernelBaseline == null)
                {
                    _cpuKernelBaseline = s.KernelTime;
                    _cpuUserBaseline = s.UserTime;
                }
                var kernel = s.KernelTime - _cpuKernelBaseline.Value;
                var user = s.UserTime - _cpuUserBaseline!.Value;
                KernelTimeText = Util.FormatTime(kernel);
                UserTimeText = Util.FormatTime(user);
                TotalCpuTimeText = Util.FormatTime(kernel + user);
                CpuStatusTooltip = null;
                CpuTimesAvailable = true;
                break;
            case CpuNotRunning:
                CpuStatusMessage = "Microsoft Defender is not running";
                CpuStatusTooltip = null;
                CpuTimesAvailable = false;
                break;
            case CpuError e:
                CpuStatusMessage = $"Unable to query CPU counters: {e.Message}";
                CpuStatusTooltip = e.Source.ToString();
                CpuTimesAvailable = false;
                break;
        }
    }

    private void RefreshStats()
    {
        if (TopProcessesPaused)
            _topProcessesRefreshPending = true;
        else
            SyncTopProcesses();

        FilesTreeRoot = ScanTreeNode.Build(_fileTotals);
    }

    private void SyncTopProcesses() =>
        SyncCollection(TopProcesses,
            _processTotals
                .OrderByDescending(kvp => kvp.Value)
                .Take(TopN)
                .Select(kvp => new ScanStat(kvp.Key, kvp.Value / 1000.0)));

    private static void SyncCollection(ObservableCollection<ScanStat> collection, IEnumerable<ScanStat> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

    private void Reset()
    {
        _processTotals.Clear();
        _fileTotals.Clear();
        _totalScannedMs = 0;
        TotalScannedSeconds = 0;
        TotalEventsProcessed = 0;
        TopProcesses.Clear();
        _topProcessesRefreshPending = false;
        FilesTreeRoot = null;
        _plotter.Reset();
        _cpuKernelBaseline = null;
        _cpuUserBaseline = null;
        PollCpuTimes();
    }

    private void CopyHumanReadable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Microsoft Defender Scan Statistics");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total Scanned Time: {TotalScannedSeconds:F2}s");
        sb.AppendLine();
        sb.AppendLine("Top Processes:");
        foreach (var stat in TopProcesses)
            sb.AppendLine($"  {stat.Name}: {stat.TotalSeconds:F2}s");
        sb.AppendLine();
        sb.AppendLine("Top Files:");
        foreach (var kvp in _fileTotals.OrderByDescending(kvp => kvp.Value).Take(TopN))
            sb.AppendLine($"  {kvp.Key}: {kvp.Value / 1000.0:F2}s");
        Clipboard.SetText(sb.ToString());
    }

    private void CopyJson()
    {
        static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"generatedAt\": ").Append(SimpleJson.String(DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"))).AppendLine(",");
        sb.Append("  \"totalScannedSeconds\": ").Append(Number(TotalScannedSeconds)).AppendLine(",");

        sb.AppendLine("  \"topProcesses\": [");
        var processes = TopProcesses.ToList();
        for (int i = 0; i < processes.Count; i++)
        {
            var s = processes[i];
            sb.Append("    { \"name\": ").Append(SimpleJson.String(s.Name))
              .Append(", \"totalSeconds\": ").Append(Number(s.TotalSeconds)).Append(" }")
              .AppendLine(i < processes.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"topFiles\": [");
        var files = _fileTotals.OrderByDescending(kvp => kvp.Value).Take(TopN).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            var kvp = files[i];
            sb.Append("    { \"path\": ").Append(SimpleJson.String(kvp.Key))
              .Append(", \"totalSeconds\": ").Append(Number(kvp.Value / 1000.0)).Append(" }")
              .AppendLine(i < files.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ]");

        sb.AppendLine("}");
        Clipboard.SetText(sb.ToString());
    }

    private void OpenEtlFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ETL files (*.etl)|*.etl|All files (*.*)|*.*",
            Title = "Open ETL Recording"
        };
        if (dialog.ShowDialog() != true) return;

        LoadEtlFile(dialog.FileName);
    }

    public void LoadEtlFile(string filePath)
    {
        if (_fileListener is not null)
        {
            _fileListener.EventReceived -= _eventsRelay.Publish;
            _fileListener.Dispose();
        }

        Reset();

        SnapshotName = Path.GetFileName(filePath);

        _fileListener = new EtwListener(filePath);
        _fileListener.EventReceived += _eventsRelay.Publish;
        _fileListener.Start();
    }

    private static void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC prompt — stay running
        }
    }

    // --- UAC shield icon via SHGetStockIconInfo ---

    private const uint SIID_SHIELD = 77;
    private const uint SHGSI_ICON = 0x100;
    private const uint SHGSI_SMALLICON = 0x1;

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    private static ImageSource? LoadUacShieldIcon()
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(SIID_SHIELD, SHGSI_ICON | SHGSI_SMALLICON, ref info) != 0)
            return null;
        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cpuTimer.Stop();
        _batchTimer.Stop();
        _rawCountTimer.Stop();

        _eventsRelay.Received -= OnEventReceived;

        if (_liveListener is not null)
        {
            _liveListener.EventReceived -= _eventsRelay.Publish;
            _liveListener.Dispose();
        }
        if (_fileListener is not null)
        {
            _fileListener.EventReceived -= _eventsRelay.Publish;
            _fileListener.Dispose();
        }

        _plotter.Dispose();
    }
}

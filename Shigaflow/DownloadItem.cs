using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace Shigaflow;

public sealed class SegmentItem : INotifyPropertyChanged
{
    private double _progress;
    public int Index { get; init; }
    public string Label => $"Part {Index}";
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); } }
    public string ProgressText => $"{Progress:0}%";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _name;
    private string _url;
    private string _status = "Queued";
    private string _resume = "Checking…";
    private double _progress;
    private string _detail = "Waiting to start";
    private string _speed = "—";
    private bool _isBusy;
    private bool _isBulkSelected;
    private bool _isSelectionMode;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }
    public string Status
    {
        get => _status;
        set { _status = value; if (!IsBulkSelectable) _isBulkSelected = false; OnPropertyChanged(); OnPropertyChanged(nameof(PauseResumeLabel)); OnPropertyChanged(nameof(PauseResumeGlyph)); OnPropertyChanged(nameof(PauseResumeText)); OnPropertyChanged(nameof(IsBulkSelectable)); OnPropertyChanged(nameof(IsBulkSelected)); OnPropertyChanged(nameof(ShowBulkCheckbox)); OnPropertyChanged(nameof(IsComplete)); OnPropertyChanged(nameof(StatusBarText)); OnPropertyChanged(nameof(ShowSegments)); OnPropertyChanged(nameof(ShowSingleBar)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(DownloadedText)); }
    }
    public bool IsComplete => Status == "Complete";
    public string PauseResumeLabel => Status is "Paused" or "Cancelled" or "Failed" ? "▶  Resume" : "Ⅱ  Pause";
    public string PauseResumeGlyph => Status is "Paused" or "Cancelled" or "Failed" ? "\uE768" : "\uE769";
    public string PauseResumeText => Status is "Paused" or "Cancelled" or "Failed" ? "Resume" : "Pause";
    public string Resume { get => _resume; set { _resume = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionsText)); } }
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StatusBarText)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(DownloadedText)); OnPropertyChanged(nameof(DownloadedBytesSort)); } }
    public string ProgressText => $"{Progress:0}%";

    // Text drawn on top of the thick status bar: state plus percentage.
    public string StatusBarText => Status switch
    {
        "Downloading" => $"Downloading · {Progress:0}%",
        "Paused" => $"Paused · {Progress:0}%",
        "Cancelled" => Progress > 0 ? $"Cancelled · {Progress:0}%" : "Cancelled",
        "Failed" => Progress > 0 ? $"Failed · {Progress:0}%" : "Failed",
        "Complete" => "Complete",
        _ => Status
    };

    // While a multi-connection download is in progress the status bar is drawn as one
    // bar per parallel part. Once complete it becomes a single full bar.
    public bool ShowSegments => Segments.Count > 1 && !IsComplete;
    public bool ShowSingleBar => !ShowSegments;

    // Format requested for the table: HH:mm, dd/MM/yyyy
    public string AddedDateText => DateAdded.ToString("HH:mm, dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // ---- Details panel (shown when a download row is left-clicked) ----
    // Speed is kept as display text ("1,5 MB/s", "—"); turn it back into bytes per second for the ETA.
    private double SpeedBytesPerSecond
    {
        get
        {
            var parts = (Speed ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[0], out double value)) return 0;
            double multiplier = parts[1].ToUpperInvariant() switch
            {
                "KB/S" => 1024d,
                "MB/S" => 1024d * 1024d,
                "GB/S" => 1024d * 1024d * 1024d,
                _ => 1d
            };
            return value * multiplier;
        }
    }

    private long DownloadedEstimate =>
        IsComplete && TotalBytes.HasValue ? TotalBytes.Value
        : DownloadedBytes > 0 ? DownloadedBytes
        : (TotalBytes is > 0 && Progress > 0 ? (long)(TotalBytes.Value * Math.Clamp(Progress, 0, 100) / 100d) : 0);

    public string DownloadedText => DownloadedEstimate > 0 ? FormatSize(DownloadedEstimate) : "—";

    public string EtaText
    {
        get
        {
            if (IsComplete) return "Done";
            double speed = SpeedBytesPerSecond;
            if (Status != "Downloading" || speed <= 0 || !(TotalBytes is > 0)) return "—";
            double remaining = Math.Max(0, TotalBytes.Value - DownloadedEstimate);
            var t = TimeSpan.FromSeconds(Math.Min(remaining / speed, 99d * 24 * 3600));
            return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
                 : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                 : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
                 : $"{Math.Max(1, (int)t.TotalSeconds)}s";
        }
    }

    // Parallel connections actually in use: the number of parts while segmented, otherwise the setting.
    public string ConnectionsText => Segments.Count > 1 ? Segments.Count.ToString()
        : (Resume == "No" ? "1" : ConnectionCount.ToString());

    public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(); } }
    public string Speed { get => _speed; set { _speed = value; OnPropertyChanged(); OnPropertyChanged(nameof(EtaText)); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; if (!IsBulkSelectable) _isBulkSelected = false; OnPropertyChanged(); OnPropertyChanged(nameof(IsBulkSelectable)); OnPropertyChanged(nameof(IsBulkSelected)); OnPropertyChanged(nameof(ShowBulkCheckbox)); OnPropertyChanged(nameof(IsComplete)); } }
    public bool IsBulkSelectable => !IsBusy && Status != "Paused";
    public bool IsBulkSelected
    {
        get => _isBulkSelected;
        set { _isBulkSelected = IsBulkSelectable && value; OnPropertyChanged(); }
    }
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set { _isSelectionMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowBulkCheckbox)); }
    }
    public bool ShowBulkCheckbox => IsSelectionMode && IsBulkSelectable;
    public string DestinationPath { get; set; } = "";
    private DateTime _dateAdded = DateTime.Now;
    public DateTime DateAdded { get => _dateAdded; set { _dateAdded = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddedDateText)); } }
    private long? _totalBytes;
    private long _downloadedBytes;
    public long DownloadedBytes { get => _downloadedBytes; set { _downloadedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadedBytesSort)); OnPropertyChanged(nameof(DownloadedText)); OnPropertyChanged(nameof(EtaText)); } }
    public long? TotalBytes { get => _totalBytes; set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalBytesSort)); OnPropertyChanged(nameof(TotalSizeText)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(DownloadedText)); } }
    public long TotalBytesSort => TotalBytes ?? long.MaxValue;
    public string TotalSizeText => TotalBytes.HasValue ? FormatSize(TotalBytes.Value) : "Unknown";
    public long DownloadedBytesSort => DownloadedBytes;
    private static string FormatSize(long bytes) { string[] u = ["B", "KB", "MB", "GB", "TB"]; double n = bytes; int i = 0; while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; } return $"{n:0.0} {u[i]}"; }
    public ObservableCollection<SegmentItem> Segments { get; } = new();
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Worker { get; set; }
    public int ConnectionCount { get; set; } = 8;
    internal List<BrowserCookie>? BrowserCookies { get; set; }
    internal readonly object Gate = new();
    internal bool PauseRequested;
    internal TaskCompletionSource? ResumeSignal;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public DownloadItem(string name, string url)
    {
        _name = name; _url = url;
        Segments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ShowSegments)); OnPropertyChanged(nameof(ShowSingleBar)); OnPropertyChanged(nameof(ConnectionsText)); };
    }
}

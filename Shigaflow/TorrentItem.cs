using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shigaflow;

public sealed class TorrentItem : INotifyPropertyChanged
{
    private string _name;
    private string _status = "Queued";
    private string _detail = "Waiting to start";
    private double _progress;
    private long _size;
    private long _downloadSpeed;
    private long _uploadSpeed;
    private bool _isComplete;
    private bool _isBulkSelected;
    private bool _isSelectionMode;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Source { get; set; } = "";
    public string SavePath { get; set; } = "";
    public string Status { get => _status; set { _status = value; if (!IsBulkSelectable) _isBulkSelected = false; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBarText)); OnPropertyChanged(nameof(StatusDetailText)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(PauseResumeLabel)); OnPropertyChanged(nameof(PauseResumeGlyph)); OnPropertyChanged(nameof(PauseResumeText)); OnPropertyChanged(nameof(IsBulkSelectable)); OnPropertyChanged(nameof(IsBulkSelected)); OnPropertyChanged(nameof(ShowBulkCheckbox)); } }
    public string Detail { get => _detail; set { _detail = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDetailText)); } }
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StatusBarText)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(DownloadedText)); OnPropertyChanged(nameof(RatioText)); } }
    public string ProgressText => $"{Progress:0.0}%";
    public string StatusBarText => Status switch
    {
        "Downloading Metadata" => "Downloading Metadata",
        "Downloading" => $"Downloading · {Progress:0}%",
        "Seeding" => "Seeding",
        "Checking" => "Checking",
        "Paused" => "Paused",
        "Stopped" => "Stopped",
        "Error" => "Error",
        "Queued" => "Queued",
        "Restoring" => "Restoring",
        _ => Status
    };
    public string StatusDetailText => IsComplete && Status == "Seeding" ? Detail : string.Empty;
    public long Size { get => _size; set { _size = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(DownloadedText)); } }
    public string SizeText => Size > 0 ? FormatBytes(Size) : "—";
    public long DownloadSpeed { get => _downloadSpeed; set { _downloadSpeed = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadSpeedText)); OnPropertyChanged(nameof(EtaText)); } }
    public string DownloadSpeedText => DownloadSpeed > 0 ? FormatRate(DownloadSpeed) : "—";
    public long UploadSpeed { get => _uploadSpeed; set { _uploadSpeed = value; OnPropertyChanged(); OnPropertyChanged(nameof(UploadSpeedText)); } }
    public string UploadSpeedText => UploadSpeed > 0 ? FormatRate(UploadSpeed) : "—";
    public bool IsComplete { get => _isComplete; set { _isComplete = value; OnPropertyChanged(); OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(StatusDetailText)); OnPropertyChanged(nameof(IsBulkSelectable)); OnPropertyChanged(nameof(IsBulkSelected)); OnPropertyChanged(nameof(ShowBulkCheckbox)); } }
    // Every torrent row can be selected, including active downloads and metadata lookups.
    // Removing an active torrent first detaches it from the engine in MainWindow.
    public bool IsBulkSelectable => true;
    public bool IsBulkSelected
    {
        get => _isBulkSelected;
        set { _isBulkSelected = value; OnPropertyChanged(); }
    }
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set { _isSelectionMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowBulkCheckbox)); }
    }
    public bool ShowBulkCheckbox => IsSelectionMode;
    public double SessionStartProgress { get; set; }
    public string PauseResumeLabel => Status is "Paused" or "Stopped" or "Queued" or "Error" ? "▶  Resume" : "Ⅱ  Pause";
    public string PauseResumeGlyph => Status is "Paused" or "Stopped" or "Queued" or "Error" ? "\uE768" : "\uE769";
    public string PauseResumeText => Status is "Paused" or "Stopped" or "Queued" or "Error" ? "Resume" : "Pause";
    public TorrentTransfer? Manager { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public string AddedDateText => AddedAt.ToString("dd MMM yyyy");


    // ---- Details panel (shown when a torrent row is left-clicked) ----
    private long _downloadedBytes;
    private long? _uploadedBytes;
    private int _peerCount;
    private int? _seedCount;
    public long DownloadedBytes { get => _downloadedBytes; set { _downloadedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadedText)); OnPropertyChanged(nameof(RatioText)); } }
    public long? UploadedBytes { get => _uploadedBytes; set { _uploadedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(UploadedText)); OnPropertyChanged(nameof(RatioText)); } }
    public int PeerCount { get => _peerCount; set { _peerCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeersText)); } }
    public int? SeedCount { get => _seedCount; set { _seedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(SeedsText)); } }

    // Best-known downloaded amount: the engine's counter, or derived from size x progress.
    private long DownloadedEstimate => DownloadedBytes > 0 ? DownloadedBytes
        : (Size > 0 && Progress > 0 ? (long)(Size * Math.Clamp(Progress, 0, 100) / 100d) : 0);
    public string DownloadedText => DownloadedEstimate > 0 ? FormatBytes(DownloadedEstimate) : "—";
    public string UploadedText => UploadedBytes.HasValue ? FormatBytes(UploadedBytes.Value) : "—";
    public string RatioText => UploadedBytes.HasValue && DownloadedEstimate > 0
        ? (UploadedBytes.Value / (double)DownloadedEstimate).ToString("0.00") : "—";
    public string PeersText => PeerCount.ToString();
    public string SeedsText => SeedCount.HasValue ? SeedCount.Value.ToString() : "—";
    public string SourceTypeText => Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "Magnet link" : "Torrent file";
    public string AddedFullText => AddedAt.ToString("HH:mm, dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public string EtaText
    {
        get
        {
            if (IsComplete) return "Done";
            if (Status is "Paused" or "Stopped" or "Error" || DownloadSpeed <= 0 || Size <= 0) return "—";
            double remaining = Size * (1 - Math.Clamp(Progress, 0, 100) / 100d);
            var t = TimeSpan.FromSeconds(Math.Min(remaining / DownloadSpeed, 99d * 24 * 3600));
            return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
                 : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                 : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
                 : $"{Math.Max(1, (int)t.TotalSeconds)}s";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatBytes(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double n = bytes; int i = 0; while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; } return $"{n:0.0} {units[i]}"; }
    private static string FormatRate(long bytes) => FormatBytes(bytes) + "/s";
    public TorrentItem(string name) => _name = name;
}

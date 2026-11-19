using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Globalization;
using System.ComponentModel;
using System.Windows.Data;
using System.Drawing;
using System.Windows.Interop;

namespace Shigaflow;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly DownloadService _service = new();
    // Prevent duplicate browser-extension handoffs (for example, the same click
    // being observed by multiple frames) from opening multiple Save As dialogs.
    private readonly object _browserRequestGate = new();
    private readonly HashSet<string> _browserRequestsInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentBrowserRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<TorrentItem> _torrents = new();
    private TorrentService? _torrentService;
    private readonly DispatcherTimer _torrentRefreshTimer;
    private static readonly string TorrentStateDirectory = Path.Combine(AppPaths.DataRoot, "Torrents");
    private static readonly string TorrentHistoryPath = Path.Combine(TorrentStateDirectory, "torrents.json");
    private DateTime _lastTorrentSaveUtc = DateTime.MinValue;
    private int _connections = 8;
    private AppSettings _settings;
    private readonly DownloadSpeedLimiter _speedLimiter = new();
    private readonly ICollectionView _itemsView;
    private readonly ICollectionView _torrentView;
    private string _downloadCategory = "All";
    private string _torrentCategory = "All";
    private ListSortDirection _sortDirection = ListSortDirection.Descending;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowRealClose;
    private readonly DispatcherTimer _historySaveTimer;
    private static readonly string HistoryPath = Path.Combine(AppPaths.DataRoot, "downloads.json");

    public MainWindow()
    {
        _settings = AppSettings.Load();
        App.ApplyTheme(_settings.Theme);
        InitializeComponent();
        LoadTorrentColumnWidths();
        LoadDownloadColumnWidths();
        if (_settings.SidebarCollapsed) SetSidebarCollapsed(true, animate: false, save: false);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _items.CollectionChanged += (_, _) => { if (_detailDownload != null && !_items.Contains(_detailDownload)) CloseDownloadDetails(); };
        _speedLimiter.SetLimit(_settings.SpeedLimitBytesPerSecond);
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _itemsView.Filter = item => MatchesCategory(item is DownloadItem download ? download.Name : string.Empty, _downloadCategory);
        _torrentView = CollectionViewSource.GetDefaultView(_torrents);
        _torrentView.Filter = item => MatchesCategory(item is TorrentItem torrent ? torrent.Name : string.Empty, _torrentCategory);
        _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _historySaveTimer.Tick += (_, _) =>
        {
            _historySaveTimer.Stop();
            SaveHistory();
        };
        LoadHistory();
        _items.CollectionChanged += Items_CollectionChanged;
        foreach (var item in _items) item.PropertyChanged += DownloadItem_PropertyChanged;
        DownloadsList.ItemsSource = _itemsView;
        TorrentsList.ItemsSource = _torrentView;
        _torrentRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _torrentRefreshTimer.Tick += (_, _) => RefreshTorrentStats();
        _torrentRefreshTimer.Start();
        ApplySort();
        UpdateSummary();
        UpdateTorrentSummary();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        InitializeTrayIcon();
    }


    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (DownloadItem item in e.OldItems) item.PropertyChanged -= DownloadItem_PropertyChanged;
        if (e.NewItems != null)
            foreach (DownloadItem item in e.NewItems) item.PropertyChanged += DownloadItem_PropertyChanged;
        ScheduleHistorySave();
    }

    private void DownloadItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Persist progress and status as downloads run, so a crash or restart cannot erase the list.
        ScheduleHistorySave();
    }

    private void ScheduleHistorySave()
    {
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var records = _items.Select(item => new DownloadRecord
            {
                Name = item.Name,
                Url = item.Url,
                Status = item.Status,
                Detail = item.Detail,
                DestinationPath = item.DestinationPath,
                DateAdded = item.DateAdded,
                Progress = item.Progress,
                DownloadedBytes = item.DownloadedBytes,
                TotalBytes = item.TotalBytes,
                ConnectionCount = item.ConnectionCount,
                // Only store a real answer; "Checking…" is a temporary placeholder.
                Resume = item.Resume is "Yes" or "No" ? item.Resume : ""
            }).ToList();
            string tempPath = HistoryPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, HistoryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Could not save download history: " + ex.Message);
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var records = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(HistoryPath));
            if (records == null) return;
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Url) ||
                    string.IsNullOrWhiteSpace(record.DestinationPath)) continue;
                bool wasRunning = record.Status is "Downloading" or "Checking" or "Queued" or "Cancelling" or "Paused";
                bool fileIsComplete = File.Exists(record.DestinationPath) &&
                    (record.Status == "Complete" ||
                     (record.TotalBytes.HasValue && new FileInfo(record.DestinationPath).Length == record.TotalBytes.Value));
                var item = new DownloadItem(record.Name, record.Url)
                {
                    DestinationPath = record.DestinationPath,
                    DateAdded = record.DateAdded == default ? DateTime.Now : record.DateAdded,
                    Status = fileIsComplete ? "Complete" : wasRunning ? "Cancelled" : record.Status,
                    Detail = fileIsComplete ? $"Saved to {record.DestinationPath}" :
                        wasRunning ? "Download interrupted when Shigaflow closed. Select Resume to continue." : record.Detail,
                    Progress = fileIsComplete ? 100 : record.Progress,
                    DownloadedBytes = record.DownloadedBytes,
                    TotalBytes = record.TotalBytes,
                    ConnectionCount = record.ConnectionCount > 0 ? record.ConnectionCount : _connections,
                    IsBusy = false,
                    // Restore the saved answer so it doesn't sit on the "Checking…" default after a restart.
                    Resume = record.Resume is "Yes" or "No" ? record.Resume : "—"
                };
                if (fileIsComplete && File.Exists(record.DestinationPath))
                    item.DownloadedBytes = new FileInfo(record.DestinationPath).Length;
                _items.Add(item);
                // History saved by an older version has no Resume value: look it up once for unfinished items.
                if (item.Resume == "—" && !fileIsComplete) { item.Resume = "Checking…"; _ = ProbeResumeAsync(item); }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Could not load download history: " + ex.Message);
        }
    }

    private async Task ProbeResumeAsync(DownloadItem item)
    {
        string result = "—";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var probe = await _service.ProbeAsync(item.Url, cts.Token);
            result = probe.CanResume ? "Yes" : "No";
        }
        catch { }
        // A restarted download may already have set the real value; don't overwrite it.
        if (item.Resume == "Checking…") item.Resume = result;
    }

    private sealed class DownloadRecord
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Status { get; set; } = "Queued";
        public string Detail { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public DateTime DateAdded { get; set; }
        public double Progress { get; set; }
        public long DownloadedBytes { get; set; }
        public long? TotalBytes { get; set; }
        public int ConnectionCount { get; set; } = 8;
        public string Resume { get; set; } = "";
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Shigaflow");
        openItem.Click += (_, _) => RestoreFromTray();
        var pauseAllItem = new System.Windows.Forms.ToolStripMenuItem("Pause All");
        pauseAllItem.Click += (_, _) =>
        {
            foreach (var item in _items.Where(i => i.IsBusy && i.Status == "Downloading").ToList())
                PauseItem(item);
        };
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(openItem);
        menu.Items.Add(pauseAllItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        Icon icon = SystemIcons.Application;
        try
        {
            string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
                icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch { }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Shigaflow",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowRealClose)
        {
            _historySaveTimer.Stop();
            _torrentRefreshTimer.Stop();
            SaveHistory();
            SaveTorrentHistory();
            SaveTorrentColumnWidths();
            SaveDownloadColumnWidths();
            try { _torrentService?.Dispose(); } catch { }
            return;
        }
        e.Cancel = true;
        Hide();
        if (_trayIcon != null)
        {
            _trayIcon.BalloonTipTitle = "Shigaflow is still running";
            _trayIcon.BalloonTipText = "The app was minimized to the system tray.";
            _trayIcon.ShowBalloonTip(1800);
        }
    }

    // Called when the user launches Shigaflow again while this instance is in the tray.
    public void ShowFromExternalLaunch()
    {
        RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _allowRealClose = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // ---------- Collapsible sidebar (icon strip, like Windows 11 Task Manager) ----------
    private const double SidebarExpandedWidth = 190;
    private const double SidebarCollapsedWidth = 64;
    private bool _sidebarCollapsed;

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(!_sidebarCollapsed, animate: true, save: true);

    private void SetSidebarCollapsed(bool collapsed, bool animate, bool save)
    {
        _sidebarCollapsed = collapsed;
        double target = collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
        double from = SidebarBorder.ActualWidth > 0 ? SidebarBorder.ActualWidth : (collapsed ? SidebarExpandedWidth : SidebarCollapsedWidth);

        if (animate)
        {
            // The border is clipped, so labels are revealed or cut off smoothly while the width slides.
            // Expanding switches to the full layout first; collapsing switches to the icon layout at the end.
            if (!collapsed) ApplySidebarLayout(false);
            SidebarBorder.Width = target;
            var slide = new System.Windows.Media.Animation.DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            if (collapsed) slide.Completed += (_, _) => { if (_sidebarCollapsed) ApplySidebarLayout(true); };
            SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, slide);
        }
        else
        {
            SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
            SidebarBorder.Width = target;
            ApplySidebarLayout(collapsed);
        }

        SidebarToggleButton.ToolTip = collapsed ? "Expand sidebar (Ctrl+B)" : "Collapse sidebar (Ctrl+B)";
        if (save)
        {
            _settings.SidebarCollapsed = collapsed;
            try { _settings.Save(); } catch { }
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var nested in LogicalDescendants(d)) yield return nested;
        }
    }

    // Switches every sidebar piece between the full layout (icon + label) and the icon strip.
    private void ApplySidebarLayout(bool collapsed)
    {
        foreach (var d in LogicalDescendants(SidebarBorder))
        {
            if (d is TextBlock label && Equals(label.Tag, "SidebarLabel"))
            {
                label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (d is Grid row && Equals(row.Tag, "SidebarRow"))
            {
                row.HorizontalAlignment = collapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
                if (row.ColumnDefinitions.Count == 3) row.ColumnDefinitions[2].Width = new GridLength(collapsed ? 0 : 18);
                foreach (UIElement child in row.Children)
                    if (Grid.GetColumn(child) == 0 && child is FrameworkElement icon)
                        icon.HorizontalAlignment = collapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            }
            else if (d is System.Windows.Controls.Button category && category.Tag is string name)
            {
                // Category buttons: smaller icon-only rows with a tooltip while collapsed.
                category.Padding = collapsed ? new Thickness(0, 7, 0, 7) : new Thickness(10, 7, 10, 7);
                category.ToolTip = collapsed ? name : null;
            }
        }

        // Logo block: small logo only while collapsed.
        SidebarLogo.Width = SidebarLogo.Height = collapsed ? 34 : 96;
        SidebarAppName.Visibility = SidebarTagline.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarHeaderPanel.Margin = collapsed ? new Thickness(0, 4, 0, 14) : new Thickness(4, 5, 4, 24);
        SidebarToggleButton.HorizontalAlignment = collapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;

        // Main buttons: centred icons with tooltips while collapsed.
        var navPadding = collapsed ? new Thickness(0, 10, 0, 10) : new Thickness(10, 10, 10, 10);
        DownloadsNavButton.Padding = navPadding;
        TorrentsNavButton.Padding = navPadding;
        SettingsButton.Padding = collapsed ? new Thickness(0, 11, 0, 11) : new Thickness(13, 11, 13, 11);
        DownloadsNavButton.ToolTip = collapsed ? "Downloads" : null;
        TorrentsNavButton.ToolTip = collapsed ? "Torrents" : null;
        SettingsButton.ToolTip = collapsed ? "Settings" : null;

        // Category lists: tighter box while collapsed so the small icons sit centred under their section icon.
        var listMargin = collapsed ? new Thickness(0, 2, 0, 10) : new Thickness(10, 2, 0, 10);
        var listPadding = collapsed ? new Thickness(3) : new Thickness(5);
        DownloadsCategories.Margin = TorrentsCategories.Margin = listMargin;
        DownloadsCategories.Padding = TorrentsCategories.Padding = listPadding;
    }

    private void ShowDownloads_Click(object sender, RoutedEventArgs e) => ActivateSection(torrents: false, toggleIfActive: true);

    private void ShowTorrents_Click(object sender, RoutedEventArgs e) => ActivateSection(torrents: true, toggleIfActive: true);

    // Sidebar accordion: only one section's sub-menu is open at a time.
    //  - Clicking the other section opens it and collapses this one.
    //  - Clicking the section that is already showing toggles its own sub-menu (toggleIfActive).
    //  - Code that only wants to show a page (for example after adding a magnet link) passes
    //    toggleIfActive: false so an already-open sub-menu is not closed by accident.
    private void ActivateSection(bool torrents, bool toggleIfActive)
    {
        var page = torrents ? TorrentsPage : DownloadsPage;
        var mine = torrents ? TorrentsCategories : DownloadsCategories;
        var other = torrents ? DownloadsCategories : TorrentsCategories;
        bool alreadyActive = page.Visibility == Visibility.Visible;

        other.Visibility = Visibility.Collapsed;
        if (!alreadyActive) mine.Visibility = Visibility.Visible;
        else if (toggleIfActive) mine.Visibility = mine.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        DownloadsExpandGlyph.Text = DownloadsCategories.Visibility == Visibility.Visible ? "−" : "+";
        TorrentsExpandGlyph.Text = TorrentsCategories.Visibility == Visibility.Visible ? "−" : "+";

        DownloadsPage.Visibility = torrents ? Visibility.Collapsed : Visibility.Visible;
        TorrentsPage.Visibility = torrents ? Visibility.Visible : Visibility.Collapsed;

        var active = torrents ? TorrentsNavButton : DownloadsNavButton;
        var inactive = torrents ? DownloadsNavButton : TorrentsNavButton;
        active.Background = (System.Windows.Media.Brush)FindResource("SelectedBrush");
        active.BorderBrush = (System.Windows.Media.Brush)FindResource("StrokeBrush");
        inactive.ClearValue(BackgroundProperty);
        inactive.ClearValue(BorderBrushProperty);
    }

    private void DownloadCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string category)
        {
            _downloadCategory = category;
            _itemsView.Refresh();
            int visibleCount = _itemsView.Cast<object>().Count();
            CountText.Text = $"{visibleCount} item{(visibleCount == 1 ? "" : "s")}";
            EmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void TorrentCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string category)
        {
            _torrentCategory = category;
            _torrentView.Refresh();
            TorrentCountText.Text = $"{_torrentView.Cast<object>().Count()} torrents";
            TorrentsEmptyState.Visibility = _torrentView.Cast<object>().Any() ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static bool MatchesCategory(string fileName, string category)
    {
        if (category == "All") return true;
        string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        string[] programs = { ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".com", ".bat", ".cmd", ".ps1", ".sh", ".jar", ".apk", ".deb", ".rpm", ".dmg", ".pkg" };
        string[] video = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpeg", ".mpg", ".3gp", ".ts" };
        string[] audio = { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff", ".mid", ".midi" };
        string[] documents = { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".odp", ".ods", ".epub" };
        string[] images = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".ico", ".heic", ".avif" };
        bool known = programs.Contains(extension) || video.Contains(extension) || audio.Contains(extension) || documents.Contains(extension) || images.Contains(extension);
        return category switch
        {
            "Programs" => programs.Contains(extension),
            "Video" => video.Contains(extension),
            "Audio" => audio.Contains(extension),
            "Document" => documents.Contains(extension),
            "Images" => images.Contains(extension),
            "Uncategorized" => !known,
            _ => true
        };
    }

    private async Task InitializeTorrentEngineAsync()
    {
        try
        {
            Directory.CreateDirectory(TorrentStateDirectory);
            _torrentService = new TorrentService(TorrentStateDirectory, _settings);
            if (!File.Exists(TorrentHistoryPath)) return;
            var records = JsonSerializer.Deserialize<List<TorrentRecord>>(File.ReadAllText(TorrentHistoryPath)) ?? new();
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Source) || string.IsNullOrWhiteSpace(record.SavePath)) continue;
                var item = new TorrentItem(record.Name) { Source = record.Source, SavePath = record.SavePath, AddedAt = record.AddedAt == default ? DateTime.Now : record.AddedAt, IsComplete = record.IsComplete, Status = "Restoring", Detail = "Restoring torrent session…", Progress = record.Progress, SessionStartProgress = record.Progress, Size = record.Size };
                _torrents.Add(item);
                try
                {
                    item.Manager = await _torrentService.AddAsync(item.Source, item.SavePath);
                    if (record.Status is "Paused" or "Stopped")
                    {
                        await _torrentService.PauseAsync(item.Manager); item.Status = record.Status;
                        item.Detail = record.Status == "Paused" ? "Paused by you" : "Stopped by you";
                    }
                    else { item.Status = item.IsComplete ? "Seeding" : "Downloading"; item.Detail = item.IsComplete ? "Completed — continuing to seed" : "Resumed after Shigaflow restarted"; }
                }
                catch (Exception ex) { item.Status = "Error"; item.Detail = "Could not restore torrent: " + ex.Message; }
            }
            UpdateTorrentSummary();
        }
        catch (Exception ex) { MessageBox.Show(this, "The torrent engine could not be initialized.\n\n" + ex.Message, "Torrent engine error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task AddTorrentFromMagnetAsync(string magnetUrl)
    {
        if (_torrentService == null)
        {
            MessageBox.Show(this, "The torrent engine is still starting. Please try the magnet link again.", "Torrent engine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!magnetUrl.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return;

        using var folder = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where this torrent should be saved",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var item = new TorrentItem("Magnet torrent")
        {
            Source = magnetUrl,
            SavePath = folder.SelectedPath,
            Status = "Queued",
            Detail = "Adding magnet link to torrent engine…"
        };
        _torrents.Insert(0, item);
        UpdateTorrentSummary();
        ActivateSection(torrents: true, toggleIfActive: false);
        try
        {
            item.Manager = await _torrentService.AddAsync(magnetUrl, item.SavePath);
            item.Name = item.Manager.Name;
            item.Status = "Downloading";
            item.Detail = "Magnet accepted — retrieving metadata and connecting to peers";
            SaveTorrentHistory();
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.Detail = ex.Message;
            MessageBox.Show(this, "Could not add the magnet link.\n\n" + ex.Message, "Magnet capture failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateTorrentSummary();
    }

    private async void AddTorrent_Click(object sender, RoutedEventArgs e)
    {
        if (_torrentService == null) { MessageBox.Show(this, "The torrent engine is still starting. Please try again in a moment.", "Torrent engine", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string source, name;
        var choice = MessageBox.Show(this, "Choose Yes to select a .torrent file, or No to paste a magnet link.", "Add Torrent", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes)
        {
            var dialog = new OpenFileDialog { Title = "Select torrent file", Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            string meta = Path.Combine(TorrentStateDirectory, "metainfo"); Directory.CreateDirectory(meta);
            source = Path.Combine(meta, Guid.NewGuid().ToString("N") + ".torrent"); File.Copy(dialog.FileName, source);
            name = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
        else
        {
            var dialog = new UrlDialog("magnet:?xt=urn:btih:") { Owner = this, Title = "Add Magnet Link" };
            if (dialog.ShowDialog() != true) return;
            source = dialog.Url.Trim();
            if (!source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Please enter a valid magnet link beginning with magnet:.", "Invalid magnet link", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            name = "Magnet torrent";
        }
        using var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where torrent files should be saved", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var item = new TorrentItem(name) { Source = source, SavePath = folder.SelectedPath, Status = "Queued", Detail = "Adding torrent to engine…" };
        _torrents.Insert(0, item); UpdateTorrentSummary();
        try { item.Manager = await _torrentService.AddAsync(source, item.SavePath); item.Status = "Downloading"; item.Detail = "Torrent added — connecting to peers"; SaveTorrentHistory(); }
        catch (Exception ex) { item.Status = "Error"; item.Detail = ex.Message; MessageBox.Show(this, "Could not add this torrent.\n\n" + ex.Message, "Add torrent failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        UpdateTorrentSummary();
    }

    // ---------- Download details panel (docked at the bottom of the Downloads page) ----------
    private DownloadItem? _detailDownload;

    private void DownloadsList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (DependencyObject? d = source; d != null; d = ParentOf(d))
        {
            // Checkboxes, the Pause/Resume button, scrollbars and column headers (sort / resize) must not open the panel.
            if (d is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.DataGridColumnHeader) return;
            if (d is DataGridRow row)
            {
                if (row.Item is DownloadItem item && !item.IsSelectionMode) ShowDownloadDetails(item);
                return;
            }
        }
    }

    private void ShowDownloadDetails(DownloadItem item)
    {
        _detailDownload = item;
        DownloadDetailsPanel.DataContext = item;
        DownloadDetailsPanel.Visibility = Visibility.Visible;
    }

    private void CloseDownloadDetails()
    {
        _detailDownload = null;
        DownloadDetailsPanel.Visibility = Visibility.Collapsed;
        DownloadDetailsPanel.DataContext = null;
        DownloadsList.SelectedItem = null;
    }

    private void CloseDownloadDetails_Click(object sender, RoutedEventArgs e) => CloseDownloadDetails();

    // ---------- Torrent details panel (docked at the bottom of the Torrents page) ----------
    private TorrentItem? _detailTorrent;

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private void TorrentsList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (DependencyObject? d = source; d != null; d = ParentOf(d))
        {
            // Checkboxes, buttons, scrollbars and column headers (sort/resize) must not open the panel.
            if (d is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.DataGridColumnHeader) return;
            if (d is DataGridRow row)
            {
                if (row.Item is TorrentItem item && !item.IsSelectionMode) ShowTorrentDetails(item);
                return;
            }
        }
    }

    private void ShowTorrentDetails(TorrentItem item)
    {
        _detailTorrent = item;
        TorrentDetailsPanel.DataContext = item;
        TorrentDetailsPanel.Visibility = Visibility.Visible;
    }

    private void CloseTorrentDetails()
    {
        _detailTorrent = null;
        TorrentDetailsPanel.Visibility = Visibility.Collapsed;
        TorrentDetailsPanel.DataContext = null;
        TorrentsList.SelectedItem = null;
    }

    private void CloseTorrentDetails_Click(object sender, RoutedEventArgs e) => CloseTorrentDetails();

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.B && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            SetSidebarCollapsed(!_sidebarCollapsed, animate: true, save: true);
            e.Handled = true;
            return;
        }
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (TorrentsPage.Visibility == Visibility.Visible && TorrentDetailsPanel.Visibility == Visibility.Visible)
        {
            CloseTorrentDetails();
            e.Handled = true;
        }
        else if (DownloadsPage.Visibility == Visibility.Visible && DownloadDetailsPanel.Visibility == Visibility.Visible)
        {
            CloseDownloadDetails();
            e.Handled = true;
        }
    }

    // Reads an optional numeric property from the engine's status object by trying several likely names.
    private static long? ReadStatusNumber(object status, params string[] names)
    {
        var type = status.GetType();
        foreach (var name in names)
        {
            try
            {
                var property = type.GetProperty(name);
                if (property == null || property.GetIndexParameters().Length != 0) continue;
                var value = property.GetValue(status);
                if (value == null) continue;
                if (value.GetType().IsPrimitive && value is not bool && value is not char)
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch { }
        }
        return null;
    }

    private void DownloadRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var item = menu.DataContext as DownloadItem
                   ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as DownloadItem;
        if (item == null) return;

        // Rebuilt on every open so Pause/Resume and Open File always match the current state.
        menu.Items.Clear();
        menu.Items.Add(CreateIconMenuItem(item.PauseResumeGlyph, item.PauseResumeText, item, TogglePauseResumeItem_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateIconMenuItem("\uE711", "Cancel", item, CancelItem_Click));
        menu.Items.Add(CreateIconMenuItem("\uE74D", "Remove", item, RemoveItem_Click));
        menu.Items.Add(CreateIconMenuItem("\uE71B", "Change URL", item, ChangeUrlItem_Click));
        menu.Items.Add(new Separator());
        if (item.IsComplete)
            menu.Items.Add(CreateIconMenuItem("\uE8A5", "Open File", item, OpenFileItem_Click));
        menu.Items.Add(CreateIconMenuItem("\uE838", "Open File Location", item, OpenFileLocationItem_Click));
    }

    private void LoadDownloadColumnWidths()
    {
        if (DownloadsList.Columns.Count != 7) return;
        double[] widths =
        [
            _settings.DownloadColumnNameWidth,
            _settings.DownloadColumnStatusWidth,
            _settings.DownloadColumnResumeWidth,
            _settings.DownloadColumnSizeWidth,
            _settings.DownloadColumnSpeedWidth,
            _settings.DownloadColumnAddedWidth,
            _settings.DownloadColumnButtonWidth
        ];
        // Column 0 (Name) is skipped on purpose: it stays flexible so the table fills the available width
        // (for example when the sidebar is collapsed or the window is maximised).
        for (int i = 1; i < widths.Length; i++)
            if (double.IsFinite(widths[i]) && widths[i] > 40)
                DownloadsList.Columns[i].Width = new DataGridLength(widths[i]);
    }

    private void SaveDownloadColumnWidths()
    {
        if (DownloadsList.Columns.Count != 7) return;
        static double Keep(double actual, double previous) => double.IsFinite(actual) && actual > 40 ? actual : previous;
        _settings.DownloadColumnStatusWidth = Keep(DownloadsList.Columns[1].ActualWidth, _settings.DownloadColumnStatusWidth);
        _settings.DownloadColumnResumeWidth = Keep(DownloadsList.Columns[2].ActualWidth, _settings.DownloadColumnResumeWidth);
        _settings.DownloadColumnSizeWidth = Keep(DownloadsList.Columns[3].ActualWidth, _settings.DownloadColumnSizeWidth);
        _settings.DownloadColumnSpeedWidth = Keep(DownloadsList.Columns[4].ActualWidth, _settings.DownloadColumnSpeedWidth);
        _settings.DownloadColumnAddedWidth = Keep(DownloadsList.Columns[5].ActualWidth, _settings.DownloadColumnAddedWidth);
        _settings.DownloadColumnButtonWidth = Keep(DownloadsList.Columns[6].ActualWidth, _settings.DownloadColumnButtonWidth);
        try { _settings.Save(); } catch { }
    }

    private void TorrentRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // The row style gives every row the SAME ContextMenu object, so it must be rebuilt for the row
        // that was right-clicked each time it opens. (Building it only once made every row reuse the
        // first torrent's items, so Open File / Stop / Remove acted on the wrong torrent.)
        // PlacementTarget is always the row that was clicked; DataContext is only a fallback.
        var item = (menu.PlacementTarget as FrameworkElement)?.DataContext as TorrentItem
                   ?? menu.DataContext as TorrentItem;
        if (item == null) return;
        menu.Items.Clear();

        // Build the row menu in code rather than declaring Click handlers inside a
        // DataGridRow style. This avoids the XAML compiler incorrectly associating
        // those handlers with the following DataGridTemplateColumn.
        menu.Items.Add(CreateIconMenuItem(item.PauseResumeGlyph, item.PauseResumeText, item, TorrentPauseResume_Click));
        menu.Items.Add(CreateIconMenuItem("\uE71A", "Stop", item, TorrentStop_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateIconMenuItem("\uE8A5", "Open File", item, TorrentOpenFile_Click));
        menu.Items.Add(CreateIconMenuItem("\uE8B7", "Open Download Folder", item, TorrentOpenFolder_Click));
        menu.Items.Add(CreateIconMenuItem("\uE74D", "Remove Torrent", item, TorrentRemove_Click));
    }

    private async void TorrentPauseResume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item || item.Manager == null || _torrentService == null) return;
        try
        {
            if (item.Status is "Paused" or "Stopped" or "Queued" or "Error") { await _torrentService.ResumeAsync(item.Manager); item.Status = item.IsComplete ? "Seeding" : "Downloading"; item.Detail = item.IsComplete ? "Completed — seeding resumed" : "Connecting to peers…"; }
            else { await _torrentService.PauseAsync(item.Manager); item.Status = "Paused"; item.Detail = "Paused by you — torrent data is preserved"; item.DownloadSpeed = item.UploadSpeed = 0; }
            SaveTorrentHistory();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Torrent control failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void TorrentStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item || item.Manager == null || _torrentService == null) return;
        try { await _torrentService.StopAsync(item.Manager); item.Status = "Stopped"; item.Detail = "Stopped by you — select Resume to start again"; item.DownloadSpeed = item.UploadSpeed = 0; SaveTorrentHistory(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Stop torrent failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TorrentMore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item) return;
        var menu = CreateTorrentContextMenu(item, sender as UIElement);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu CreateTorrentContextMenu(TorrentItem item, UIElement? placementTarget)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            DataContext = item
        };
        var pauseResume = CreateIconMenuItem(item.PauseResumeGlyph, item.PauseResumeText, item, TorrentPauseResume_Click);
        var stop = CreateIconMenuItem("\uE71A", "Stop", item, TorrentStop_Click);
        var openFile = CreateIconMenuItem("\uE8A5", "Open File", item, TorrentOpenFile_Click);
        var openFolder = CreateIconMenuItem("\uE8B7", "Open Download Folder", item, TorrentOpenFolder_Click);
        var remove = CreateIconMenuItem("\uE74D", "Remove Torrent", item, TorrentRemove_Click);
        menu.Items.Add(pauseResume);
        menu.Items.Add(stop);
        menu.Items.Add(new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = (System.Windows.Media.Brush)FindResource("StrokeBrush") });
        menu.Items.Add(openFile);
        menu.Items.Add(openFolder);
        menu.Items.Add(remove);
        return menu;
    }

    private static MenuItem CreateIconMenuItem(string glyph, string label, object tag, RoutedEventHandler click)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush")
        });
        var item = new MenuItem { Header = header, Tag = tag };
        item.Click += click;
        return item;
    }

    private void TorrentOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item) return;
        try
        {
            string contentPath = Path.Combine(item.SavePath, item.Name);
            if (File.Exists(contentPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetFullPath(contentPath),
                    UseShellExecute = true
                });
                return;
            }

            if (Directory.Exists(contentPath))
            {
                // A multi-file torrent has a root folder instead of one file.
                // If it contains exactly one file, open that file; otherwise open the root folder.
                var files = Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories).Take(2).ToArray();
                string target = files.Length == 1 ? files[0] : contentPath;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetFullPath(target),
                    UseShellExecute = true
                });
                return;
            }

            MessageBox.Show(this,
                "The torrent's file is not available yet. Wait until its metadata has loaded and the download has started.",
                "File unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Windows could not open the torrent content.\n\n" + ex.Message,
                "Open file failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TorrentOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item) return;
        try
        {
            if (Directory.Exists(item.SavePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", item.SavePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TorrentRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TorrentItem item) return;

        var choice = MessageBox.Show(this,
            "How would you like to remove this torrent?\n\n" +
            "Yes = Remove it from the list and delete its downloaded file(s).\n" +
            "No = Remove it from the list but keep the downloaded file(s).\n" +
            "Cancel = Keep the torrent and its files.",
            "Remove Torrent", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;

        // Detach from libtorrent before touching the files. Do not let a detach
        // failure silently leave the row looking as though nothing happened.
        if (item.Manager != null && _torrentService != null)
        {
            try { await _torrentService.RemoveAsync(item.Manager); }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "The torrent could not be detached from the download engine, so it was kept in the list.\\n\\n" + ex.Message,
                    "Remove torrent failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _torrents.Remove(item);
        foreach (var remaining in _torrents)
        {
            remaining.IsSelectionMode = false;
            remaining.IsBulkSelected = false;
        }
        TorrentSelectButton.Content = "Select";
        SaveTorrentHistory();
        UpdateTorrentSummary();

        if (choice == MessageBoxResult.Yes)
            TryDeleteTorrentContent(item);
    }

    private void TryDeleteTorrentContent(TorrentItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.SavePath) || string.IsNullOrWhiteSpace(item.Name)) return;

            string root = Path.GetFullPath(item.SavePath);
            string contentPath = Path.GetFullPath(Path.Combine(root, item.Name));
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            // Only delete the torrent's own content path, never a path outside its save folder.
            if (!contentPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The torrent content path is outside its download folder. Nothing was deleted.");

            if (File.Exists(contentPath)) File.Delete(contentPath);
            else if (Directory.Exists(contentPath)) Directory.Delete(contentPath, recursive: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The torrent was removed from the list, but its downloaded files could not be deleted.\n\n{ex.Message}",
                "File could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsGenericTorrentName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.Equals("Magnet torrent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Torrent", StringComparison.OrdinalIgnoreCase);

    private static bool SameTorrentSavePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try
        {
            string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
    }

    private void RefreshTorrentStats()
    {
        if (_detailTorrent != null && !_torrents.Contains(_detailTorrent)) CloseTorrentDetails();
        foreach (var item in _torrents.ToList())
        {
            var manager = item.Manager; if (manager == null) continue;
            try
            {
                var snapshot = manager.GetCurrentStatus();
                string state = snapshot.State.ToString();

                // A torrent's name is part of its on-disk content path. Never let
                // the "new files in the save folder" fallback rename an existing
                // torrent when another torrent shares that same destination.
                // Handle metadata lookup is per-manager and remains safe.
                bool anotherTorrentSharesFolder = _torrents.Any(other =>
                    !ReferenceEquals(other, item) &&
                    SameTorrentSavePath(other.SavePath, item.SavePath));
                bool allowFolderNameFallback =
                    !item.IsComplete &&
                    IsGenericTorrentName(item.Name) &&
                    !anotherTorrentSharesFolder;

                if (manager.TryResolveMetadataName(snapshot, out string resolvedName, allowFolderNameFallback) &&
                    !resolvedName.Equals(item.Name, StringComparison.Ordinal))
                {
                    item.Name = resolvedName;
                    SaveTorrentHistory();
                }
                item.DownloadSpeed = snapshot.DownloadRate;
                item.UploadSpeed = snapshot.UploadRate;
                if (manager.TotalSize > 0) item.Size = manager.TotalSize;
                long downloaded = snapshot.BytesDownloaded;
                item.DownloadedBytes = downloaded;
                item.PeerCount = Convert.ToInt32(snapshot.PeerCount);
                // Optional extras: read only if this LibtorrentSharp build exposes them; otherwise the panel shows "—".
                if (ReadStatusNumber(snapshot, "SeedCount", "Seeds", "NumSeeds", "SeedsConnected", "ConnectedSeeds") is long seeds) item.SeedCount = (int)seeds;
                if (ReadStatusNumber(snapshot, "BytesUploaded", "TotalUploaded", "TotalUpload", "UploadedBytes", "BytesUp") is long uploaded) item.UploadedBytes = uploaded;
                if (!item.IsComplete && snapshot.Progress > 0)
                    item.Progress = Math.Min(99.9, snapshot.Progress * 100d);

                if (state.Equals("Seeding", StringComparison.OrdinalIgnoreCase) || state.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsComplete = true; item.Progress = 100;
                    if (item.Status is not ("Paused" or "Stopped"))
                    {
                        if (string.Equals(_settings.TorrentAfterFinishAction, "Stop", StringComparison.OrdinalIgnoreCase))
                        {
                            // Mark stopped immediately so the refresh timer cannot issue duplicate stop requests.
                            item.Status = "Stopped";
                            item.Detail = "Download complete — stopped by your torrent setting";
                            _ = StopCompletedTorrentAsync(item);
                        }
                        else { item.Status = "Seeding"; item.Detail = "Complete — seeding to other peers until you pause or stop"; }
                    }
                }
                else if (state.Equals("Downloading", StringComparison.OrdinalIgnoreCase) && item.Status is not ("Paused" or "Stopped"))
                { item.Status = "Downloading"; item.Detail = $"{FormatBytes(downloaded)} downloaded · {item.DownloadSpeedText} down · {item.UploadSpeedText} up"; }
                else if (state.Contains("Metadata", StringComparison.OrdinalIgnoreCase) && item.Status is not ("Paused" or "Stopped"))
                {
                    item.Status = "Downloading Metadata";
                    item.Detail = snapshot.PeerCount > 0
                        ? $"Retrieving torrent metadata · {snapshot.PeerCount} peer connection(s)"
                        : "Waiting for peers that can provide metadata · checking DHT and trackers";
                }
                else if (state.Equals("Errored", StringComparison.OrdinalIgnoreCase)) { item.Status = "Error"; item.Detail = string.IsNullOrWhiteSpace(snapshot.ErrorMessage) ? "Torrent engine reported an error" : snapshot.ErrorMessage; }
                else if (state.StartsWith("Checking", StringComparison.OrdinalIgnoreCase) && item.Status is not ("Paused" or "Stopped"))
                { item.Status = "Checking"; item.Detail = "Checking existing downloaded data…"; }
            }
            catch (Exception ex) { item.Detail = "Torrent status unavailable: " + ex.Message; }
        }
        // Re-apply the selected sort after live status/progress/size changes.
        // The torrent list is refreshed once per second, so dynamic sorts stay in order.
        ApplyTorrentSort();
        if (DateTime.UtcNow - _lastTorrentSaveUtc >= TimeSpan.FromSeconds(10)) SaveTorrentHistory();
    }


    private async Task StopCompletedTorrentAsync(TorrentItem item)
    {
        if (item.Manager == null || _torrentService == null) return;
        try
        {
            await _torrentService.StopAsync(item.Manager);
            SaveTorrentHistory();
        }
        catch (Exception ex)
        {
            item.Detail = "Download complete, but stopping the torrent failed: " + ex.Message;
        }
    }

    private void TorrentBulkSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool hasSelection = _torrents.Any(item => item.IsBulkSelectable && item.IsBulkSelected);
        TorrentSelectButton.Content = hasSelection ? "Delete" : "Cancel";
    }

    private async void TorrentSelectButton_Click(object sender, RoutedEventArgs e)
    {
        bool selectionMode = _torrents.Any(item => item.IsSelectionMode);
        if (!selectionMode)
        {
            foreach (var item in _torrents) { item.IsSelectionMode = true; item.IsBulkSelected = false; }
            TorrentSelectButton.Content = "Cancel";
            return;
        }

        var selected = _torrents.Where(item => item.IsBulkSelectable && item.IsBulkSelected).ToList();
        if (selected.Count == 0)
        {
            foreach (var item in _torrents) { item.IsSelectionMode = false; item.IsBulkSelected = false; }
            TorrentSelectButton.Content = "Select";
            return;
        }

        foreach (var item in selected)
        {
            if (item.Manager != null && _torrentService != null)
                try { await _torrentService.RemoveAsync(item.Manager); } catch { }
            _torrents.Remove(item);
        }
        foreach (var item in _torrents) { item.IsSelectionMode = false; item.IsBulkSelected = false; }
        TorrentSelectButton.Content = "Select";
        SaveTorrentHistory();
        UpdateTorrentSummary();
    }

    private async void TorrentClearListButton_Click(object sender, RoutedEventArgs e)
    {
        var eligible = _torrents.Where(item => item.IsBulkSelectable).ToList();
        if (eligible.Count == 0)
        {
            MessageBox.Show(this, "There are no completed, paused, or stopped torrents available to clear. Active downloads will remain in the list.",
                "Clear List", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(this,
            "Remove all completed, paused, stopped, and errored torrents from the list?\n\nTheir downloaded files will be kept. Active torrents will remain.",
            "Clear List", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;

        foreach (var item in eligible)
        {
            if (item.Manager != null && _torrentService != null)
                try { await _torrentService.RemoveAsync(item.Manager); } catch { }
            _torrents.Remove(item);
        }
        SaveTorrentHistory();
        UpdateTorrentSummary();
    }

    private void TorrentsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column is null) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in TorrentsList.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        string key = e.Column.SortMemberPath;
        using (_torrentView.DeferRefresh())
        {
            _torrentView.SortDescriptions.Clear();
            _torrentView.SortDescriptions.Add(new SortDescription(key, direction));
        }
    }

    private void LoadTorrentColumnWidths()
    {
        if (TorrentsList.Columns.Count != 6) return;
        double[] widths =
        [
            _settings.TorrentColumnNameWidth,
            _settings.TorrentColumnStatusWidth,
            _settings.TorrentColumnSizeWidth,
            _settings.TorrentColumnDownloadWidth,
            _settings.TorrentColumnUploadWidth,
            _settings.TorrentColumnAddedWidth
        ];
        // Column 0 (Name) stays flexible so the table fills the available width.
        for (int i = 1; i < widths.Length; i++)
            if (double.IsFinite(widths[i]) && widths[i] > 40)
                TorrentsList.Columns[i].Width = new DataGridLength(widths[i]);
    }

    private void SaveTorrentColumnWidths()
    {
        if (TorrentsList.Columns.Count != 6) return;
        _settings.TorrentColumnStatusWidth = TorrentsList.Columns[1].ActualWidth;
        _settings.TorrentColumnSizeWidth = TorrentsList.Columns[2].ActualWidth;
        _settings.TorrentColumnDownloadWidth = TorrentsList.Columns[3].ActualWidth;
        _settings.TorrentColumnUploadWidth = TorrentsList.Columns[4].ActualWidth;
        _settings.TorrentColumnAddedWidth = TorrentsList.Columns[5].ActualWidth;
        try { _settings.Save(); } catch { }
    }

    private void ApplyTorrentSort()
    {
        // Refresh the active header sort after live transfer values change.
        // The current SortDescriptions are retained by the collection view.
        _torrentView?.Refresh();
    }

    private void UpdateTorrentSummary()
    {
        _torrentView?.Refresh();
        int visibleCount = _torrentView?.Cast<object>().Count() ?? _torrents.Count;
        TorrentCountText.Text = $"{visibleCount} torrent{(visibleCount == 1 ? "" : "s")}";
        TorrentsEmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveTorrentHistory()
    {
        try
        {
            Directory.CreateDirectory(TorrentStateDirectory);
            var records = _torrents.Select(i => new TorrentRecord { Name = i.Name, Source = i.Source, SavePath = i.SavePath, Status = i.Status, Progress = i.Progress, Size = i.Size, IsComplete = i.IsComplete, AddedAt = i.AddedAt }).ToList();
            string temp = TorrentHistoryPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, TorrentHistoryPath, overwrite: true);
            _lastTorrentSaveUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Could not save torrent history: " + ex.Message); }
    }

    private sealed class TorrentRecord { public string Name { get; set; } = ""; public string Source { get; set; } = ""; public string SavePath { get; set; } = ""; public string Status { get; set; } = "Queued"; public double Progress { get; set; } public long Size { get; set; } public bool IsComplete { get; set; } public DateTime AddedAt { get; set; } }

    private async void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var input = new AddDownloadWindow { Owner = this };
        if (input.ShowDialog() != true) return;
        await AddDownloadFromUrlAsync(input.DownloadUrl, null);
    }

    private async Task AddDownloadFromUrlAsync(string url, string? browserSuggestedName, List<BrowserCookie>? browserCookies = null, bool isGoogleDriveBulkZip = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "Shigaflow can only take direct HTTP or HTTPS download URLs.", "Unsupported URL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string suggestedName = !string.IsNullOrWhiteSpace(browserSuggestedName)
            ? SanitizeFileName(Path.GetFileName(browserSuggestedName))
            : GetFileName(uri);
        bool returnedWebPage = false;
        try
        {
            var probe = await _service.ProbeAsync(url, CancellationToken.None, browserCookies);
            returnedWebPage = string.Equals(probe.ContentType, "text/html", StringComparison.OrdinalIgnoreCase)
                              && string.IsNullOrWhiteSpace(probe.FileName);
            if (!string.IsNullOrWhiteSpace(probe.FileName) && Path.HasExtension(probe.FileName)) suggestedName = SanitizeFileName(probe.FileName!);
            else if (!Path.HasExtension(suggestedName)) suggestedName = AddExtensionFromContentType(suggestedName, probe.ContentType);
        }
        catch { }

        // Google Drive assembles multi-file selections into a ZIP. Its download
        // endpoint can omit Content-Disposition and return a generic content type,
        // so neither Chrome's filename nor the HTTP probe is guaranteed to carry
        // the archive extension. Trust this flag only from the extension's
        // Google-domain bulk-download capture path.
        if (isGoogleDriveBulkZip && !suggestedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            suggestedName = Path.ChangeExtension(suggestedName, ".zip");

        // The link answered with a web page (sign-in/confirmation page, a folder, an expired link) rather
        // than a file. Say so instead of reporting a misleading empty file type.
        if (returnedWebPage && !isGoogleDriveBulkZip && !Path.HasExtension(suggestedName))
        {
            MessageBox.Show(this,
                "The server returned a web page instead of a file, so there is nothing to download from this link.\n\n" +
                "This can happen with a folder, a sign-in or confirmation page, or an expired link.",
                "Not a downloadable file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_settings.IsExtensionAllowed(suggestedName))
        {
            MessageBox.Show(this,
                $"The file type \"{Path.GetExtension(suggestedName)}\" is not in your allowed extensions list.\n\nChange the list in Settings to allow this file type.",
                "File type not allowed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save downloaded file",
            FileName = suggestedName,
            Filter = "All files (*.*)|*.*",
            AddExtension = false,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        string destinationPath = GetUniquePath(dialog.FileName);

        var item = new DownloadItem(Path.GetFileName(destinationPath), url)
        {
            DestinationPath = destinationPath,
            Status = "Queued",
            Detail = $"Saving to {dialog.FileName}",
            IsBusy = true,
            Cts = new CancellationTokenSource(),
            BrowserCookies = browserCookies
        };
        item.IsSelectionMode = _items.Any(existing => existing.IsSelectionMode);
        _items.Insert(0, item);
        _itemsView.Refresh();
        DownloadsList.SelectedItem = item;
        UpdateSummary();
        StartDownload(item, _connections);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RunNativePipeServerAsync();
        _ = InitializeTorrentEngineAsync();
    }

    private async Task RunNativePipeServerAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new System.IO.Pipes.NamedPipeServerStream(
                    "ShigaflowNativeHost", System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var request = System.Text.Json.JsonSerializer.Deserialize<BrowserDownloadRequest>(line,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (request != null && !string.IsNullOrWhiteSpace(request.Url))
                    {
                        // Acknowledge receipt immediately. Chrome's onDeterminingFilename
                        // callback has a short deadline; waiting for the user's Save As
                        // folder choice here would let Chrome fall back to its own dialog.
                        // The actual UI workflow continues on the dispatcher after the
                        // native host has received this acknowledgement.
                        await writer.WriteLineAsync("{\"ok\":true}");
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                            _ = ProcessBrowserDownloadRequestSafelyAsync(request)));
                    }
                    else await writer.WriteLineAsync("{\"ok\":false}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Native messaging pipe: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    private async Task ProcessBrowserDownloadRequestSafelyAsync(BrowserDownloadRequest request)
    {
        try
        {
            await ProcessBrowserDownloadRequestAsync(request);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Browser download processing failed: " + ex);
            await Dispatcher.InvokeAsync(() => MessageBox.Show(this,
                "Shigaflow received the browser download, but could not add it:\n\n" + ex.Message,
                "Browser download", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private async Task ProcessBrowserDownloadRequestAsync(BrowserDownloadRequest request)
    {
        // A browser click can be observed in more than one frame. Deduplicate by
        // URL in the app as a second line of defense, independent of file-name metadata.
        string key = request.Url.Trim();
        lock (_browserRequestGate)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string expired in _recentBrowserRequests
                .Where(pair => now - pair.Value > TimeSpan.FromSeconds(10))
                .Select(pair => pair.Key).ToArray())
                _recentBrowserRequests.Remove(expired);

            if (_browserRequestsInProgress.Contains(key) || _recentBrowserRequests.ContainsKey(key))
                return;

            _browserRequestsInProgress.Add(key);
        }

        try
        {
            // Browser capture can arrive while the app is minimized to the tray.
            RestoreFromTray();
            if (request.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                await AddTorrentFromMagnetAsync(request.Url);
            else
                await AddDownloadFromUrlAsync(request.Url, request.FileName, request.Cookies, request.GoogleDriveBulk);
        }
        finally
        {
            lock (_browserRequestGate)
            {
                _browserRequestsInProgress.Remove(key);
                _recentBrowserRequests[key] = DateTime.UtcNow;
            }
        }
    }

    private sealed class BrowserDownloadRequest
    {
        public BrowserDownloadRequest() { }
        public string Url { get; set; } = "";
        public string? FileName { get; set; }
        public List<BrowserCookie>? Cookies { get; set; }
        public bool GoogleDriveBulk { get; set; }
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SanitizeFileName(string name)
    {
        name = name.Trim().Trim('"');
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    private static string AddExtensionFromContentType(string name, string? contentType)
    {
        string? extension = contentType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/x-ms-wmv" => ".wmv",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            _ => null
        };
        return extension == null ? name : name + extension;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = window.UpdatedSettings;
            _speedLimiter.SetLimit(_settings.SpeedLimitBytesPerSecond);
            foreach (var item in _items.Where(i => i.IsBusy))
                item.Detail = _settings.SpeedLimitEnabled
                    ? $"Speed limit: {_settings.SpeedLimitValue:0.##} {_settings.SpeedLimitUnit} total"
                    : item.Detail;
        }
    }

    private static string GetFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
        try { name = Uri.UnescapeDataString(name); } catch { }
        return name;
    }

    private void StartDownload(DownloadItem item, int connections)
    {
        if (item.Cts == null || item.Cts.IsCancellationRequested) item.Cts = new CancellationTokenSource();
        item.Status = "Checking";
        item.Detail = "Checking server range support…";
        var progress = new Progress<(double overall, int segment, double segmentProgress, long bytes)>(p =>
        {
            item.Progress = p.overall;
            item.DownloadedBytes = p.bytes;
            if (SortBox?.SelectedItem is ComboBoxItem sortItem && sortItem.Content?.ToString() is "Downloaded size" or "Total size") _itemsView.Refresh();
            if (item.TotalBytes is > 0)
                item.Detail = $"{FormatBytes(p.bytes)} of {FormatBytes(item.TotalBytes.Value)}";
            else
                item.Detail = $"{FormatBytes(p.bytes)} downloaded";
        });
        item.ConnectionCount = connections;
        item.Worker = RunDownloadAsync(item, connections, progress);
    }

    private async Task RunDownloadAsync(DownloadItem item, int connections, IProgress<(double overall, int segment, double segmentProgress, long bytes)> progress)
    {
        try
        {
            item.Status = "Downloading";
            item.Detail = "Connecting to server…";
            await _service.DownloadAsync(item, item.Url, item.DestinationPath, connections, progress, item.Cts!.Token, _speedLimiter, item.BrowserCookies);
            item.Status = "Complete";
            item.Progress = 100;
            item.Speed = "—";
            item.Detail = $"Saved to {item.DestinationPath}";
            item.IsBusy = false;
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            item.Status = item.PauseRequested ? "Paused" : "Cancelled";
            item.Speed = "—";
            item.Detail = item.PauseRequested ? "Paused for this session" : "Download cancelled";
            item.IsBusy = false;
            UpdateSummary();
        }
        catch (Exception ex)
        {
            item.Status = "Failed";
            item.Speed = "—";
            item.Detail = ex.Message;
            item.IsBusy = false;
            UpdateSummary();
        }
    }

    private void CancelSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsList.SelectedItem is DownloadItem item) CancelItem(item);
    }

    private static DownloadItem? GetItem(object sender) => (sender as FrameworkElement)?.Tag as DownloadItem;
    private void TogglePauseResumeItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not DownloadItem item) return;
        if (item.Status is "Paused" or "Cancelled" or "Failed") ResumeItem(item);
        else PauseItem(item);
    }
    private void CancelItem_Click(object sender, RoutedEventArgs e) { if (GetItem(sender) is DownloadItem item) CancelItem(item); }

    private void OpenFileItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not DownloadItem item || item.Status != "Complete") return;
        try
        {
            string path = item.DestinationPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "The downloaded file could not be found.", "File unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, $"Windows could not open the downloaded file.\n\n{ex.Message}", "Unable to open file", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenFileLocationItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not DownloadItem item) return;

        try
        {
            string path = item.DestinationPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "The download does not have a saved file location yet.", "File location unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            path = Path.GetFullPath(path);
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });
                MessageBox.Show(this,
                    "The downloaded file could not be found. I opened its containing folder instead.",
                    "File not found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBox.Show(this,
                "The downloaded file and its containing folder could not be found.",
                "File location unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Windows Explorer could not open the file location.\n\n{ex.Message}",
                "Unable to open file location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PauseItem(DownloadItem item)
    {
        if (!item.IsBusy || item.Status is "Complete" or "Cancelled" or "Failed" or "Paused") return;

        lock (item.Gate)
        {
            item.PauseRequested = true;
            item.ResumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Do not cancel the request: the active stream workers wait at read boundaries.
        // Keeping those workers alive preserves each segment's in-progress byte count.
        item.Status = "Paused";
        item.Speed = "—";
        item.Detail = "Paused — downloaded data is being kept";
    }

    private void ResumeItem(DownloadItem item)
    {
        if (item.Status == "Paused" && item.IsBusy)
        {
            lock (item.Gate)
            {
                item.PauseRequested = false;
                item.ResumeSignal?.TrySetResult();
                item.ResumeSignal = null;
            }
            item.Status = "Downloading";
            item.Detail = "Resuming from saved progress…";
            return;
        }

        // A failed/cancelled worker is no longer alive; that is a restart, not an in-place pause resume.
        if (item.Status is not ("Cancelled" or "Failed")) return;
        item.PauseRequested = false;
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();
        item.Status = "Queued";
        item.Detail = "Restarting download after interruption…";
        item.IsBusy = true;
        StartDownload(item, item.ConnectionCount > 0 ? item.ConnectionCount : _connections);
    }

    private void CancelItem(DownloadItem item)
    {
        if (!item.IsBusy && (item.Status is "Complete" or "Cancelled")) return;
        item.PauseRequested = false;
        lock (item.Gate) { item.ResumeSignal?.TrySetResult(); item.ResumeSignal = null; }
        item.Cts?.Cancel(); item.Status = "Cancelling"; item.Detail = "Stopping download…";
    }

    private async void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not DownloadItem item) return;

        var choice = MessageBox.Show(this,
            "How would you like to remove this download?\n\n" +
            "Yes = Remove it from the list and delete the downloaded file.\n" +
            "No = Remove it from the list but keep the downloaded file.\n" +
            "Cancel = Keep the download in the list.",
            "Remove Download", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;

        if (item.IsBusy)
        {
            item.PauseRequested = false;
            lock (item.Gate)
            {
                item.ResumeSignal?.TrySetResult();
                item.ResumeSignal = null;
            }
            item.Cts?.Cancel();
            item.Status = "Cancelling";
            item.Detail = "Stopping before removal…";
            try
            {
                if (item.Worker != null) await item.Worker;
            }
            catch { }
        }

        _items.Remove(item);

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.DestinationPath) && File.Exists(item.DestinationPath))
                    File.Delete(item.DestinationPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The download was removed from the list, but its file could not be deleted.\n\n{ex.Message}",
                    "File could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        CleanupPartsFolder(item);
        UpdateSummary();
    }

    private async void ChangeUrlItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not DownloadItem item) return;
        if (item.IsBusy || item.Status == "Complete") { MessageBox.Show(this, "Pause or stop this download before changing its URL.", "Change URL", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new UrlDialog(item.Url) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        string newUrl = dialog.Url.Trim();
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { MessageBox.Show(this, "Enter a valid HTTP or HTTPS URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var probe = await _service.ProbeAsync(newUrl, CancellationToken.None);
        bool hasPartialTarget = File.Exists(item.DestinationPath) && new FileInfo(item.DestinationPath).Length > 0;
        bool hasPartialSegments = Directory.Exists(item.DestinationPath + ".parts") && Directory.EnumerateFiles(item.DestinationPath + ".parts").Any();
        if ((hasPartialTarget || hasPartialSegments) && (!item.TotalBytes.HasValue || !probe.Length.HasValue))
        { MessageBox.Show(this, "The file size could not be verified for both links, so the URL was not changed. This avoids mixing an unverified file with partial data.", "Cannot verify file", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (item.TotalBytes.HasValue && probe.Length.HasValue && item.TotalBytes.Value != probe.Length.Value)
        { MessageBox.Show(this, "The new URL reports a different file size, so it was not applied. This avoids mixing different files.", "File mismatch", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if ((hasPartialTarget || hasPartialSegments) && !probe.CanResume)
        { MessageBox.Show(this, "The new URL does not support byte ranges, so it cannot safely resume this partial file.", "Cannot resume", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        item.Url = newUrl;
        item.Cts = new CancellationTokenSource();
        item.Status = "Queued"; item.Detail = "URL updated; restarting download…";
        StartDownload(item, item.ConnectionCount > 0 ? item.ConnectionCount : 1);
    }


    private void BulkSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool hasSelection = _items.Any(item => item.IsBulkSelectable && item.IsBulkSelected);
        SelectDeleteButton.Content = hasSelection ? "Delete" : "Cancel";
    }

    private void SelectDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        bool selectionMode = _items.Any(item => item.IsSelectionMode);
        if (!selectionMode)
        {
            foreach (var item in _items)
            {
                item.IsSelectionMode = true;
                item.IsBulkSelected = false;
            }
            SelectDeleteButton.Content = "Cancel";
            return;
        }

        var selected = _items.Where(item => item.IsBulkSelectable && item.IsBulkSelected).ToList();
        if (selected.Count == 0)
        {
            foreach (var item in _items)
            {
                item.IsSelectionMode = false;
                item.IsBulkSelected = false;
            }
            SelectDeleteButton.Content = "Select";
            return;
        }

        foreach (var item in selected)
        {
            _items.Remove(item);
            CleanupPartsFolder(item);
        }

        foreach (var item in _items)
        {
            item.IsSelectionMode = false;
            item.IsBulkSelected = false;
        }
        SelectDeleteButton.Content = "Select";
        UpdateSummary();
    }

    private void ClearListButton_Click(object sender, RoutedEventArgs e)
    {
        var eligible = _items.Where(item => item.IsBulkSelectable).ToList();
        if (eligible.Count == 0)
        {
            MessageBox.Show(this, "There are no completed or stopped downloads available to clear. Active and paused downloads are kept.",
                "Clear List", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(this,
            "Choose how to clear eligible downloads:\n\nYes = Remove entries and delete their downloaded files.\nNo = Remove entries but keep downloaded files.\nCancel = Do nothing.\n\nActive and paused downloads will remain in the list.",
            "Clear List", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;

        foreach (var item in eligible)
        {
            _items.Remove(item);
            if (choice == MessageBoxResult.Yes)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(item.DestinationPath) && File.Exists(item.DestinationPath))
                        File.Delete(item.DestinationPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not delete '{item.Name}'.\n\n{ex.Message}",
                        "File could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            CleanupPartsFolder(item);
        }

        UpdateSummary();
    }

    private static void CleanupPartsFolder(DownloadItem item)
    {
        try
        {
            string partsFolder = item.DestinationPath + ".parts";
            if (Directory.Exists(partsFolder)) Directory.Delete(partsFolder, recursive: true);
        }
        catch { }
    }

    private string? _headerSortKey;      // set when the active sort came from a column header with no dropdown equivalent
    private bool _syncingSort;

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSort || _itemsView == null) return;
        // The dropdown took over: drop the header's sort arrow.
        _headerSortKey = null;
        foreach (var column in DownloadsList.Columns) column.SortDirection = null;
        ApplySort();
    }

    private void DownloadsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column is null) return;
        e.Handled = true;
        string key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;

        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in DownloadsList.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;

        _sortDirection = direction;
        SortDirectionButton.Content = direction == ListSortDirection.Ascending ? "↑" : "↓";

        // Keep the Sort dropdown in step where it has a matching entry.
        string? dropdownText = key switch
        {
            nameof(DownloadItem.Name) => "Name",
            nameof(DownloadItem.Status) => "Status",
            nameof(DownloadItem.TotalBytesSort) => "Total size",
            nameof(DownloadItem.DateAdded) => "Date added",
            _ => null
        };
        _syncingSort = true;
        try
        {
            SortBox.SelectedItem = dropdownText == null
                ? null
                : SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == dropdownText);
        }
        finally { _syncingSort = false; }
        _headerSortKey = dropdownText == null ? key : null;
        ApplySort();
    }
    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDirection = _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        SortDirectionButton.Content = _sortDirection == ListSortDirection.Ascending ? "↑" : "↓";
        foreach (var column in DownloadsList.Columns)
            if (column.SortDirection != null) column.SortDirection = _sortDirection;
        ApplySort();
    }
    private void ApplySort()
    {
        if (_itemsView == null) return;
        string key;
        if (_headerSortKey != null) key = _headerSortKey;
        else if (SortBox?.SelectedItem is ComboBoxItem selected)
            key = selected.Content?.ToString() switch
            {
                "Name" => nameof(DownloadItem.Name),
                "Downloaded size" => nameof(DownloadItem.DownloadedBytesSort),
                "Total size" => nameof(DownloadItem.TotalBytesSort),
                "Progress" => nameof(DownloadItem.Progress),
                "Status" => nameof(DownloadItem.Status),
                _ => nameof(DownloadItem.DateAdded)
            };
        else return;
        if (_itemsView is ListCollectionView listView)
        {
            listView.CustomSort = new DownloadItemComparer(key, _sortDirection);
        }
        else
        {
            using (_itemsView.DeferRefresh())
            {
                _itemsView.SortDescriptions.Clear();
                _itemsView.SortDescriptions.Add(new SortDescription(key, _sortDirection));
            }
        }
    }

    private sealed class DownloadItemComparer(string key, ListSortDirection direction) : System.Collections.IComparer
    {
        // Speed is stored as display text such as "1,5 MB/s" or "—"; turn it back into bytes/second.
        private static double ParseSpeed(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not DownloadItem a) return -1;
            if (y is not DownloadItem b) return 1;
            if (key == nameof(DownloadItem.TotalBytesSort))
            {
                if (!a.TotalBytes.HasValue && b.TotalBytes.HasValue) return 1;
                if (a.TotalBytes.HasValue && !b.TotalBytes.HasValue) return -1;
                if (!a.TotalBytes.HasValue && !b.TotalBytes.HasValue) return 0;
                int sizeCompare = a.TotalBytes!.Value.CompareTo(b.TotalBytes!.Value);
                return direction == ListSortDirection.Ascending ? sizeCompare : -sizeCompare;
            }
            int result = key switch
            {
                nameof(DownloadItem.Name) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                nameof(DownloadItem.DownloadedBytesSort) => a.DownloadedBytes.CompareTo(b.DownloadedBytes),
                nameof(DownloadItem.Progress) => a.Progress.CompareTo(b.Progress),
                nameof(DownloadItem.Status) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Status, b.Status),
                nameof(DownloadItem.Resume) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Resume, b.Resume),
                nameof(DownloadItem.Speed) => ParseSpeed(a.Speed).CompareTo(ParseSpeed(b.Speed)),
                _ => a.DateAdded.CompareTo(b.DateAdded)
            };
            return direction == ListSortDirection.Ascending ? result : -result;
        }
    }

    private void UpdateSummary()
    {
        _itemsView?.Refresh();
        int visibleCount = _itemsView?.Cast<object>().Count() ?? _items.Count;
        CountText.Text = $"{visibleCount} item{(visibleCount == 1 ? "" : "s")}";
        EmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleHistorySave();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.0} {units[unit]}";
    }
}
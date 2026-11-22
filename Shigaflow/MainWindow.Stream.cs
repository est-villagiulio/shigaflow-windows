using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

namespace Shigaflow;

// Downloads handled by the video engine (yt-dlp + FFmpeg): YouTube, Facebook and other sites,
// HLS (.m3u8) and DASH (.mpd) streams, and any player whose video is not a single plain file.
public partial class MainWindow
{
    private readonly SemaphoreSlim _streamToolsGate = new(1, 1);

    private async Task AddStreamDownloadAsync(BrowserDownloadRequest request)
    {
        var urls = new List<string>();
        foreach (string candidate in new[] { request.Url }.Concat(request.AlternateUrls ?? new List<string>()))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !urls.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
                urls.Add(uri.AbsoluteUri);
        }
        if (urls.Count == 0) return;

        bool needDeno = urls.Any(StreamEngine.IsYouTubeUrl);
        string browserTitle = (request.FileName ?? "").Trim();
        string quality = StreamEngine.NormalizeQuality(request.Quality);
        bool audioOnly = StreamEngine.IsAudioQuality(quality);
        string defaultExtension = StreamEngine.ExtensionFor(quality);

        // A row is shown right away: finding the video can take several seconds.
        var item = new DownloadItem((string.IsNullOrWhiteSpace(browserTitle) ? (audioOnly ? "Audio" : "Video") : browserTitle) + "." + defaultExtension, urls[0])
        {
            IsStream = true,
            StreamQuality = quality,
            Status = "Checking",
            Detail = "Preparing video download…",
            Resume = "Yes",
            IsBusy = true,
            Cts = new CancellationTokenSource(),
            Referer = request.Referer
        };
        item.IsSelectionMode = _items.Any(existing => existing.IsSelectionMode);
        _items.Insert(0, item);
        _itemsView.Refresh();
        DownloadsList.SelectedItem = item;
        UpdateSummary();

        try
        {
            var tools = await EnsureStreamToolsAsync(item, needDeno);
            if (tools == null) { DiscardStreamPlaceholder(item); return; }

            CancellationToken ct = item.Cts!.Token;
            item.Detail = "Finding the video…";
            await StreamEngine.UpdateIfDueAsync(tools, false, ct);
            var probe = await StreamEngine.ProbeAsync(tools, urls, request.Referer, _settings.StreamCookiesFromBrowser, ct);
            if (probe.Url == null)
            {
                // Sites change often; an out-of-date yt-dlp is the most common reason. Update once and retry.
                item.Detail = "Updating the video engine and trying again…";
                if (await StreamEngine.UpdateIfDueAsync(tools, true, ct))
                    probe = await StreamEngine.ProbeAsync(tools, urls, request.Referer, _settings.StreamCookiesFromBrowser, ct);
            }
            if (probe.Url == null)
            {
                MessageBox.Show(this,
                    "Shigaflow could not find a downloadable video on this page.\n\n" +
                    (string.IsNullOrWhiteSpace(probe.Error) ? "The video engine gave no reason." : probe.Error),
                    "Video not found", MessageBoxButton.OK, MessageBoxImage.Information);
                DiscardStreamPlaceholder(item);
                return;
            }

            string title = !string.IsNullOrWhiteSpace(probe.Title) ? probe.Title!
                : string.IsNullOrWhiteSpace(browserTitle) ? "video" : browserTitle;
            string suggested = SanitizeFileName(title);
            if (suggested.Length > 150) suggested = suggested[..150];
            suggested = suggested.TrimEnd('.', ' ');
            if (suggested.Length == 0) suggested = "video";

            var dialog = new SaveFileDialog
            {
                Title = audioOnly ? "Save audio" : "Save video",
                FileName = suggested + "." + defaultExtension,
                Filter = audioOnly
                    ? (defaultExtension == "mp3" ? "MP3 audio (*.mp3)|*.mp3|All files (*.*)|*.*" : "M4A audio (*.m4a)|*.m4a|All files (*.*)|*.*")
                    : "MP4 video (*.mp4)|*.mp4|MKV video (*.mkv)|*.mkv|WebM video (*.webm)|*.webm|All files (*.*)|*.*",
                AddExtension = false,
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true) { DiscardStreamPlaceholder(item); return; }

            string chosen = dialog.FileName;
            string extension = Path.GetExtension(chosen).TrimStart('.').ToLowerInvariant();
            if (audioOnly) { if (extension != defaultExtension) chosen = Path.ChangeExtension(chosen, defaultExtension); }
            else if (extension is not ("mp4" or "mkv" or "webm")) chosen = Path.ChangeExtension(chosen, "mp4");
            chosen = GetUniquePath(chosen);
            string mergeFormat = Path.GetExtension(chosen).TrimStart('.').ToLowerInvariant();

            item.Name = Path.GetFileName(chosen);
            item.Url = probe.Url;
            item.DestinationPath = chosen;
            StartStreamDownload(item, tools, mergeFormat);
        }
        catch (OperationCanceledException)
        {
            DiscardStreamPlaceholder(item);
        }
        catch (Exception ex)
        {
            DiscardStreamPlaceholder(item);
            MessageBox.Show(this, "Shigaflow could not start the video download:\n\n" + ex.Message,
                "Video download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DiscardStreamPlaceholder(DownloadItem item)
    {
        item.IsBusy = false;
        _items.Remove(item);
        UpdateSummary();
    }

    private async Task<StreamTools?> EnsureStreamToolsAsync(DownloadItem item, bool needDeno)
    {
        await _streamToolsGate.WaitAsync();
        try
        {
            var tools = StreamEngine.Locate(needDeno);
            if (tools != null) return tools;

            string list = string.Join("\n", StreamEngine.MissingTools(needDeno).Select(name => "  •  " + name));
            var answer = MessageBox.Show(this,
                "To download videos from YouTube, Facebook and streaming sites, Shigaflow needs these free helper programs:\n\n" +
                list +
                "\n\nShigaflow can download them now from their official GitHub releases (roughly 150 MB in total) into:\n" +
                StreamEngine.ToolsDirectory + "\n\nDownload them now?",
                "Video download components", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return null;

            try
            {
                await StreamEngine.InstallMissingAsync(needDeno,
                    text => Dispatcher.BeginInvoke(new Action(() => item.Detail = text)), item.Cts!.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "The helper programs could not be downloaded:\n\n" + ex.Message +
                    "\n\nYou can also place yt-dlp.exe, ffmpeg.exe (and deno.exe for YouTube) in this folder yourself:\n" +
                    StreamEngine.ToolsDirectory,
                    "Video download components", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            tools = StreamEngine.Locate(needDeno);
            if (tools == null)
                MessageBox.Show(this, "The helper programs were downloaded but could not be found in:\n" + StreamEngine.ToolsDirectory,
                    "Video download components", MessageBoxButton.OK, MessageBoxImage.Warning);
            return tools;
        }
        finally
        {
            _streamToolsGate.Release();
        }
    }

    private void StartStreamDownload(DownloadItem item, StreamTools tools, string mergeFormat)
    {
        if (item.Cts == null || item.Cts.IsCancellationRequested) item.Cts = new CancellationTokenSource();
        item.PauseRequested = false;
        item.IsBusy = true;
        item.Status = "Downloading";
        item.Speed = "—";
        item.Detail = "Connecting…";
        item.Worker = RunStreamDownloadAsync(item, tools, mergeFormat);
    }

    private async Task RunStreamDownloadAsync(DownloadItem item, StreamTools tools, string mergeFormat)
    {
        string directory = Path.GetDirectoryName(item.DestinationPath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(item.DestinationPath);
        string basePath = Path.Combine(directory, baseName);
        // The video and the audio are separate downloads, so the raw percentage restarts; the bar never goes backwards.
        double best = item.Progress;
        try
        {
            var result = await StreamEngine.DownloadAsync(tools, item.Url, basePath, mergeFormat, item.StreamQuality, item.Referer,
                _settings.StreamCookiesFromBrowser,
                p => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (item.Status != "Downloading") return;
                    item.DownloadedBytes = p.Downloaded;
                    if (p.Total is > 0) item.TotalBytes = p.Total;
                    if (p.Percent >= 0)
                    {
                        best = Math.Max(best, Math.Min(99d, p.Percent));
                        item.Progress = best;
                    }
                    item.Speed = p.Speed is > 0 ? DownloadService.FormatSpeed(p.Speed.Value) : "—";
                    item.Detail = item.TotalBytes is > 0
                        ? $"{FormatBytes(p.Downloaded)} of {FormatBytes(item.TotalBytes.Value)}"
                        : $"{FormatBytes(p.Downloaded)} downloaded";
                })),
                item.Cts!.Token);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"The video engine stopped with exit code {result.ExitCode}."
                    : result.Error);

            string finalPath = result.FinalPath ?? "";
            if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
            {
                finalPath = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, baseName + ".*")
                        .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault() ?? ""
                    : "";
            }
            if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
                throw new IOException("The video engine finished, but the saved file was not found.");

            long size = new FileInfo(finalPath).Length;
            item.DestinationPath = finalPath;
            item.Name = Path.GetFileName(finalPath);
            item.TotalBytes = size;
            item.DownloadedBytes = size;
            item.Status = "Complete";
            item.Progress = 100;
            item.Speed = "—";
            item.Detail = $"Saved to {finalPath}";
            item.IsBusy = false;
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            item.Status = item.PauseRequested ? "Paused" : "Cancelled";
            item.Speed = "—";
            item.Detail = item.PauseRequested ? "Paused — the partial video is kept" : "Download cancelled";
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

    // yt-dlp keeps its partial files and continues from them, so Pause = stop the process, Resume = start it again.
    private void PauseStreamItem(DownloadItem item)
    {
        if (!item.IsBusy) return;
        item.PauseRequested = true;
        item.Status = "Paused";
        item.Speed = "—";
        item.Detail = "Pausing…";
        item.Cts?.Cancel();
    }

    private void ResumeStreamItem(DownloadItem item)
    {
        if (item.IsBusy || item.Status is not ("Paused" or "Cancelled" or "Failed")) return;
        var tools = StreamEngine.Locate(StreamEngine.IsYouTubeUrl(item.Url));
        if (tools == null)
        {
            item.Status = "Failed";
            item.Detail = "The video helper programs are missing. Click the Download video button in the browser again to reinstall them.";
            return;
        }
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();
        string extension = Path.GetExtension(item.DestinationPath).TrimStart('.').ToLowerInvariant();
        string mergeFormat = extension is "mp4" or "mkv" or "webm" ? extension : "mp4";
        StartStreamDownload(item, tools, mergeFormat);
    }

    private static void CleanupStreamLeftovers(DownloadItem item)
    {
        try
        {
            if (item.Status == "Complete") return;
            string directory = Path.GetDirectoryName(item.DestinationPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(item.DestinationPath);
            if (baseName.Length == 0 || !Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, baseName + ".*"))
            {
                if (string.Equals(file, item.DestinationPath, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(file);
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(name, @"\.f\d{1,4}(?:-\d+)?\.[A-Za-z0-9]+$"))
                    File.Delete(file);
            }
        }
        catch { }
    }
}

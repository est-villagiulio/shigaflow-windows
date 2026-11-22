using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace Shigaflow;

/// <summary>Where the helper programs of the video engine were found.</summary>
internal sealed class StreamTools
{
    public string YtDlpPath { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public string? DenoPath { get; init; }
    /// <summary>True when yt-dlp lives in Shigaflow's own tools folder, so Shigaflow may keep it up to date.</summary>
    public bool YtDlpIsManaged { get; init; }
}

internal sealed class StreamProgress
{
    /// <summary>Overall percent over every stream downloaded so far, or -1 when the size is not known yet.</summary>
    public double Percent { get; init; } = -1;
    public long Downloaded { get; init; }
    public long? Total { get; init; }
    public double? Speed { get; init; }
}

internal sealed class StreamRunResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public string? FinalPath { get; init; }
}

/// <summary>
/// Video engine: runs yt-dlp (which understands YouTube, Facebook, HLS .m3u8 and DASH .mpd) and FFmpeg
/// (which joins separate video and audio streams). yt-dlp needs the Deno JavaScript runtime for YouTube.
/// The three programs are downloaded once, with the user's permission, into %LOCALAPPDATA%\Shigaflow\tools.
/// </summary>
internal static class StreamEngine
{
    public static readonly string ToolsDirectory = Path.Combine(AppPaths.DataRoot, "tools");

    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private static readonly ConcurrentDictionary<Process, byte> Running = new();
    private static readonly HttpClient Http = CreateHttpClient();
    private static bool _updateAttemptedThisSession;

    static StreamEngine()
    {
        // Never leave a yt-dlp / FFmpeg process running after Shigaflow exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shigaflow");
        return client;
    }

    public static void KillAll()
    {
        foreach (var process in Running.Keys.ToArray())
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }
    }

    public static string NormalizeQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality)) return "best";
        quality = quality.Trim().ToLowerInvariant();
        if (quality is "best" or "audio-mp3" or "audio-m4a") return quality;
        return quality.Length is >= 4 and <= 5 && quality[0] == 'h' && int.TryParse(quality[1..], out int h) && h is >= 144 and <= 4320
            ? quality : "best";
    }

    public static bool IsAudioQuality(string quality) => quality is "audio-mp3" or "audio-m4a";

    /// <summary>File extension of the finished download for a quality choice.</summary>
    public static string ExtensionFor(string quality) => quality switch { "audio-mp3" => "mp3", "audio-m4a" => "m4a", _ => "mp4" };

    public static bool IsYouTubeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com") ||
               host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com");
    }

    // ----- finding the helper programs -----

    private static string? FindTool(string fileName)
    {
        var dirs = new List<string> { ToolsDirectory, Path.Combine(AppContext.BaseDirectory, "tools"), AppContext.BaseDirectory };
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            dirs.Add(dir.Trim('"'));
        foreach (string dir in dirs)
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static StreamTools? Locate(bool needDeno)
    {
        string? ytDlp = FindTool("yt-dlp.exe");
        string? ffmpeg = FindTool("ffmpeg.exe");
        string? deno = FindTool("deno.exe");
        if (ytDlp == null || ffmpeg == null) return null;
        if (needDeno && deno == null) return null;
        return new StreamTools
        {
            YtDlpPath = ytDlp,
            FfmpegPath = ffmpeg,
            DenoPath = deno,
            YtDlpIsManaged = string.Equals(Path.GetDirectoryName(ytDlp), ToolsDirectory, StringComparison.OrdinalIgnoreCase)
        };
    }

    public static List<string> MissingTools(bool needDeno)
    {
        var missing = new List<string>();
        if (FindTool("yt-dlp.exe") == null) missing.Add("yt-dlp  (reads YouTube, Facebook, HLS and DASH videos)");
        if (FindTool("ffmpeg.exe") == null) missing.Add("FFmpeg  (joins the video and audio parts into one file)");
        if (needDeno && FindTool("deno.exe") == null) missing.Add("Deno  (lets yt-dlp read YouTube)");
        return missing;
    }

    // ----- installing / updating -----

    public static async Task InstallMissingAsync(bool needDeno, Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);

        if (FindTool("yt-dlp.exe") == null)
            await DownloadFileAsync(YtDlpUrl, Path.Combine(ToolsDirectory, "yt-dlp.exe"), "yt-dlp", status, ct);

        if (FindTool("ffmpeg.exe") == null)
        {
            string zipPath = Path.Combine(ToolsDirectory, "ffmpeg.zip.download");
            await DownloadFileAsync(FfmpegUrl, zipPath, "FFmpeg", status, ct);
            status("Unpacking FFmpeg…");
            ExtractFromZip(zipPath, ToolsDirectory, "ffmpeg.exe", "ffprobe.exe");
            try { File.Delete(zipPath); } catch { }
            if (!File.Exists(Path.Combine(ToolsDirectory, "ffmpeg.exe")))
                throw new IOException("ffmpeg.exe was not found inside the downloaded FFmpeg package.");
        }

        if (needDeno && FindTool("deno.exe") == null)
        {
            string zipPath = Path.Combine(ToolsDirectory, "deno.zip.download");
            await DownloadFileAsync(DenoUrl, zipPath, "Deno", status, ct);
            status("Unpacking Deno…");
            ExtractFromZip(zipPath, ToolsDirectory, "deno.exe");
            try { File.Delete(zipPath); } catch { }
            if (!File.Exists(Path.Combine(ToolsDirectory, "deno.exe")))
                throw new IOException("deno.exe was not found inside the downloaded Deno package.");
        }
    }

    private static async Task DownloadFileAsync(string url, string destination, string label, Action<string> status, CancellationToken ct)
    {
        string temp = destination + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            DateTime lastReport = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(300))
                {
                    lastReport = DateTime.UtcNow;
                    status(total is > 0
                        ? $"Downloading {label}… {done * 100 / total.Value}%"
                        : $"Downloading {label}… {done / (1024 * 1024)} MB");
                }
            }
        }
        File.Move(temp, destination, overwrite: true);
    }

    private static void ExtractFromZip(string zipPath, string targetDirectory, params string[] fileNames)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (string wanted in fileNames)
        {
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            string temp = Path.Combine(targetDirectory, wanted + ".part");
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, Path.Combine(targetDirectory, wanted), overwrite: true);
        }
    }

    /// <summary>
    /// Sites change often, so yt-dlp is updated now and then (at most every 3 days, or when forced after a failure).
    /// Only the copy in Shigaflow's own tools folder is touched. Returns true when an update check actually ran.
    /// </summary>
    public static async Task<bool> UpdateIfDueAsync(StreamTools tools, bool force, CancellationToken ct)
    {
        if (!tools.YtDlpIsManaged || _updateAttemptedThisSession) return false;
        string marker = Path.Combine(ToolsDirectory, "yt-dlp.checked");
        if (!force)
        {
            try
            {
                if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(3)) return false;
            }
            catch { }
        }
        _updateAttemptedThisSession = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await RunCaptureAsync(tools, new List<string> { "-U" }, timeout.Token);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch { }
        return true;
    }

    // ----- running yt-dlp -----

    private static ProcessStartInfo CreateStartInfo(StreamTools tools, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        // yt-dlp finds FFmpeg and Deno through PATH.
        var dirs = new List<string>();
        foreach (string? tool in new[] { tools.FfmpegPath, tools.DenoPath })
        {
            string? dir = tool == null ? null : Path.GetDirectoryName(tool);
            if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
        }
        psi.Environment.TryGetValue("PATH", out string? existingPath);
        psi.Environment["PATH"] = string.Join(';', dirs) + ";" + (existingPath ?? "");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    private static string? RefererFor(string url, string? referer)
    {
        // A page needs no Referer for itself; a playlist or video file on another host usually does.
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var r) || (r.Scheme != Uri.UriSchemeHttp && r.Scheme != Uri.UriSchemeHttps)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        static string Plain(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return string.Equals(Plain(r.Host), Plain(u.Host), StringComparison.OrdinalIgnoreCase) ? null : r.AbsoluteUri;
    }

    private static List<string> BaseArgs(StreamTools tools, string? referer, string? cookiesFromBrowser)
    {
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--playlist-items", "1", "--no-warnings", "--no-colors",
            "--ffmpeg-location", Path.GetDirectoryName(tools.FfmpegPath)!,
            "--socket-timeout", "30", "--retries", "10", "--fragment-retries", "10"
        };
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
        {
            args.Add("--referer"); args.Add(r.AbsoluteUri);
            args.Add("--add-header"); args.Add("Origin:" + r.GetLeftPart(UriPartial.Authority));
        }
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser"); args.Add(cookiesFromBrowser.Trim());
        }
        return args;
    }

    private static string FriendlyError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string line = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault() ?? "";
        line = line.Replace("ERROR: ", "", StringComparison.OrdinalIgnoreCase).Trim();
        string lower = line.ToLowerInvariant();
        if (lower.Contains("sign in to confirm") || lower.Contains("cookies") || lower.Contains("login required") ||
            lower.Contains("private video") || lower.Contains("log in") || lower.Contains("members-only"))
            line += " — this video needs a signed-in browser. Set \"StreamCookiesFromBrowser\" (for example \"firefox\" or \"edge\") in Shigaflow's settings.json and try again.";
        else if (lower.Contains("unsupported url"))
            line += " — Shigaflow's video engine does not recognise a video on this page.";
        else if (lower.Contains("drm"))
            line += " — DRM-protected videos cannot be downloaded.";
        return line.Replace("\r", " ").Replace("\n", " ");
    }

    private static async Task<StreamRunResult> RunCaptureAsync(StreamTools tools, List<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(tools, args) };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        process.Start();
        Running[process] = 0;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            string errorText, outputText;
            lock (error) errorText = error.ToString();
            lock (output) outputText = output.ToString();
            return new StreamRunResult { ExitCode = process.ExitCode, Output = outputText, Error = FriendlyError(errorText) };
        }
        finally
        {
            Running.TryRemove(process, out _);
        }
    }

    /// <summary>Asks yt-dlp whether it can read a video at one of the addresses, and for the video's title.</summary>
    public static async Task<(string? Title, string? Url, string Error)> ProbeAsync(
        StreamTools tools, IReadOnlyList<string> urls, string? referer, string? cookiesFromBrowser, CancellationToken ct)
    {
        string lastError = "";
        foreach (string url in urls)
        {
            var args = BaseArgs(tools, RefererFor(url, referer), cookiesFromBrowser);
            args.AddRange(new[] { "--skip-download", "--print", "%(title)s", "--", url });
            StreamRunResult result;
            try { result = await RunCaptureAsync(tools, args, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex.Message; continue; }
            if (result.ExitCode == 0)
            {
                string title = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? "";
                return (title == "NA" ? "" : title, url, "");
            }
            lastError = result.Error;
        }
        return (null, null, lastError);
    }

    private static long? ParseNumber(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0)
            return (long)value;
        return null;
    }

    /// <summary>Downloads the video (best quality, audio and video merged) to basePathNoExt + the chosen container.</summary>
    public static async Task<StreamRunResult> DownloadAsync(
        StreamTools tools, string url, string basePathNoExt, string mergeFormat, string quality, string? referer, string? cookiesFromBrowser,
        Action<StreamProgress> onProgress, CancellationToken ct)
    {
        quality = NormalizeQuality(quality);
        var args = BaseArgs(tools, RefererFor(url, referer), cookiesFromBrowser);
        if (IsAudioQuality(quality))
        {
            // Audio only: best audio stream, converted (or just remuxed) to the chosen format.
            args.AddRange(new[]
            {
                "-f", quality == "audio-m4a" ? "ba[ext=m4a]/ba/b" : "ba/b",
                "-x", "--audio-format", quality == "audio-mp3" ? "mp3" : "m4a", "--audio-quality", "0"
            });
        }
        else
        {
            // "best" = highest resolution; "h1080" = highest up to 1080p. Among equals prefer MP4 video and M4A audio.
            string sort = quality == "best" ? "res,ext:mp4:m4a" : $"res:{quality[1..]},ext:mp4:m4a";
            args.AddRange(new[] { "-S", sort, "--merge-output-format", mergeFormat });
            if (mergeFormat == "mp4") { args.Add("--remux-video"); args.Add("mp4"); }
        }
        args.AddRange(new[]
        {
            "-o", basePathNoExt.Replace("%", "%%") + ".%(ext)s",
            "--concurrent-fragments", "8",
            "--no-mtime",
            "--newline", "--progress",
            "--progress-template",
            "download:SFP|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
            "--print", "after_move:SFF|%(filepath)s"
        });
        args.Add("--");
        args.Add(url);

        using var process = new Process { StartInfo = CreateStartInfo(tools, args) };
        var error = new StringBuilder();
        string? finalPath = null;
        var streams = new Dictionary<string, (long Done, long Total)>();

        bool HandleLine(string? line)
        {
            if (line == null) return true;
            if (line.StartsWith("SFF|", StringComparison.Ordinal))
            {
                finalPath = line[4..].Trim();
                return true;
            }
            if (!line.StartsWith("SFP|", StringComparison.Ordinal)) return false;
            string[] parts = line.Split('|');
            if (parts.Length < 8) return true;
            long done = ParseNumber(parts[2]) ?? 0;
            long? total = ParseNumber(parts[3]) ?? ParseNumber(parts[4]);
            double? speed = ParseNumber(parts[5]);
            long sumDone, sumTotal;
            lock (streams)
            {
                streams[parts[7]] = (done, total ?? 0);
                sumDone = streams.Values.Sum(v => v.Done);
                sumTotal = streams.Values.Sum(v => v.Total);
            }
            onProgress(new StreamProgress
            {
                Percent = sumTotal > 0 ? Math.Min(100d, sumDone * 100d / sumTotal) : -1,
                Downloaded = sumDone,
                Total = sumTotal > 0 ? sumTotal : null,
                Speed = speed
            });
            return true;
        }

        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null || HandleLine(e.Data)) return;
            lock (error) error.AppendLine(e.Data);
        };
        process.Start();
        Running[process] = 0;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            string errorText;
            lock (error) errorText = error.ToString();
            return new StreamRunResult { ExitCode = process.ExitCode, Error = FriendlyError(errorText), FinalPath = finalPath };
        }
        finally
        {
            Running.TryRemove(process, out _);
        }
    }
}

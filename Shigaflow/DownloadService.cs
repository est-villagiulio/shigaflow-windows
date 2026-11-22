using System.Net;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

namespace Shigaflow;

internal sealed class DownloadService
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpClient? CreateCookieClient(IReadOnlyCollection<BrowserCookie>? browserCookies, string? referer = null)
    {
        bool hasCookies = browserCookies is { Count: > 0 };
        bool hasReferer = Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)
                          && (refererUri.Scheme == Uri.UriSchemeHttp || refererUri.Scheme == Uri.UriSchemeHttps);
        if (!hasCookies && !hasReferer) return null;
        var container = new CookieContainer();
        foreach (var source in browserCookies ?? Array.Empty<BrowserCookie>())
        {
            // Only accept Google cookies. Never forward browser cookies to arbitrary download hosts.
            string domain = (source.Domain ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (!IsAllowedGoogleCookieDomain(domain)) continue;
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Any(ch => char.IsControl(ch) || ch is ';' or ',')) continue;
            try
            {
                var cookie = new Cookie(source.Name, source.Value ?? "", string.IsNullOrWhiteSpace(source.Path) ? "/" : source.Path,
                    source.Domain ?? domain)
                {
                    Secure = source.Secure,
                    HttpOnly = source.HttpOnly
                };
                if (source.ExpirationDate is > 0)
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)source.ExpirationDate.Value).UtcDateTime;
                container.Add(cookie);
            }
            catch { /* Ignore malformed or expired browser cookies. */ }
        }
        var client = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = container,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        // Videos and other hotlink-protected files are often served only when the request names the page they came from.
        if (hasReferer) client.DefaultRequestHeaders.Referrer = refererUri;
        return client;
    }

    public async Task<(bool CanResume, long? Length, string? FileName, string? ContentType)> ProbeAsync(string url, CancellationToken ct, IReadOnlyCollection<BrowserCookie>? browserCookies = null, string? referer = null)
    {
        using var cookieClient = CreateCookieClient(browserCookies, referer);
        var client = cookieClient ?? Client;
        return await ProbeWithClientAsync(client, url, ct);
    }

    private static async Task<(bool CanResume, long? Length, string? FileName, string? ContentType)> ProbeWithClientAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var response = await SendDownloadRequestAsync(client, url, new RangeHeaderValue(0, 0), ct);
            // Report web pages (login/confirmation pages, folders, expired links) as text/html so the
            // caller can explain it, instead of hiding it behind an empty probe result.
            if (IsHtmlResponse(response))
                return (false, null, null, "text/html");

            long? length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName)) fileName = fileName.Trim().Trim('"');
            bool supports = response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.From == 0
                && response.Content.Headers.ContentRange?.To == 0
                && response.Content.Headers.ContentRange?.Length is > 0;
            return (supports, length, fileName, response.Content.Headers.ContentType?.MediaType);
        }
        catch { return (false, null, null, null); }
    }

    private static bool IsAllowedGoogleCookieDomain(string domain) =>
        domain == "google.com" || domain.EndsWith(".google.com", StringComparison.Ordinal) ||
        domain == "googleusercontent.com" || domain.EndsWith(".googleusercontent.com", StringComparison.Ordinal);

    private static bool IsGoogleDownloadHost(string host) =>
        host.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtmlResponse(HttpResponseMessage response)
    {
        bool html = response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true ||
            response.Content.Headers.ContentType?.MediaType?.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) == true;
        if (!html) return false;
        // A user may legitimately download an .html document. Treat it as a web
        // interstitial only when the server did not mark it as an attachment.
        var disposition = response.Content.Headers.ContentDisposition;
        return !string.Equals(disposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(disposition?.FileName)
            && string.IsNullOrWhiteSpace(disposition?.FileNameStar);
    }

    // Google Drive may first return a small HTML confirmation page (rather than the file).
    // Follow its hidden GET form while preserving the byte-range request used by the probe
    // and the segmented downloader. Never save that HTML page as if it were the file.
    private static async Task<HttpResponseMessage> SendDownloadRequestAsync(HttpClient client, string url, RangeHeaderValue? range, CancellationToken ct)
    {
        var current = new Uri(url, UriKind.Absolute);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (range != null) request.Headers.Range = range;
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUri = response.RequestMessage?.RequestUri ?? current;
            if (!IsGoogleDownloadHost(finalUri.Host) || !IsHtmlResponse(response)) return response;

            string html;
            try { html = await response.Content.ReadAsStringAsync(ct); }
            finally { response.Dispose(); }

            var nextUrl = ExtractGoogleDriveConfirmationUrl(html, finalUri);
            if (nextUrl == null)
                throw new InvalidDataException("Google Drive returned a confirmation, quota, or sign-in page instead of the video. Reload Google Drive, sign in if needed, and capture the Download button again.");
            current = nextUrl;
        }
        throw new InvalidDataException("Google Drive kept returning confirmation pages; the file download URL could not be resolved.");
    }

    private static Uri? ExtractGoogleDriveConfirmationUrl(string html, Uri pageUri)
    {
        var form = Regex.Match(html, @"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!form.Success) return null;

        var attrs = form.Groups["attrs"].Value;
        var actionMatch = Regex.Match(attrs, @"\baction\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!actionMatch.Success) return null;
        var methodMatch = Regex.Match(attrs, @"\bmethod\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase);
        if (methodMatch.Success && !methodMatch.Groups["value"].Value.Equals("get", StringComparison.OrdinalIgnoreCase)) return null;

        Uri action;
        try { action = new Uri(pageUri, WebUtility.HtmlDecode(actionMatch.Groups["value"].Value)); }
        catch { return null; }
        if (!IsGoogleDownloadHost(action.Host)) return null;

        var queryParts = new List<string>();
        var existingQuery = action.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(existingQuery)) queryParts.Add(existingQuery);
        string body = form.Groups["body"].Value;
        foreach (Match input in Regex.Matches(body, @"<input\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var inputAttrs = input.Groups["attrs"].Value;
            var name = Regex.Match(inputAttrs, @"\bname\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!name.Success) continue;
            var value = Regex.Match(inputAttrs, @"\bvalue\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string key = WebUtility.HtmlDecode(name.Groups["value"].Value);
            string val = value.Success ? WebUtility.HtmlDecode(value.Groups["value"].Value) : "";
            queryParts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(val));
        }
        if (queryParts.Count == 0) return null;
        var builder = new UriBuilder(action) { Query = string.Join("&", queryParts) };
        return builder.Uri;
    }

    public async Task DownloadAsync(DownloadItem item, string url, string target, int connections, IProgress<(double overall, int segment, double segmentProgress, long bytes)> progress, CancellationToken ct, DownloadSpeedLimiter? limiter = null, IReadOnlyCollection<BrowserCookie>? browserCookies = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var cookieClient = CreateCookieClient(browserCookies, item.Referer);
        var client = cookieClient ?? Client;
        var probe = await ProbeWithClientAsync(client, url, ct);
        item.Resume = probe.CanResume ? "Yes" : "No";
        item.TotalBytes = probe.Length;
        if (probe.Length is > 0 && probe.CanResume && connections > 1)
        {
            try
            {
                await DownloadSegmentedAsync(client, item, url, target, probe.Length.Value, connections, progress, ct, limiter);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
            {
                // Range-capable servers sometimes return malformed/short partial responses at the very start.
                // In that case discard the (tiny) segments and retry once as a normal single-stream download.
                // A failure LATE in the download (for example a dropped connection at 99%) must never throw
                // away the data already saved: keep the parts, report the failure, and let Resume continue.
                var partsFolder = target + ".parts";
                long saved = 0;
                try
                {
                    if (Directory.Exists(partsFolder))
                        saved = Directory.EnumerateFiles(partsFolder, "*.part").Sum(f => new FileInfo(f).Length);
                }
                catch { }
                if (saved >= Math.Max(1024 * 1024, probe.Length.Value / 20)) throw;

                if (Directory.Exists(partsFolder)) Directory.Delete(partsFolder, recursive: true);
                item.Segments.Clear();
                item.Segments.Add(new SegmentItem { Index = 1 });
                item.Resume = "No";
                item.Detail = "Server range response was incomplete; retrying as a single stream…";
                await DownloadSingleAsync(client, item, url, target, progress, ct, limiter);
                return;
            }
        }
        item.Segments.Clear();
        item.Segments.Add(new SegmentItem { Index = 1 });
        await DownloadSingleAsync(client, item, url, target, progress, ct, limiter);
    }

    private async Task DownloadSingleAsync(HttpClient client, DownloadItem item, string url, string target, IProgress<(double overall, int segment, double segmentProgress, long bytes)> progress, CancellationToken ct, DownloadSpeedLimiter? limiter)
    {
        long existing = File.Exists(target) ? new FileInfo(target).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0 && item.Resume == "Yes") req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var response = await SendDownloadRequestAsync(client, url, req.Headers.Range, ct);
        if (IsHtmlResponse(response))
            throw new InvalidDataException("The server returned a web page instead of the requested file; no HTML page was saved as a download.");
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
        if (existing == 0 && File.Exists(target)) File.Delete(target);
        // Content-Range carries the complete object length for resumed 206 responses.
        // Content-Length alone is only the remaining response body in that case.
        long? total = response.Content.Headers.ContentRange?.Length;
        if (!total.HasValue && response.Content.Headers.ContentLength.HasValue)
            total = response.Content.Headers.ContentLength.Value + existing;
        item.TotalBytes = total;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = new FileStream(target, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        long done = existing;
        long lastBytes = 0;
        var lastTime = DateTime.UtcNow;
        while (true)
        {
            await WaitIfPaused(item, ct);
            int n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) break;
            if (limiter != null) await limiter.WaitForTurnAsync(n, ct);
            await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            var totalBytes = item.TotalBytes;
            double p = totalBytes is > 0 ? Math.Clamp(done * 100d / totalBytes.Value, 0, 100) : 0;
            progress.Report((p, 1, p, done));
            var now = DateTime.UtcNow;
            var elapsed = (now - lastTime).TotalSeconds;
            if (elapsed >= 1) { item.Speed = FormatSpeed((done - lastBytes) / elapsed); lastBytes = done; lastTime = now; }
        }
        if (item.TotalBytes is > 0 && done != item.TotalBytes.Value)
            throw new IOException($"The server ended the download early ({done:N0} bytes received; expected {item.TotalBytes.Value:N0} bytes). The file was not marked complete.");
        progress.Report((100, 1, 100, done));
    }

    private async Task DownloadSegmentedAsync(HttpClient client, DownloadItem item, string url, string target, long length, int connections, IProgress<(double overall, int segment, double segmentProgress, long bytes)> progress, CancellationToken ct, DownloadSpeedLimiter? limiter)
    {
        connections = Math.Clamp(connections, 2, 16);
        var tempFolder = target + ".parts";
        // Parts left by an earlier attempt are reused only when they belong to the same layout (size + connections).
        string layoutFile = Path.Combine(tempFolder, "layout.txt");
        string layout = $"{length}|{connections}";
        try
        {
            if (Directory.Exists(tempFolder) && (!File.Exists(layoutFile) || File.ReadAllText(layoutFile) != layout))
                Directory.Delete(tempFolder, recursive: true);
        }
        catch { }
        Directory.CreateDirectory(tempFolder);
        File.WriteAllText(layoutFile, layout);
        var segmentLength = (long)Math.Ceiling(length / (double)connections);
        var ranges = new List<(long start, long end)>();
        for (int i = 0; i < connections; i++)
        {
            long start = i * segmentLength;
            if (start >= length) break;
            ranges.Add((start, Math.Min(length - 1, start + segmentLength - 1)));
        }
        item.Segments.Clear();
        foreach (var _ in ranges) item.Segments.Add(new SegmentItem { Index = item.Segments.Count + 1 });
        var segmentDone = new long[ranges.Count];
        var started = DateTime.UtcNow;
        long speedBytes = 0;
        var speedAt = DateTime.UtcNow;
        var speedGate = new object();

        var tasks = ranges.Select(async (range, i) =>
        {
            var partPath = Path.Combine(tempFolder, $"{i:D3}.part");
            long segmentSize = range.end - range.start + 1;
            long PartLength() => File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (PartLength() > segmentSize) File.Delete(partPath);
            segmentDone[i] = PartLength();
            item.Segments[i].Progress = Math.Clamp(segmentDone[i] * 100d / segmentSize, 0, 100);

            // A dropped connection, timeout or short answer only costs a retry of THIS segment's missing bytes,
            // never the bytes already saved (and never a restart of the whole file).
            int failures = 0;
            while (PartLength() < segmentSize)
            {
                ct.ThrowIfCancellationRequested();
                long done = PartLength();
                long before = done;
                try
                {
                    long from = range.start + done;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, range.end);
                    using var response = await SendDownloadRequestAsync(client, url, request.Headers.Range, ct);
                    if (IsHtmlResponse(response))
                        throw new InvalidDataException("The server returned a web page instead of the requested file.");
                    var contentRange = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        contentRange?.From != from || contentRange?.To != range.end || contentRange?.Length != length)
                        throw new InvalidOperationException("The server returned an invalid byte range. Retrying as a single stream.");
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var dest = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 128 * 1024, true);
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        await WaitIfPaused(item, ct);
                        int n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                        if (n == 0) break;
                        if (limiter != null) await limiter.WaitForTurnAsync(n, ct);
                        await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        segmentDone[i] = done;
                        double segPct = Math.Clamp(done * 100d / segmentSize, 0, 100);
                        double overall = Math.Clamp(segmentDone.Sum() * 100d / length, 0, 100);
                        item.Segments[i].Progress = segPct;
                        progress.Report((overall, i + 1, segPct, segmentDone.Sum()));
                        lock (speedGate)
                        {
                            speedBytes += n;
                            var now = DateTime.UtcNow;
                            if ((now - speedAt).TotalSeconds >= 1)
                            {
                                item.Speed = FormatSpeed(speedBytes / (now - speedAt).TotalSeconds);
                                speedBytes = 0; speedAt = now;
                            }
                        }
                    }
                    if (done < segmentSize) throw new IOException($"Segment {i + 1} ended early.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    (ex is IOException or HttpRequestException or TaskCanceledException or InvalidOperationException))
                {
                    // Nothing saved yet and the server answers with a bad range: let the caller fall back to one stream.
                    if (ex is InvalidOperationException && before == 0 && PartLength() == 0) throw;
                    // Count only attempts that made no progress; a connection that got further earns a fresh budget.
                    failures = PartLength() > before ? 1 : failures + 1;
                    if (failures > 8) throw;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10000, 500 * failures * failures)), ct);
                }
            }
            segmentDone[i] = segmentSize;
            item.Segments[i].Progress = 100;
        }).ToArray();

        await Task.WhenAll(tasks);
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            foreach (var range in ranges.Select((r, i) => (r, i)))
            {
                await using var part = new FileStream(Path.Combine(tempFolder, $"{range.i:D3}.part"), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                await part.CopyToAsync(output, ct);
            }
        }
        try { Directory.Delete(tempFolder, true); } catch { }
        progress.Report((100, ranges.Count, 100, length));
    }

    private static async Task WaitIfPaused(DownloadItem item, CancellationToken ct)
    {
        Task? wait = null;
        lock (item.Gate)
        {
            if (item.PauseRequested)
            {
                item.ResumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = item.ResumeSignal.Task;
            }
        }
        if (wait != null) await wait.WaitAsync(ct);
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double size = bytesPerSecond; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.0} {units[unit]}";
    }
}

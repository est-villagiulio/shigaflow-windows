using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            byte[] header = new byte[4];
            if (!await ReadExactAsync(input, header)) return 1;
            int length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > 1024 * 1024) return 1;
            byte[] payload = new byte[length];
            if (!await ReadExactAsync(input, payload)) return 1;
            var request = JsonSerializer.Deserialize<NativeRequest>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request == null || string.IsNullOrWhiteSpace(request.Url))
            {
                await WriteResponseAsync(new { ok = false, error = "A download URL is required." });
                return 0;
            }
            bool isStream = string.Equals(request.Mode, "stream", StringComparison.OrdinalIgnoreCase);
            if (isStream)
            {
                // Video-engine requests carry a page or playlist address (no file extension to check). Keep only http(s) addresses.
                request.AlternateUrls = request.AlternateUrls?
                    .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var alt) &&
                        (alt.Scheme == Uri.UriSchemeHttp || alt.Scheme == Uri.UriSchemeHttps))
                    .Take(4).ToList();
            }
            bool isMagnet = request.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
            Uri? uri = null;
            if (!isMagnet && (!Uri.TryCreate(request.Url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                await WriteResponseAsync(new { ok = false, error = "Expected an HTTP/HTTPS URL or magnet link." });
                return 0;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string settingsPath = Path.Combine(localAppData, "Shigaflow", "settings.json");
            // Before the app has run once after upgrading, its settings are still in the folder used by older versions.
            if (!File.Exists(settingsPath))
            {
                string legacySettingsPath = Path.Combine(localAppData, "NightDownload", "settings.json");
                if (File.Exists(legacySettingsPath)) settingsPath = legacySettingsPath;
            }
            string? allowedExtensions = null;
            if (File.Exists(settingsPath))
            {
                try
                {
                    using var settingsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
                    if (settingsDoc.RootElement.TryGetProperty("AllowedExtensions", out var allowedValue))
                        allowedExtensions = allowedValue.GetString();
                }
                catch { }
            }

            string candidateName = isMagnet ? "" : (!string.IsNullOrWhiteSpace(request.FileName)
                ? Path.GetFileName(request.FileName)
                : Path.GetFileName(uri!.AbsolutePath));

            // Defensive normalization: when the extension explicitly marks this
            // as a Google Drive multi-file archive, enforce .zip before the
            // allow-list check and before forwarding to the WPF application.
            // This protects against Drive returning an empty/extensionless name
            // while the archive is still being prepared.
            if (request.GoogleDriveBulk && !isMagnet && !candidateName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                candidateName = string.IsNullOrWhiteSpace(candidateName)
                    ? "Google-Drive-Download.zip"
                    : Path.ChangeExtension(candidateName, ".zip");
                request.FileName = candidateName;
            }

            string extension = Path.GetExtension(candidateName).TrimStart('.').ToLowerInvariant();
            var allowed = (allowedExtensions ?? "")
                .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().TrimStart('.').ToLowerInvariant())
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Do not reject extensionless URLs here. Many download services use opaque
            // endpoints while sending the real filename in Content-Disposition. The WPF
            // app probes the response and applies the user's extension allow-list after
            // it has resolved the actual filename/content type.
            if (!isMagnet && !isStream && !string.IsNullOrWhiteSpace(extension) && !allowed.Contains(extension))
            {
                await WriteResponseAsync(new { ok = false, error = "This file extension is not enabled in Shigaflow Settings." });
                return 0;
            }

            // Find Shigaflow.exe wherever it was installed (the installer lets the user pick any folder).
            // 1) app-path.txt next to this host, 2) app-path.txt in the default data folder (script installs),
            // 3) the installer layout: <install>\NativeHost\<this exe> and <install>\App\Shigaflow.exe.
            string hostDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string[] appPathFiles =
            {
                Path.Combine(hostDir, "app-path.txt"),
                Path.Combine(localAppData, "Shigaflow", "NativeHost", "app-path.txt")
            };
            string? appPath = null;
            foreach (string appPathFile in appPathFiles)
            {
                if (!File.Exists(appPathFile)) continue;
                string candidate = File.ReadAllText(appPathFile).Trim();
                if (File.Exists(candidate)) { appPath = candidate; break; }
            }
            if (appPath == null)
            {
                string sibling = Path.GetFullPath(Path.Combine(hostDir, "..", "App", "Shigaflow.exe"));
                if (File.Exists(sibling)) appPath = sibling;
            }
            if (appPath == null)
                throw new FileNotFoundException("Shigaflow.exe was not found. Run \"Connect Shigaflow to Chrome\" again, or reinstall Shigaflow.");

            bool connected = await TrySendToAppAsync(request, 1800);
            if (!connected)
            {
                Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(appPath)! });
                connected = await TrySendToAppAsync(request, 15000);
            }
            await WriteResponseAsync(connected ? new { ok = true } : new { ok = false, error = "Could not connect to Shigaflow." });
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(new { ok = false, error = ex.Message });
        }
        return 0;
    }

    private static async Task<bool> TrySendToAppAsync(NativeRequest request, int timeoutMs)
    {
        var stopAt = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", "ShigaflowNativeHost", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(300);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(request));
                string? reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return reply != null && reply.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.TimeoutException) { }
            catch (IOException) { }
            await Task.Delay(200);
        }
        return false;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task WriteResponseAsync(object response)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response);
        byte[] header = BitConverter.GetBytes(payload.Length);
        using var output = Console.OpenStandardOutput();
        await output.WriteAsync(header);
        await output.WriteAsync(payload);
        await output.FlushAsync();
    }

    private sealed class NativeRequest
    {
        public NativeRequest() { }
        public string Url { get; set; } = "";
        public string? FileName { get; set; }
        public List<NativeCookie>? Cookies { get; set; }
        // Preserve the bulk-archive marker across the native-host hop. Without
        // this property System.Text.Json drops googleDriveBulk and the WPF app
        // receives an extensionless download instead of a guaranteed .zip name.
        public bool GoogleDriveBulk { get; set; }
        // Page the link was found on (sent for videos; many servers refuse the request without it).
        public string? Referer { get; set; }
        // "stream" = hand the page/playlist address to Shigaflow's video engine (yt-dlp + FFmpeg).
        public string? Mode { get; set; }
        public List<string>? AlternateUrls { get; set; }
        // best | h2160 | h1440 | h1080 | h720 | h480 | h360 | audio-mp3 | audio-m4a
        public string? Quality { get; set; }
    }

    private sealed class NativeCookie
    {
        public NativeCookie() { }
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public double? ExpirationDate { get; set; }
    }
}

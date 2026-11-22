using System.IO;
using System.Text;
using LibtorrentSharp;

namespace Shigaflow;

/// <summary>
/// Owns the native libtorrent session and adapts both magnet and .torrent handles
/// to the small surface the WPF torrent list needs.
/// </summary>
public sealed class TorrentService : IDisposable
{
    private readonly LibtorrentSession _session;
    private bool _disposed;

    private static readonly string[] FallbackTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.moeking.me:6969/announce"
    ];

    public TorrentService(string stateDirectory, AppSettings? appSettings = null)
    {
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(Path.Combine(stateDirectory, "cache"));

        // libtorrent-rasterbar supplies DHT, LSD, PEX and uTP through its native
        // session defaults. Bind on an available port so a stale/occupied fixed
        // port cannot prevent the engine from starting.
        var settings = new SettingsPack();
        settings.Set("enable_dht", true);
        settings.Set("enable_lsd", true);
        settings.Set("enable_upnp", true);
        settings.Set("enable_natpmp", true);
        settings.Set("enable_incoming_utp", true);
        settings.Set("enable_outgoing_utp", true);
        settings.Set("enable_incoming_tcp", true);
        settings.Set("enable_outgoing_tcp", true);
        settings.Set("connections_limit", 500);
        // Libtorrent rate limits are global session limits in bytes per second; zero means unlimited.
        settings.Set("download_rate_limit", appSettings?.TorrentDownloadLimitBytesPerSecond ?? 0);
        settings.Set("upload_rate_limit", appSettings?.TorrentUploadLimitBytesPerSecond ?? 0);
        settings.Set("active_downloads", -1);
        settings.Set("active_seeds", -1);
        settings.Set("active_dht_limit", 128);
        settings.Set("active_tracker_limit", 500);
        settings.Set("active_lsd_limit", 128);
        settings.Set("tick_interval", 250);
        settings.Set("listen_interfaces", "0.0.0.0:0,[::]:0");
        settings.Set("dht_bootstrap_nodes", "router.bittorrent.com:6881,router.utorrent.com:6881,dht.transmissionbt.com:6881");

        _session = new LibtorrentSession(settings)
        {
            DefaultDownloadPath = Path.Combine(stateDirectory, "downloads")
        };
    }

    public Task<TorrentTransfer> AddAsync(string source, string savePath, string? torrentName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(savePath);

        TorrentTransfer transfer;
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            string magnetUri = AddFallbackTrackers(source);
            var existingEntries = Directory.GetFileSystemEntries(savePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = _session.Add(new AddTorrentParams
            {
                MagnetUri = magnetUri,
                SavePath = savePath
            });

            var magnet = result.Magnet ?? throw new InvalidOperationException("libtorrent did not return a magnet handle.");
            if (!magnet.IsValid) throw new InvalidOperationException("libtorrent rejected this magnet link.");
            transfer = new TorrentTransfer(magnet, ExtractDisplayName(source), savePath, existingEntries);
            // LibtorrentSharp creates magnet handles paused. Explicitly resume
            // them so BEP-9 metadata exchange and DHT discovery begin immediately.
            magnet.Resume();
        }
        else
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("The selected .torrent file could not be found.", source);

            var info = new TorrentInfo(source);
            var result = _session.Add(new AddTorrentParams
            {
                TorrentInfo = info,
                SavePath = savePath
            });
            var handle = result.Torrent ?? throw new InvalidOperationException("libtorrent did not return a torrent handle.");
            transfer = new TorrentTransfer(handle, info.Metadata.Name, savePath, null);
            handle.Start();
        }

        return Task.FromResult(transfer);
    }

    public Task PauseAsync(TorrentTransfer transfer)
    {
        if (transfer.Magnet is not null) transfer.Magnet.Pause();
        else transfer.Torrent!.Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync(TorrentTransfer transfer)
    {
        // MagnetHandle currently exposes queue-aware pause/resume rather than a
        // separate stop operation. Pausing preserves the handle and downloaded data.
        if (transfer.Magnet is not null) transfer.Magnet.Pause();
        else transfer.Torrent!.Stop();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(TorrentTransfer transfer)
    {
        if (transfer.Magnet is not null) transfer.Magnet.Resume();
        else transfer.Torrent!.Start();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(TorrentTransfer transfer)
    {
        if (transfer.Magnet is not null) _session.DetachMagnet(transfer.Magnet);
        else if (transfer.Torrent is not null) _session.DetachTorrent(transfer.Torrent);
        return Task.CompletedTask;
    }

    private static string AddFallbackTrackers(string source)
    {
        // Do not append public trackers to magnets that already specify trackers.
        // This is important for private torrents: their private tracker list must
        // remain authoritative and must not be disclosed to public swarms.
        if (source.Contains("tr=", StringComparison.OrdinalIgnoreCase)) return source;

        var builder = new StringBuilder(source);
        foreach (string tracker in FallbackTrackers)
        {
            string encoded = Uri.EscapeDataString(tracker);
            if (source.Contains(encoded, StringComparison.OrdinalIgnoreCase) ||
                source.Contains(tracker, StringComparison.OrdinalIgnoreCase)) continue;
            builder.Append(source.Contains('?') ? '&' : '?').Append("tr=").Append(encoded);
        }
        return builder.ToString();
    }

    private static string ExtractDisplayName(string magnet)
    {
        try
        {
            var query = new Uri(magnet).Query.TrimStart('?').Split('&');
            foreach (var part in query)
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0] == "dn")
                {
                    var name = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch { }
        return "Magnet torrent";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}

/// <summary>Common managed wrapper around libtorrent's magnet and file handles.</summary>
public sealed class TorrentTransfer
{
    internal TorrentHandle? Torrent { get; }
    internal MagnetHandle? Magnet { get; }
    public string Name { get; }
    private readonly string _savePath;
    private readonly HashSet<string> _existingEntries;

    internal TorrentTransfer(TorrentHandle torrent, string name, string savePath, HashSet<string>? existingEntries)
    {
        Torrent = torrent;
        Name = string.IsNullOrWhiteSpace(name) ? "Torrent" : name;
        _savePath = savePath;
        _existingEntries = existingEntries ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal TorrentTransfer(MagnetHandle magnet, string name, string savePath, HashSet<string> existingEntries)
    {
        Magnet = magnet;
        Name = string.IsNullOrWhiteSpace(name) ? "Magnet torrent" : name;
        _savePath = savePath;
        _existingEntries = existingEntries;
    }

    public TorrentStatus GetCurrentStatus() => Torrent is not null
        ? Torrent.GetCurrentStatus()
        : Magnet!.GetCurrentStatus();

    // TorrentStatus in LibtorrentSharp 0.1.0-alpha deliberately does not carry
    // the torrent name, and MagnetHandle remains a MagnetHandle after metadata
    // arrives. Its GetFiles() API does, however, expose the metadata-backed file
    // paths. Resolve the name from THIS handle's file list so another torrent in
    // the same destination folder can never rename this item.
    public bool TryResolveMetadataName(object status, out string name, bool allowStorageFallback = true)
    {
        name = string.Empty;

        if (Magnet is not null)
        {
            try
            {
                var relativePaths = Magnet.GetFiles()
                    .Select(file => file.Info.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Replace('\\', '/').Trim('/'))
                    .Where(path => path.Length > 0)
                    .ToArray();

                if (relativePaths.Length > 0)
                {
                    var segments = relativePaths
                        .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                        .Where(parts => parts.Length > 0)
                        .ToArray();

                    string? metadataName = null;
                    if (segments.Length > 0 && segments.All(parts => parts.Length > 1) &&
                        segments.Select(parts => parts[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    {
                        // Multi-file torrents conventionally place every file
                        // beneath one metadata root directory.
                        metadataName = segments[0][0];
                    }
                    else if (relativePaths.Length == 1)
                    {
                        // Single-file torrent: retain the extension because it
                        // is also the actual on-disk filename used by Open File.
                        metadataName = Path.GetFileName(relativePaths[0]);
                    }
                    else if (segments.Length > 0)
                    {
                        // Some multi-file torrents have no common root folder.
                        // Use the first metadata file's parent/name as a safe
                        // per-handle display fallback; never scan the shared disk.
                        metadataName = segments[0].Length > 1
                            ? segments[0][0]
                            : Path.GetFileNameWithoutExtension(segments[0][0]);
                    }

                    if (IsUsefulName(metadataName))
                    {
                        name = metadataName!;
                        return true;
                    }
                }
            }
            catch
            {
                // Metadata may not have arrived yet. The normal status refresh
                // will retry without changing this torrent's current name.
            }
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var root in new object?[] { status, Magnet, Torrent })
        {
            var found = FindName(root, visited, 0);
            if (IsUsefulName(found)) { name = found!; return true; }
        }

        // Directory scanning is only a last-resort name lookup. It is unsafe
        // when multiple torrents share a destination folder because a different
        // torrent can create the "new" entry. The caller disables this fallback
        // for shared folders and for already-completed torrents.
        if (!allowStorageFallback) return false;

        try
        {
            if (Directory.Exists(_savePath))
            {
                var created = Directory.GetFileSystemEntries(_savePath)
                    .Where(path => !_existingEntries.Contains(path))
                    .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                    .Where(path => !Path.GetFileName(path).EndsWith(".parts", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (created.Length == 1)
                {
                    name = Path.GetFileName(created[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return IsUsefulName(name);
                }
                // Multi-file torrents normally create one top-level root folder.
                var directories = created.Where(Directory.Exists).ToArray();
                if (directories.Length == 1)
                {
                    name = Path.GetFileName(directories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return IsUsefulName(name);
                }
            }
        }
        catch { }
        return false;
    }

    private static string? FindName(object? value, HashSet<object> visited, int depth)
    {
        if (value is null || depth > 3 || value is string || value.GetType().IsPrimitive || !visited.Add(value)) return null;
        var type = value.GetType();
        foreach (var propertyName in new[] { "Name", "TorrentInfo", "TorrentFile", "Info", "Metadata" })
        {
            try
            {
                var property = type.GetProperty(propertyName);
                if (property is null || property.GetIndexParameters().Length != 0) continue;
                var child = property.GetValue(value);
                if (propertyName == "Name" && child is string text && IsUsefulName(text)) return text.Trim();
                if (child is not null)
                {
                    var nested = FindName(child, visited, depth + 1);
                    if (IsUsefulName(nested)) return nested;
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsUsefulName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("Magnet torrent", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Torrent", StringComparison.OrdinalIgnoreCase);

    public long TotalSize => Magnet?.TotalSize ?? Torrent?.Info.Metadata.TotalSize ?? 0;
}

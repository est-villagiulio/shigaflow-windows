using System;
using System.Text.Json;
using System.IO;

namespace Shigaflow;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool SpeedLimitEnabled { get; set; }
    public double SpeedLimitValue { get; set; } = 2;
    public string SpeedLimitUnit { get; set; } = "MB/s";

    // Torrent limits are independent from the regular download engine limits.
    public bool TorrentDownloadLimitEnabled { get; set; }
    public double TorrentDownloadLimitValue { get; set; } = 2;
    public string TorrentDownloadLimitUnit { get; set; } = "MB/s";
    public bool TorrentUploadLimitEnabled { get; set; }
    public double TorrentUploadLimitValue { get; set; } = 1;
    public string TorrentUploadLimitUnit { get; set; } = "MB/s";
    // Supported values: "Seed" or "Stop".
    public string TorrentAfterFinishAction { get; set; } = "Seed";

    // User-resized Torrents table column widths (pixels); zero uses the default width.
    public double TorrentColumnNameWidth { get; set; }
    public double TorrentColumnStatusWidth { get; set; }
    public double TorrentColumnSizeWidth { get; set; }
    public double TorrentColumnDownloadWidth { get; set; }
    public double TorrentColumnUploadWidth { get; set; }
    public double TorrentColumnAddedWidth { get; set; }

    // True when the sidebar is collapsed to its icon strip.
    public bool SidebarCollapsed { get; set; }

    // User-resized Downloads table column widths (pixels); zero uses the default width.
    public double DownloadColumnNameWidth { get; set; }
    public double DownloadColumnStatusWidth { get; set; }
    public double DownloadColumnResumeWidth { get; set; }
    public double DownloadColumnSizeWidth { get; set; }
    public double DownloadColumnSpeedWidth { get; set; }
    public double DownloadColumnAddedWidth { get; set; }
    public double DownloadColumnButtonWidth { get; set; }

    public int TorrentDownloadLimitBytesPerSecond => GetTorrentLimitBytesPerSecond(
        TorrentDownloadLimitEnabled, TorrentDownloadLimitValue, TorrentDownloadLimitUnit);
    public int TorrentUploadLimitBytesPerSecond => GetTorrentLimitBytesPerSecond(
        TorrentUploadLimitEnabled, TorrentUploadLimitValue, TorrentUploadLimitUnit);

    private static int GetTorrentLimitBytesPerSecond(bool enabled, double value, string unit)
    {
        if (!enabled || !double.IsFinite(value) || value <= 0) return 0;
        double multiplier = unit == "KB/s" ? 1024d : 1024d * 1024d;
        return (int)Math.Clamp(value * multiplier, 1d, int.MaxValue);
    }
    // Empty means no browser file types are allowed until the user adds extensions.
    public string AllowedExtensions { get; set; } = "zip, rar, 7z, pdf, mp3, mp4, mkv, mov, avi, webm, doc, docx, xls, xlsx, ppt, pptx, txt, csv, exe, msi";

    public static string NormalizeAllowedExtensions(string? input)
    {
        var values = (input ?? "").Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().TrimStart('.').ToLowerInvariant())
            .Where(value => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", values);
    }

    public bool IsExtensionAllowed(string? fileName)
    {
        string extension = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) return false;
        var allowed = NormalizeAllowedExtensions(AllowedExtensions)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public double SpeedLimitBytesPerSecond => SpeedLimitEnabled
        ? Math.Max(1, SpeedLimitValue) * (SpeedLimitUnit == "KB/s" ? 1024d : 1024d * 1024d)
        : 0;

    public static string SettingsPath => Path.Combine(AppPaths.DataRoot, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

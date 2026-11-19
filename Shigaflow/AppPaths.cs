using System;
using System.IO;

namespace Shigaflow;

/// <summary>
/// Single source of truth for Shigaflow's per-user data folder (%LOCALAPPDATA%\Shigaflow).
/// The first time this type is touched it copies data left by older versions (which used a
/// different folder name) into the new folder, so settings, download history and torrents carry over.
/// </summary>
internal static class AppPaths
{
    public const string FolderName = "Shigaflow";
    private const string LegacyFolderName = "NightDownload";
    private const string MigrationMarker = ".migrated";

    public static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    static AppPaths()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            MigrateLegacyData();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Data folder migration failed: " + ex.Message);
        }
    }

    // Copies (never moves) so the old folder, which may also hold an old installation, is left untouched.
    private static void MigrateLegacyData()
    {
        string markerPath = Path.Combine(DataRoot, MigrationMarker);
        if (File.Exists(markerPath)) return;

        string legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);
        if (!Directory.Exists(legacyRoot)) return;

        CopyFileIfMissing(Path.Combine(legacyRoot, "settings.json"), Path.Combine(DataRoot, "settings.json"));
        CopyFileIfMissing(Path.Combine(legacyRoot, "downloads.json"), Path.Combine(DataRoot, "downloads.json"));

        string legacyTorrents = Path.Combine(legacyRoot, "Torrents");
        string newTorrents = Path.Combine(DataRoot, "Torrents");
        if (Directory.Exists(legacyTorrents) && !Directory.Exists(newTorrents))
        {
            // Copy into a temporary folder first so an interrupted copy is never mistaken for a finished one.
            string staging = newTorrents + ".migrating";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyDirectory(legacyTorrents, staging);

            // Saved .torrent files are referenced by absolute path; point them at the new folder.
            string history = Path.Combine(staging, "torrents.json");
            if (File.Exists(history))
            {
                string text = File.ReadAllText(history);
                string oldEscaped = legacyTorrents.Replace("\\", "\\\\");
                string newEscaped = newTorrents.Replace("\\", "\\\\");
                text = text.Replace(oldEscaped, newEscaped, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(history, text);
            }
            Directory.Move(staging, newTorrents);
        }

        File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
    }

    private static void CopyFileIfMissing(string source, string destination)
    {
        try
        {
            if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination);
        }
        catch { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

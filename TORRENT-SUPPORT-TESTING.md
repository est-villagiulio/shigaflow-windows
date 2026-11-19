# Shigaflow Torrent Support — First Test Pass

## What was added

- A separate **Torrents** page in the left sidebar. The existing HTTP/HTTPS Downloads page remains separate.
- Add a `.torrent` file or paste a `magnet:` link.
- Choose a destination folder for each torrent.
- Show torrent name, progress, total size, download speed, upload speed, and status.
- Pause, Resume, Stop, open the destination folder, and remove a torrent entry.
- Save torrent source, destination, progress, completion state, and user pause/stop state under `%LOCALAPPDATA%\Shigaflow\Torrents`.
- Completed torrents stay active in the Seeding state until manually paused or stopped. On the next launch, completed torrents that were not manually paused/stopped start seeding again.
- The ordinary Downloads context menu now includes Open File for completed downloads and follows the selected Shigaflow theme.

## First build

1. Open `Shigaflow.sln` in Visual Studio.
2. Allow NuGet package restore. The LibtorrentSharp package and its Windows x64 native runtime require internet access on the first restore if they are not already cached.
3. Build the `Shigaflow` project in Debug.
4. Run Shigaflow and select **Torrents** in the sidebar.

## Suggested tests

1. Add a known legal/public-domain `.torrent` file and choose a test folder.
2. Add a known legal/public-domain magnet link and choose a test folder.
3. Verify progress, download speed, and upload speed update.
4. Pause, wait, and verify both speeds stop; then Resume.
5. Stop and verify the torrent remains stopped until Resume is selected.
6. Let a torrent finish and verify its status changes to **Seeding** and upload continues when peers are available.
7. Close Shigaflow using Exit (not the window close button), reopen it, and verify active torrents resume and completed torrents seed again. Verify paused/stopped torrents remain paused/stopped.
8. Confirm normal HTTP/HTTPS downloads and Chrome capture still work as before.

## Important test limitation

The source was updated against the published LibtorrentSharp API documentation, but it was not compiled or run on Windows in the current environment. Build it in Visual Studio before using it for important downloads. Torrent connectivity and seeding also depend on tracker/DHT availability, network/firewall settings, and whether peers are available.

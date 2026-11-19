<img width="1328" height="772" alt="image" src="https://github.com/user-attachments/assets/36a3fa0a-529b-4ed1-8f47-541b545e190a" />


# Shigaflow — Windows download manager

This ZIP contains the complete Visual Studio source for Shigaflow, including the WPF application, Chrome/Edge browser extension, Native Messaging host, and Inno Setup 6 installer definition.

## Requirements

- Windows 10 or Windows 11, 64-bit
- Visual Studio 2022 or newer with the **.NET desktop development** workload, or the .NET 8 SDK
- Inno Setup 6 (only required to compile the installer)

The application projects target `net8.0-windows`. A newer installed .NET SDK can build this target; the .NET 8 runtime does not need to be installed on the destination PC because the release publish is self-contained.

## Product naming and compatibility

The application, executable, Visual Studio solution/project names, Native Messaging executable and host name (`com.shigaflow.host`), installer, shortcuts, data folder (`%LOCALAPPDATA%\Shigaflow`) and user-facing labels are all named **Shigaflow**. On first launch, settings, download history and torrents from an older version's data folder are copied into the new folder automatically. After upgrading, run **Connect Shigaflow to Chrome** once and reload the browser extension, because the browser host name changed.

## Open and build in Visual Studio

1. Extract the ZIP.
2. Open `Shigaflow.sln`.
3. Allow Visual Studio to restore the projects.
4. Select **Release** (or **Debug**) and build the solution.
5. Press F5 to run the WPF app.

The solution contains both projects:
- `Shigaflow` — WPF desktop application
- `Shigaflow.NativeHost` — Chrome/Edge Native Messaging host

## Create a self-contained Windows x64 publish

Open PowerShell in the extracted project folder and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\publish.ps1
```

The script publishes both projects into:

- `installer-payload\App`
- `installer-payload\NativeHost`

Both outputs target `win-x64` and are self-contained. They are published as single-file executables.

## Create the Inno Setup 6 installer

1. Install Inno Setup 6.
2. Run `Build-Shigaflow-Installer.ps1` from the project folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Build-Shigaflow-Installer.ps1
```

3. The installer is created at `installer\Shigaflow-Setup.exe`.

Alternatively, run `publish.ps1`, then open `Shigaflow-Setup.iss` in Inno Setup Compiler and click **Compile**.

The installer:
- Installs per-user under `%LOCALAPPDATA%\Shigaflow` (no administrator elevation).
- Includes the app, Native Messaging host, and browser extension.
- Uses the Shigaflow ICO for the installer and application shortcuts.
- Adds a Start Menu shortcut and optionally a Desktop shortcut.
- Offers to launch Shigaflow after installation.

## Browser integration setup

After installing, use **Connect Shigaflow to Chrome** from the Start Menu, or run `Register-NativeHost.ps1` from the installation folder. Follow the prompts to load the installed `BrowserExtension` folder as an unpacked extension and register its extension ID. Fully restart Chrome or Edge after registration.

Browser file capture is restricted to the extension allowlist in Settings (for example, `zip, pdf, mp4`). Unlisted or unidentified extensions are rejected by the Native Messaging host and are not added to Shigaflow. The extension intercepts supported download-link clicks before the browser's normal download flow where possible. The `downloads.onCreated` listener remains a fallback; for downloads it cannot intercept in advance, the browser may still display its own download prompt. Some sites require browser cookies, authentication, or custom request headers and may not be transferable to Shigaflow.

## Included feature stages

- **Stage 1:** Spaced download rows, one always-visible Pause/Resume control, row-specific right-click actions, and Open File Location.
- **Stage 2:** Bulk selection/deletion, Clear List with Yes/No/Cancel behavior, and protection for active/paused downloads.
- **Stage 3:** Windows system tray, restore/open, Pause All, and Exit.
- **Stage 4:** Chrome/Edge extension, Native Messaging integration, early link interception where supported, and single-instance protection.
- **Stage 5:** How To tab with a width-fitted, vertically scrollable setup guide; Shigaflow application/installer/shortcut icon; and preserved About screen.

## Stage 6 — Build and packaging

The project includes `publish.ps1`, `Build-Shigaflow-Installer.ps1`, and `Shigaflow-Setup.iss` for release packaging. The installer target remains Inno Setup 6 and the install location remains under Local AppData.

**Important verification note:** a Windows build and Inno Setup compilation must be run on a Windows machine with the .NET SDK and Inno Setup 6 installed. This source package has not been represented as a successfully compiled Windows installer unless those steps have actually been run and verified.

## Troubleshooting

- If Visual Studio cannot find the target framework, install a .NET 8 SDK (or a newer SDK that supports `net8.0-windows`) and the .NET desktop development workload.
- If Chrome/Edge does not connect, confirm that the extension was loaded from the installed `%LOCALAPPDATA%\Shigaflow\BrowserExtension` folder, rerun the native-host registration, then fully restart the browser.
- If Windows SmartScreen warns about the installer, note that this project is not code-signed.


## Google Drive signed-in My Drive downloads

Version 1.0.5 adds authenticated Google Drive handoff. Select a single file in My Drive and choose Download. The extension reads Google-domain cookies and passes them to the local Shigaflow app through native messaging. The app uses them only in a Google-domain-scoped cookie container for its metadata probe and download. Cookie values are not saved to settings or download history. Because this adds the Chrome `cookies` permission, approve the permission change when Chrome asks and reload the extension. Rebuild/reinstall the app and native host with `Install-BrowserIntegration.ps1` so the new cookie-aware native host and app are installed together.


## Torrent support (initial implementation)

Shigaflow now includes a separate Torrents workspace powered by libtorrent-rasterbar through the LibtorrentSharp native Windows binding. Use **Torrents** in the sidebar, then **Add Torrent** to select a `.torrent` file or paste a `magnet:` link. Choose a destination folder when prompted. Torrent state is stored separately under `%LOCALAPPDATA%\Shigaflow\Torrents\torrents.json`. Completed torrents remain active and seed until the user pauses or stops them. Paused/stopped torrents can be resumed from the Torrents list.

The Windows build restores `LibtorrentSharp` and its bundled Windows x64 native engine during `dotnet publish`. The binding is currently an early-access alpha package. This source update has not been compiled or exercised on Windows in this environment; restore/build and test it on Windows before installing.


## Google Drive bulk ZIP regression hardening (V10)

The browser extension now merges Drive's selected-row and selected-checkbox accessibility states when counting selected file IDs. The native messaging host also normalizes requests explicitly marked `googleDriveBulk` to a `.zip` filename before extension allow-list validation, so a temporarily extensionless Drive archive cannot be rejected as an empty extension. The extension manifest version is `1.3.0`.

This is a source package. Build and verify the Windows application/host and installer on Windows with the .NET SDK and Inno Setup before deploying; this package does not claim to contain a newly compiled or runtime-tested executable.

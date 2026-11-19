<img width="1328" height="772" alt="image" src="https://github.com/user-attachments/assets/36a3fa0a-529b-4ed1-8f47-541b545e190a" />


# Shigaflow — Windows download manager

This ZIP contains the complete Visual Studio source for Shigaflow, including the WPF application, Chrome/Edge browser extension, Native Messaging host, and Inno Setup 6 installer definition.
The finished build are in installer Folder, already an exe File, feel free to use that, after installed, in setting, there How-to Tab, Follow it to add Browser Extension.

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

## Build and packaging

The project includes `publish.ps1`, `Build-Shigaflow-Installer.ps1`, and `Shigaflow-Setup.iss` for release packaging. The installer target remains Inno Setup 6 and the install location remains under Local AppData.

**Important verification note:** a Windows build and Inno Setup compilation must be run on a Windows machine with the .NET SDK and Inno Setup 6 installed. This source package has not been represented as a successfully compiled Windows installer unless those steps have actually been run and verified.

## Troubleshooting

- If Visual Studio cannot find the target framework, install a .NET 8 SDK (or a newer SDK that supports `net8.0-windows`) and the .NET desktop development workload.
- If Chrome/Edge does not connect, confirm that the extension was loaded from the installed `%LOCALAPPDATA%\Shigaflow\BrowserExtension` folder, rerun the native-host registration, then fully restart the browser.
- If Windows SmartScreen warns about the installer, note that this project is not code-signed.


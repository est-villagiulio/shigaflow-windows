# Stage 6 — Build and packaging

## Build the Visual Studio solution
Open `Shigaflow.sln` in Visual Studio 2022 or newer with the .NET desktop development workload. The projects target `net8.0-windows`.

## Publish
Run `publish.ps1` in PowerShell on Windows with the .NET 8 SDK (or newer compatible SDK). It creates self-contained `win-x64` single-file outputs in `installer-payload`.

## Compile the installer
Install Inno Setup 6 and run `Build-Shigaflow-Installer.ps1`. The script publishes both projects, locates `ISCC.exe`, compiles `Shigaflow-Setup.iss`, and checks for `installer\Shigaflow-Setup.exe`.

The installer remains per-user under `%LOCALAPPDATA%\Shigaflow` and includes the app, Native Messaging host, and browser extension.

## Important
The source ZIP is not itself a compiled Windows installer. A successful Windows publish and Inno Setup compile must be performed on Windows before claiming the installer is build-tested.

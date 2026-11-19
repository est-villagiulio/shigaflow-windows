#define MyAppName "Shigaflow"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Shiga"
#define MyAppExeName "Shigaflow.exe"

[Setup]
AppId={{C39C6A7E-17B5-4D5A-A4E7-5B8C9B8E1F63}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Shigaflow
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
OutputDir=installer
OutputBaseFilename=Shigaflow-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Shigaflow\Assets\shigaflow.ico
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName={#MyAppName}

[Dirs]
Name: "{app}\App"
Name: "{app}\NativeHost"
Name: "{app}\BrowserExtension"

[Files]
Source: "installer-payload\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer-payload\NativeHost\*"; DestDir: "{app}\NativeHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Register-NativeHost.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-For-Friends.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Shigaflow"; Filename: "{app}\App\{#MyAppExeName}"; IconFilename: "{app}\App\{#MyAppExeName}"
Name: "{autoprograms}\Connect Shigaflow to Chrome"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Register-NativeHost.ps1"""; WorkingDir: "{app}"
Name: "{autodesktop}\Shigaflow"; Filename: "{app}\App\{#MyAppExeName}"; IconFilename: "{app}\App\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\App\{#MyAppExeName}"; Description: "Launch Shigaflow"; Flags: postinstall nowait skipifsilent

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $projectRoot "Shigaflow.sln"
Write-Host "Building Shigaflow and its native messaging host..."
dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed. Fix the build errors and run this script again." }

$installRoot = Join-Path $env:LOCALAPPDATA "Shigaflow"
$appInstall = Join-Path $installRoot "App"
$hostInstall = Join-Path $installRoot "NativeHost"
$extensionInstall = Join-Path $installRoot "BrowserExtension"
New-Item -ItemType Directory -Force -Path $appInstall,$hostInstall,$extensionInstall | Out-Null

$appOutput = Join-Path $projectRoot "Shigaflow\bin\Release\net8.0-windows"
$hostOutput = Join-Path $projectRoot "NativeHost\bin\Release\net8.0-windows"
if (!(Test-Path (Join-Path $appOutput "Shigaflow.exe"))) { throw "Shigaflow.exe was not found in build output." }
if (!(Test-Path (Join-Path $hostOutput "Shigaflow.NativeHost.exe"))) { throw "Native host executable was not found in build output." }
Copy-Item (Join-Path $appOutput "*") $appInstall -Recurse -Force
# Copy all host build output files, including Shigaflow.NativeHost.dll and runtime configuration.
Copy-Item (Join-Path $hostOutput "*") $hostInstall -Recurse -Force
Copy-Item (Join-Path $projectRoot "BrowserExtension\*") $extensionInstall -Recurse -Force
$appExe = Join-Path $appInstall "Shigaflow.exe"
Set-Content -Path (Join-Path $hostInstall "app-path.txt") -Value $appExe -Encoding UTF8

Write-Host ""
Write-Host "Now load the unpacked extension in Chrome or Edge from:"
Write-Host $extensionInstall
Write-Host "Chrome: chrome://extensions ; Edge: edge://extensions ; enable Developer mode and choose Load unpacked."
$extensionId = Read-Host "After loading it, copy its Extension ID and paste it here"
if ([string]::IsNullOrWhiteSpace($extensionId) -or $extensionId -notmatch '^[a-p]{32}$') {
  throw "That does not look like a Chrome/Edge extension ID (32 letters a-p). Run this script again after loading the extension."
}

$manifestPath = Join-Path $hostInstall "com.shigaflow.host.json"
$nativeManifest = @{
  name = "com.shigaflow.host"
  description = "Shigaflow native messaging host"
  path = (Join-Path $hostInstall "Shigaflow.NativeHost.exe")
  type = "stdio"
  allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $nativeManifest, [System.Text.UTF8Encoding]::new($false))

$regPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.shigaflow.host"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name '(default)' -Value $manifestPath
$edgeRegPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.shigaflow.host"
New-Item -Path $edgeRegPath -Force | Out-Null
Set-ItemProperty -Path $edgeRegPath -Name '(default)' -Value $manifestPath

# Remove the registration left by older versions of this app (browser host name from before the rename).
$legacyHostName = "com.nightdownload.host"
foreach ($legacyKey in @("HKCU:\Software\Google\Chrome\NativeMessagingHosts\$legacyHostName", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$legacyHostName")) {
  if (Test-Path $legacyKey) { Remove-Item -Path $legacyKey -Recurse -Force }
}
$legacyManifest = Join-Path $env:LOCALAPPDATA "NightDownload\NativeHost\$legacyHostName.json"
if (Test-Path $legacyManifest) { Remove-Item -LiteralPath $legacyManifest -Force }

Write-Host ""
Write-Host "Installed! Native host manifest was written as UTF-8 without BOM."
Write-Host "Restart the browser completely, then test a direct file URL."
Write-Host "Shigaflow app: $appExe"
Write-Host "Extension folder: $extensionInstall"

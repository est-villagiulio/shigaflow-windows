$ErrorActionPreference = 'Stop'
$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$extensionPath = Join-Path $installRoot 'BrowserExtension'
$hostPath = Join-Path $installRoot 'NativeHost\Shigaflow.NativeHost.exe'
$appPath = Join-Path $installRoot 'App\Shigaflow.exe'
$manifestPath = Join-Path $installRoot 'NativeHost\com.shigaflow.host.json'

if (!(Test-Path (Join-Path $extensionPath 'manifest.json'))) { throw "Extension manifest not found: $extensionPath" }
if (!(Test-Path $hostPath)) { throw "Native host executable not found: $hostPath" }
if (!(Test-Path $appPath)) { throw "Shigaflow executable not found: $appPath" }

Write-Host 'STEP 1: In Chrome, open chrome://extensions.'
Write-Host 'STEP 2: Turn on Developer mode, click Load unpacked, and select:'
Write-Host "        $extensionPath"
Write-Host 'STEP 3: Copy the 32-character Extension ID shown on the extension card.'
Write-Host ''
$extensionId = Read-Host 'Paste the extension ID here'
if ($extensionId -notmatch '^[a-p]{32}$') { throw 'Invalid extension ID. It should be 32 letters from a through p. Run this again and paste the ID exactly.' }

Set-Content -LiteralPath (Join-Path $installRoot 'NativeHost\app-path.txt') -Value $appPath -Encoding UTF8
$nativeManifest = [ordered]@{
    name = 'com.shigaflow.host'
    description = 'Shigaflow native messaging host'
    path = $hostPath
    type = 'stdio'
    allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $nativeManifest, [System.Text.UTF8Encoding]::new($false))

$regKeys = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.shigaflow.host',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.shigaflow.host'
)
foreach ($key in $regKeys) {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value $manifestPath
}

# Remove the registration left by older versions of this app (browser host name from before the rename).
$legacyHostName = "com.nightdownload.host"
foreach ($legacyKey in @("HKCU:\Software\Google\Chrome\NativeMessagingHosts\$legacyHostName", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$legacyHostName")) {
  if (Test-Path $legacyKey) { Remove-Item -Path $legacyKey -Recurse -Force }
}
$legacyManifest = Join-Path $env:LOCALAPPDATA "NightDownload\NativeHost\$legacyHostName.json"
if (Test-Path $legacyManifest) { Remove-Item -LiteralPath $legacyManifest -Force }

Write-Host ''
Write-Host 'Native host registration completed for Chrome and Edge.'
Write-Host 'Completely exit and reopen Chrome before testing.'
Read-Host 'Press Enter to close'

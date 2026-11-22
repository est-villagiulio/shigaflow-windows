$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishRoot = Join-Path $projectRoot 'installer-payload'
$appOut = Join-Path $publishRoot 'App'
$hostOut = Join-Path $publishRoot 'NativeHost'

# Keep publish output deterministic; stale files must not leak into a new installer.
if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appOut, $hostOut | Out-Null

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '.NET SDK was not found. Install the .NET 8 SDK (or a newer SDK that supports net8.0-windows), then run this script again.'
}

Write-Host 'Restoring and publishing Shigaflow for Windows x64...'
& dotnet publish (Join-Path $projectRoot 'Shigaflow\Shigaflow.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $appOut `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Shigaflow publish failed with exit code $LASTEXITCODE."
}

Write-Host 'Publishing the Native Messaging host for Windows x64...'
& dotnet publish (Join-Path $projectRoot 'NativeHost\Shigaflow.NativeHost.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $hostOut `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Native host publish failed with exit code $LASTEXITCODE."
}

$appExe = Join-Path $appOut 'Shigaflow.exe'
$hostExe = Join-Path $hostOut 'Shigaflow.NativeHost.exe'
if (-not (Test-Path $appExe)) {
    throw "Published application executable was not produced: $appExe"
}
if (-not (Test-Path $hostExe)) {
    throw "Published Native Messaging host executable was not produced: $hostExe"
}

Write-Host ''
Write-Host 'Self-contained Windows x64 publish output is ready:'
Write-Host "  App:        $appOut"
Write-Host "  Native host: $hostOut"
Write-Host ''
Write-Host 'To create the installer, run Build-Shigaflow-Installer.ps1 or compile Shigaflow-Setup.iss with Inno Setup 6.'

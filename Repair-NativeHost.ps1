$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostProject = Join-Path $projectRoot "NativeHost\Shigaflow.NativeHost.csproj"
$hostOutput = Join-Path $projectRoot "NativeHost\bin\Release\net8.0-windows"
$hostInstall = Join-Path $env:LOCALAPPDATA "Shigaflow\NativeHost"

Write-Host "Building Shigaflow native host..."
dotnet build $hostProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Native host build failed." }

$dllPath = Join-Path $hostOutput "Shigaflow.NativeHost.dll"
if (!(Test-Path $dllPath)) {
    throw "Build output is missing Shigaflow.NativeHost.dll: $dllPath"
}

New-Item -ItemType Directory -Force -Path $hostInstall | Out-Null
Copy-Item (Join-Path $hostOutput "*") $hostInstall -Recurse -Force

Write-Host ""
Write-Host "Native host files copied to:"
Write-Host $hostInstall
Write-Host "Restart Chrome/Edge completely, then test a direct file URL."

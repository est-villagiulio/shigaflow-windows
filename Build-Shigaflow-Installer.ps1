$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot

# Build the two publish payloads first. publish.ps1 throws on any dotnet failure.
& (Join-Path $projectRoot 'publish.ps1')

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$isccCandidates = @(
    'D:\Program\Inno Setup 6\ISCC.exe',
    (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
    (Join-Path $programFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $isccCandidates) {
    throw 'The app and Native Messaging host were published, but Inno Setup 6 was not found. Install Inno Setup 6 and run this script again.'
}

$outputDir = Join-Path $projectRoot 'installer'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$iscc = $isccCandidates | Select-Object -First 1
$issFile = Join-Path $projectRoot 'Shigaflow-Setup.iss'
Write-Host "Compiling the installer with: $iscc"
& $iscc $issFile
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $outputDir 'Shigaflow-Setup.exe'
if (-not (Test-Path $installer)) {
    throw "Inno Setup returned success but the expected installer was not found: $installer"
}

Write-Host ''
Write-Host 'Installer successfully created:'
Write-Host $installer

# Builds, tests and packages Night Tech Tools on Windows.
# Needs: .NET 8 SDK (https://dotnet.microsoft.com/download) and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Version 0.1.0] [-SkipTests]
param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "== Night Tech Tools build $Version ==" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download (or: winget install Microsoft.DotNet.SDK.8)."
}

if (-not $SkipTests) {
    dotnet test tests\NightTech.Core.Tests\NightTech.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Nothing was packaged." }
}

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
dotnet publish src\NightTech.Recovery\NightTech.Recovery.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version `
    -o publish\recovery
if ($LASTEXITCODE -ne 0) { throw "Publishing Night Tech Recovery failed." }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup)." }

& $iscc "/DAppVersion=$Version" installer\NightTechRecovery.iss
if ($LASTEXITCODE -ne 0) { throw "Building the installer failed." }

$out = "installer\Output\NightTechRecovery-Setup.exe"
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
"$hash  NightTechRecovery-Setup.exe" | Out-File -Encoding ascii "installer\Output\NightTechRecovery-Setup.exe.sha256.txt"
Write-Host ""
Write-Host "Done: $out" -ForegroundColor Green
Write-Host "SHA-256: $hash"

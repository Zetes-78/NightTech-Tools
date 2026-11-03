# Builds, tests and packages Night Tech Tools on Windows.
# Needs: .NET 8 SDK (https://dotnet.microsoft.com/download) and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SkipTests] [-Version 1.2.3]
# Each tool's version comes from its <Version> in the .csproj. -Version overrides it for every tool.
param(
    [string]$Version = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download (or: winget install Microsoft.DotNet.SDK.8)."
}
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup)." }

if (-not $SkipTests) {
    dotnet test tests\NightTech.Core.Tests\NightTech.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Nothing was packaged." }
}

function Get-ProjectVersion([string]$csproj) {
    $v = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { throw "No <Version> in $csproj" }
    return $v
}

function Write-Hash([string]$file) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path $file -Leaf)" | Out-File -Encoding ascii "$file.sha256.txt"
    Write-Host "  $(Split-Path $file -Leaf)  SHA-256 $hash"
}

$tools = @(
    @{ Name = "Night Tech Recovery";  Project = "src\NightTech.Recovery\NightTech.Recovery.csproj"; Out = "publish\recovery"; Iss = "installer\NightTechRecovery.iss"; Setup = "NightTechRecovery-Setup.exe"; SelfExtract = "true" },
    # Field Kit keeps its native files beside the exe instead of unpacking them to the customer's temp folder.
    @{ Name = "Night Tech Field Kit"; Project = "src\NightTech.FieldKit\NightTech.FieldKit.csproj"; Out = "publish\fieldkit"; Iss = "installer\NightTechFieldKit.iss"; Setup = "NightTechFieldKit-Setup.exe"; SelfExtract = "false" },
    @{ Name = "Night Tech Wipe";      Project = "src\NightTech.Wipe\NightTech.Wipe.csproj";         Out = "publish\wipe";     Iss = "installer\NightTechWipe.iss";     Setup = "NightTechWipe-Setup.exe";     SelfExtract = "false" },
    @{ Name = "Night Tech Cleanup";   Project = "src\NightTech.Cleanup\NightTech.Cleanup.csproj";   Out = "publish\cleanup";  Iss = "installer\NightTechCleanup.iss";  Setup = "NightTechCleanup-Setup.exe";  SelfExtract = "false" },
    @{ Name = "Night Tech Burn-in";   Project = "src\NightTech.BurnIn\NightTech.BurnIn.csproj";     Out = "publish\burnin";   Iss = "installer\NightTechBurnIn.iss";   Setup = "NightTechBurnIn-Setup.exe";   SelfExtract = "false" }
)

if (Test-Path publish) { Remove-Item -Recurse -Force publish }
New-Item -ItemType Directory -Force installer\Output | Out-Null

foreach ($t in $tools) {
    $v = if ($Version) { $Version } else { Get-ProjectVersion $t.Project }
    Write-Host "== $($t.Name) $v ==" -ForegroundColor Cyan
    dotnet publish $t.Project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true "-p:IncludeNativeLibrariesForSelfExtract=$($t.SelfExtract)" -p:DebugType=None -p:Version=$v `
        -o $t.Out
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($t.Name) failed." }
    & $iscc "/DAppVersion=$v" $t.Iss
    if ($LASTEXITCODE -ne 0) { throw "Building the $($t.Name) installer failed." }
    Write-Hash "installer\Output\$($t.Setup)"
}

# USB kit: Field Kit, Recovery and Wipe in portable mode, ready to copy to a stick.
# The USB copy of Recovery keeps its native files beside the exe too, so neither tool unpacks anything
# into the customer's temp folder, and portable.txt keeps both tools' logs and reports on the stick.
Write-Host "== USB kit ==" -ForegroundColor Cyan
$recoveryVersion = if ($Version) { $Version } else { Get-ProjectVersion $tools[0].Project }
dotnet publish $tools[0].Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None -p:Version=$recoveryVersion `
    -o publish\recovery-usb
if ($LASTEXITCODE -ne 0) { throw "Publishing the USB copy of Night Tech Recovery failed." }
$kit = "publish\usb\Night Tech Field Kit"
New-Item -ItemType Directory -Force "$kit\Field Kit", "$kit\Recovery", "$kit\Wipe", "$kit\Cleanup", "$kit\Burn-in" | Out-Null
Copy-Item -Recurse publish\fieldkit\* "$kit\Field Kit"
Copy-Item -Recurse publish\recovery-usb\* "$kit\Recovery"
Copy-Item -Recurse publish\wipe\* "$kit\Wipe"
Copy-Item -Recurse publish\cleanup\* "$kit\Cleanup"
Copy-Item -Recurse publish\burnin\* "$kit\Burn-in"
# The Mac cleanup is built on a Mac by the workflow (mac-build\). When it's here, it goes in the kit and gets its own zip.
if (Test-Path mac-build) {
    New-Item -ItemType Directory -Force "$kit\Mac Cleanup" | Out-Null
    Copy-Item -Recurse mac-build\* "$kit\Mac Cleanup"
    "This file tells Night Tech tools to keep their logs and reports on this USB stick. Don't delete it." | Out-File -Encoding ascii "$kit\Mac Cleanup\arm64\portable.txt"
    "This file tells Night Tech tools to keep their logs and reports on this USB stick. Don't delete it." | Out-File -Encoding ascii "$kit\Mac Cleanup\x64\portable.txt"
    # The download zip is the one made on the Mac (it keeps the programs' permissions).
    if (Test-Path mac-zip\NightTechMacCleanup.zip) {
        Copy-Item mac-zip\NightTechMacCleanup.zip installer\Output\NightTechMacCleanup.zip -Force
        Write-Hash installer\Output\NightTechMacCleanup.zip
    }
}
foreach ($dir in "$kit\Field Kit", "$kit\Recovery", "$kit\Wipe", "$kit\Cleanup", "$kit\Burn-in") {
    "This file tells Night Tech tools to keep their logs and reports on this USB stick. Don't delete it." |
        Out-File -Encoding ascii "$dir\portable.txt"
}
Copy-Item packaging\USB-README.txt "$kit\README.txt"
$zip = "installer\Output\NightTechFieldKit-USB.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "publish\usb\*" -DestinationPath $zip
Write-Hash $zip

Write-Host ""
Write-Host "Done. Everything is in installer\Output\" -ForegroundColor Green

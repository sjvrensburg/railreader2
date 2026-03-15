# build-msix.ps1 — Build an MSIX package for Microsoft Store submission.
#
# Prerequisites:
#   - Windows SDK installed (provides makeappx.exe and signtool.exe)
#   - .NET 10 SDK
#   - For Store submission: signing certificate from Partner Center
#   - For local testing: self-signed certificate (see comments below)
#
# Usage:
#   .\msix\build-msix.ps1                              # unsigned (for testing layout)
#   .\msix\build-msix.ps1 -CertPath path\to\cert.pfx   # signed

param(
    [string]$CertPath = "",
    [string]$CertPassword = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$publishDir = Join-Path $repoRoot "publish\msix"
$msixOutput = Join-Path $repoRoot "publish\RailReader2.msix"

Write-Host "=== Building RailReader2 MSIX package ===" -ForegroundColor Cyan

# 1. Publish self-contained
Write-Host "`n[1/4] Publishing self-contained ($Runtime)..."
dotnet publish "$repoRoot\src\RailReader2\RailReader2.csproj" `
    -c $Configuration -r $Runtime --self-contained `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. Copy MSIX assets and manifest
Write-Host "[2/4] Copying MSIX manifest and assets..."
$assetsDir = Join-Path $publishDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Copy-Item "$scriptDir\Package.appxmanifest" -Destination "$publishDir\AppxManifest.xml" -Force
Copy-Item "$scriptDir\Assets\*" -Destination $assetsDir -Force

# 3. Create MSIX package
Write-Host "[3/4] Creating MSIX package..."
$makeappx = Get-Command makeappx.exe -ErrorAction SilentlyContinue
if (-not $makeappx) {
    # Try to find it in Windows SDK
    $sdkPaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe"
        "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\makeappx.exe"
    )
    $found = $sdkPaths | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } | Sort-Object -Descending | Select-Object -First 1
    if ($found) {
        $makeappx = $found.FullName
    } else {
        throw "makeappx.exe not found. Install the Windows SDK."
    }
}

if (Test-Path $msixOutput) { Remove-Item $msixOutput -Force }
& $makeappx pack /d $publishDir /p $msixOutput /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

# 4. Sign (optional)
if ($CertPath -and (Test-Path $CertPath)) {
    Write-Host "[4/4] Signing MSIX package..."
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $sdkPaths = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe"
            "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\signtool.exe"
        )
        $found = $sdkPaths | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } | Sort-Object -Descending | Select-Object -First 1
        if ($found) { $signtool = $found.FullName }
        else { throw "signtool.exe not found. Install the Windows SDK." }
    }

    $signArgs = @("sign", "/fd", "SHA256", "/f", $CertPath)
    if ($CertPassword) { $signArgs += @("/p", $CertPassword) }
    $signArgs += $msixOutput

    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }
    Write-Host "  Signed successfully." -ForegroundColor Green
} else {
    Write-Host "[4/4] Skipping signing (no certificate provided)." -ForegroundColor Yellow
    Write-Host "  For local testing, create a self-signed cert:" -ForegroundColor Yellow
    Write-Host '  New-SelfSignedCertificate -Subject "CN=1760E4F3-7B38-4A64-8D2D-B4F7703D7D10" -Type CodeSigningCert -CertStoreLocation "Cert:\CurrentUser\My"' -ForegroundColor DarkGray
}

$size = (Get-Item $msixOutput).Length / 1MB
Write-Host "`n=== Done: $msixOutput ($([math]::Round($size, 1)) MB) ===" -ForegroundColor Green

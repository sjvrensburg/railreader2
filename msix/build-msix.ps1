# build-msix.ps1 — Build the MSIX package for Microsoft Store submission.
#
# Used by release.yml (the package submitted to the Store) and by test.yml's msix job
# (msix/test-msix-install.ps1), so both build exactly the same layout; also handy locally.
#
# Layout: the app (self-contained, ReadyToRun) plus railreader2-mcp (RailReader2.Mcp.exe, NativeAOT), the
# small MCP server for AI tools (roadmap M8). Only the MCP exe is copied in: it shares the app's own
# e_sqlite3.dll, built from the same package versions. Package.appxmanifest gives it the execution alias
# `railreader2-mcp.exe`, a stable command across Store updates (the install folder's name changes with
# every version).
#
# Prerequisites:
#   - Windows SDK installed (provides makeappx.exe and signtool.exe)
#   - .NET 10 SDK
#   - For Store submission: none (Microsoft re-signs the package during review)
#   - For local install testing: a self-signed certificate whose subject is the manifest's Publisher:
#     New-SelfSignedCertificate -Subject "CN=1760E4F3-7B38-4A64-8D2D-B4F7703D7D10" -Type CodeSigningCert -CertStoreLocation "Cert:\CurrentUser\My"
#
# Usage:
#   .\msix\build-msix.ps1                                   # unsigned
#   .\msix\build-msix.ps1 -CertPath path\to\cert.pfx        # signed
#   .\msix\build-msix.ps1 -Version v3.90.2.0 -ModelPath models\x.onnx -Output railreader2-win-x64.msix   # CI

param(
    [string]$CertPath = "",
    [string]$CertPassword = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # Tag-style version (v3.90.2.0 or 3.90.2.0). Empty: the manifest's own version is kept.
    [string]$Version = "",
    # A layout-model file to bundle under models\. Empty: none (fine for tests).
    [string]$ModelPath = "",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$publishDir = Join-Path $repoRoot "publish\msix"
$mcpDir = Join-Path $repoRoot "publish\mcp"
$msixOutput = if (-not $Output) { Join-Path $repoRoot "publish\RailReader2.msix" }
              elseif ([IO.Path]::IsPathRooted($Output)) { $Output }
              else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $Output)) }

function Find-SdkTool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $found = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name", "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\$name") |
        ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } | Sort-Object -Descending | Select-Object -First 1
    if (-not $found) { throw "$name not found. Install the Windows SDK." }
    return $found.FullName
}

Write-Host "=== Building RailReader2 MSIX package ===" -ForegroundColor Cyan
Remove-Item -Recurse -Force $publishDir, $mcpDir -ErrorAction SilentlyContinue

# 1. Publish the app (self-contained) and railreader2-mcp (NativeAOT)
Write-Host "`n[1/5] Publishing ($Runtime)..."
dotnet publish "$repoRoot\src\RailReader2\RailReader2.csproj" -c $Configuration -r $Runtime --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed" }
dotnet publish "$repoRoot\src\RailReader2.Mcp\RailReader2.Mcp.csproj" -c $Configuration -r $Runtime -o $mcpDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (railreader2-mcp) failed" }
Copy-Item (Join-Path $mcpDir "RailReader2.Mcp.exe") -Destination $publishDir -Force
if (-not (Test-Path (Join-Path $publishDir "e_sqlite3.dll"))) { throw "e_sqlite3.dll missing from the app layout (railreader2-mcp needs it)" }

# 2. Model (optional)
if ($ModelPath) {
    Write-Host "[2/5] Copying model..."
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDir "models") | Out-Null
    Copy-Item $ModelPath -Destination (Join-Path $publishDir "models") -Force
} else {
    Write-Host "[2/5] No model bundled."
}

# 3. Manifest and assets (and the version, if given)
Write-Host "[3/5] Copying MSIX manifest and assets..."
$assetsDir = Join-Path $publishDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$manifestPath = Join-Path $publishDir "AppxManifest.xml"
Copy-Item "$scriptDir\Package.appxmanifest" -Destination $manifestPath -Force
Copy-Item "$scriptDir\Assets\*" -Destination $assetsDir -Force
if ($Version) {
    # MSIX requires a 4-part version with revision = 0 (Microsoft Store requirement).
    $parts = $Version.TrimStart('v').Split('.')
    while ($parts.Count -lt 3) { $parts += '0' }
    $msixVersion = ($parts[0..2] + '0') -join '.'
    $manifest = Get-Content $manifestPath -Raw
    $manifest = $manifest -replace '(<Identity[^>]*)\bVersion="[^"]*"', "`$1Version=`"$msixVersion`""
    [System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))
    Write-Host "  Version $msixVersion"
}

# 4. Pack
Write-Host "[4/5] Creating MSIX package..."
if (Test-Path $msixOutput) { Remove-Item $msixOutput -Force }
& (Find-SdkTool "makeappx.exe") pack /d $publishDir /p $msixOutput /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

# 5. Sign (optional)
if ($CertPath -and (Test-Path $CertPath)) {
    Write-Host "[5/5] Signing MSIX package..."
    $signArgs = @("sign", "/fd", "SHA256", "/f", $CertPath)
    if ($CertPassword) { $signArgs += @("/p", $CertPassword) }
    $signArgs += $msixOutput
    & (Find-SdkTool "signtool.exe") @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }
    Write-Host "  Signed successfully." -ForegroundColor Green
} else {
    Write-Host "[5/5] Skipping signing (no certificate provided)." -ForegroundColor Yellow
}

$size = (Get-Item $msixOutput).Length / 1MB
Write-Host "`n=== Done: $msixOutput ($([math]::Round($size, 1)) MB) ===" -ForegroundColor Green

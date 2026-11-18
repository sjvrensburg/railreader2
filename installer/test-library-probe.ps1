# Library search on the real Windows app (railreader2#298), run by .github/workflows/test.yml and release.yml.
#
# The CLI's `library probe` already runs on Windows, but the app is ReadyToRun, not AOT, and loads its
# native libraries (e_sqlite3.dll, pdfium.dll) its own way, so it gets its own check. `RailReader2.exe
# --library-probe` (src/RailReader2/Services/LibraryProbeMode.cs) writes a one-page PDF, indexes it into a
# temporary database through the app's own SQLite and PDFium, searches it, and exits 0 or 1 without opening
# a window. It is a GUI-subsystem executable, so its report comes back through --output.
#
# Checks, for the published folder and then for a silent per-user install of the Inno installer built
# from it (or the one given with -Setup):
#   - the probe exits 0 and reports OK;
#   - SQLite was loaded from the app's own folder, not from somewhere else on the machine;
#   - railreader2-mcp (RailReader2.Mcp.exe, roadmap M8) is beside the app and answers over real stdio pipes:
#     a tool call is refused while AI tools are off, and reaches the library once `enable` turned them on.
param(
    [string]$Publish = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish'),
    [string]$Setup,
    [switch]$SkipInstall,
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    # RUNNER_TEMP on CI: GetTempPath() can be an 8.3 short path there (RUNNER~1), while Windows reports
    # loaded modules by their long path, which would fail the "loaded from the app folder" check.
    [string]$Work = (Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) "rr-library-probe")
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failures = @()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures += $what }
}

Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null

function Probe([string]$appDir, [string]$label) {
    $exe = Join-Path $appDir 'RailReader2.exe'
    if (-not (Test-Path $exe)) { Check $false "$label - RailReader2.exe exists ($exe)"; return }
    $report = Join-Path $Work "probe-$label.txt"
    $p = Start-Process $exe -ArgumentList '--library-probe', '--output', "`"$report`"" -PassThru -Wait
    $text = if (Test-Path $report) { Get-Content $report -Raw } else { '' }
    Write-Host "---- $label probe report ----"
    Write-Host $text
    Check ($p.ExitCode -eq 0) "$label - probe exit code 0 (was $($p.ExitCode))"
    Check ($text -match '(?m)^OK\s*$') "$label - probe reports OK"
    $loaded = [regex]::Match($text, 'Native SQLite loaded from: (.+)').Groups[1].Value.Trim()
    $full = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'
    Check ($loaded.StartsWith($full, [StringComparison]::OrdinalIgnoreCase)) "$label - SQLite loaded from the app folder ($loaded)"
}

# Sends JSON-RPC lines to RailReader2.Mcp.exe over stdin and returns its stdout.
function Mcp([string]$exe, [string[]]$arguments, [string[]]$lines) {
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($psi)
    foreach ($l in $lines) { $p.StandardInput.Write($l + "`n") }
    $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(30000)) { $p.Kill() }
    return $out
}

function McpCheck([string]$appDir, [string]$label) {
    $exe = Join-Path $appDir 'RailReader2.Mcp.exe'
    if (-not (Test-Path $exe)) { Check $false "$label - RailReader2.Mcp.exe exists ($exe)"; return }
    $prefs = Join-Path $Work "mcp-prefs-$label.json"
    $db = Join-Path $Work "no-such-library-$label.db"
    $calls = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_documents","arguments":{}}}')
    $off = Mcp $exe @('--prefs', $prefs, '--db', $db) $calls
    Write-Host "---- $label railreader2-mcp (off) ----"; Write-Host $off
    Check ($off -match '"protocolVersion":"2025-06-18"') "$label - railreader2-mcp answers initialize over stdio"
    Check ($off -match 'turned off') "$label - railreader2-mcp refuses tools while AI tools are off"
    & $exe enable --prefs $prefs | Out-Host
    Check ($LASTEXITCODE -eq 0) "$label - railreader2-mcp enable exits 0"
    $on = Mcp $exe @('--prefs', $prefs, '--db', $db) $calls
    Check ($on -match 'library is empty') "$label - railreader2-mcp reaches the (empty) library once enabled"
}

Probe $Publish 'published'
McpCheck $Publish 'published'

if (-not $SkipInstall) {
    if (-not $Setup) {
        $icon = Join-Path $root 'installer\icon.ico'
        if (-not (Test-Path $icon)) { magick (Join-Path $root 'assets\railreader2.png') -define icon:auto-resize=256,48,32,16 $icon }
        & $Iscc /Q '/DAPP_VERSION=0.0.0.0' (Join-Path $root 'installer\railreader2.iss')
        if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
        $Setup = Join-Path $root 'installer\output\railreader2-setup-x64.exe'
    }
    $installDir = Join-Path $Work 'installed'
    $log = Join-Path $Work 'install.log'
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER',
        "/DIR=`"$installDir`"", "/LOG=`"$log`"" -PassThru -Wait
    Check ($p.ExitCode -eq 0) "installer exit code 0 (was $($p.ExitCode))"
    Check (Test-Path (Join-Path $installDir 'e_sqlite3.dll')) 'installer put e_sqlite3.dll in the app folder'
    Probe $installDir 'installed'
    McpCheck $installDir 'installed'
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:"
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "`nAll library probe checks passed."

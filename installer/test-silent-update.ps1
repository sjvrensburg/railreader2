# Silent in-app update test for the Inno installer (run by .github/workflows/test.yml on Windows).
#
# Builds two installers around a placeholder app (whoami.exe copied as RailReader2.exe), installs
# 1.0.0.0 per-user, then runs 2.0.0.0 the way the app does after it exits
# (src/RailReader2.Shared/Updates/UpdateInstaller.cs InnoArguments). Checks that:
#   - /WAITPID makes the installer wait for the "app" (a process that lives ~8 s) before installing;
#   - the upgrade lands in the same place and records the new version (/DAPP_VERSION reaches AppVersion);
#   - /RELAUNCH=1 runs the app once afterwards, and a plain silent install does not.
param(
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) "rr-installer-test")
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failures = @()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures += $what }
}

Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null

# Placeholder payload: the .iss packs ..\publish\* and needs installer\icon.ico.
$publish = Join-Path $root 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null
Copy-Item "$env:WINDIR\System32\whoami.exe" (Join-Path $publish 'RailReader2.exe') -Force
$icon = Join-Path $root 'installer\icon.ico'
if (-not (Test-Path $icon)) { magick (Join-Path $root 'assets\railreader2.png') -define icon:auto-resize=256,48,32,16 $icon }

foreach ($v in '1.0.0.0', '2.0.0.0') {
    & $Iscc /Q "/DAPP_VERSION=$v" (Join-Path $root 'installer\railreader2.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $v" }
    Move-Item (Join-Path $root 'installer\output\railreader2-setup-x64.exe') (Join-Path $Work "setup-$v.exe") -Force
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8A3F2D1-7B4C-4E5A-9F6D-2C8B1A3E4F5D}_is1'
function Run-Setup([string]$version, [string[]]$extra) {
    $log = Join-Path $Work "install-$version-$([guid]::NewGuid().ToString('N').Substring(0,6)).log"
    $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/LOG=$log") + $extra
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process (Join-Path $Work "setup-$version.exe") -ArgumentList $setupArgs -PassThru -Wait
    return [pscustomobject]@{ Exit = $p.ExitCode; Seconds = $sw.Elapsed.TotalSeconds; Log = (Get-Content $log -Raw) }
}
function Run-Entries([string]$log) { ([regex]::Matches($log, '-- Run entry --')).Count }

# 1. Fresh install of 1.0.0.0: no relaunch requested, so the postinstall entry is skipped (skipifsilent).
$r1 = Run-Setup '1.0.0.0' @()
Check ($r1.Exit -eq 0) "1.0.0.0 installs silently (exit $($r1.Exit))"
$installed = (Get-ItemProperty $uninstallKey)
Check ($installed.DisplayVersion -eq '1.0.0.0') "installed version is 1.0.0.0 (got '$($installed.DisplayVersion)')"
Check ((Run-Entries $r1.Log) -eq 0) "plain silent install launches nothing"
$appDir = $installed.InstallLocation

# 2. The update as the app runs it: wait for the app's PID, install, relaunch.
$app = Start-Process ping -ArgumentList '-n', '9', '127.0.0.1' -PassThru -WindowStyle Hidden   # ~8 s
$r2 = Run-Setup '2.0.0.0' @("/WAITPID=$($app.Id)", '/RELAUNCH=1')
$appGone = $app.HasExited
Check ($r2.Exit -eq 0) "2.0.0.0 installs silently (exit $($r2.Exit))"
Check ($appGone) "the waited-for process had exited when the install finished"
Check ($r2.Seconds -ge 6) "the installer waited for the app (took $([math]::Round($r2.Seconds,1)) s; the app lived ~8 s)"
$after = (Get-ItemProperty $uninstallKey)
Check ($after.DisplayVersion -eq '2.0.0.0') "installed version is 2.0.0.0 (got '$($after.DisplayVersion)')"
Check ($after.InstallLocation -eq $appDir) "upgrade went to the same folder ($($after.InstallLocation))"
Check ((Run-Entries $r2.Log) -eq 1) "/RELAUNCH=1 runs the app once"
Check ($r2.Log -match [regex]::Escape('RailReader2.exe')) "the relaunched program is RailReader2.exe"

# 3. A PID that is already gone must not hold the installer up.
$gone = Start-Process cmd -ArgumentList '/c', 'exit' -PassThru -Wait
$r3 = Run-Setup '2.0.0.0' @("/WAITPID=$($gone.Id)")
Check ($r3.Exit -eq 0 -and $r3.Seconds -lt 30) "a stale /WAITPID doesn't block (took $([math]::Round($r3.Seconds,1)) s)"
Check ((Run-Entries $r3.Log) -eq 0) "no relaunch without /RELAUNCH=1"

Remove-Item -Recurse -Force $publish
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed"; exit 1 }
Write-Host "All installer checks passed"

# Installs the Microsoft Store package on real Windows and checks railreader2-mcp inside it (roadmap M8),
# run by .github/workflows/test.yml's msix job. Until now the MSIX was only ever built, never installed.
#
# Builds the MSIX with msix/build-msix.ps1, signs it with a throwaway self-signed certificate whose subject is
# the manifest's Publisher (the Store re-signs the real one), trusts that certificate, installs the package,
# and then:
#   - checks the execution alias railreader2-mcp.exe exists at its stable path;
#   - runs `railreader2-mcp enable` through it and reports where Windows actually put the preferences file:
#     the real %APPDATA%\railreader2, or the package's private copy of AppData
#     (%LOCALAPPDATA%\Packages\<family>\LocalCache\Roaming\railreader2). Either is a pass; it decides how the
#     Settings switches must write other programs' configs;
#   - puts a one-document library index beside it and drives the server over real stdin/stdout pipes
#     (initialize, tools/list, list_documents, search_pages), as an agent would;
#   - FINDINGS only (never a failure): from inside the package, modifies an existing file in the real
#     %APPDATA%\Claude and creates a new one there, and reports whether the real files changed. That is the
#     Claude Desktop config case (claude_desktop_config.json), decided by MSIX's AppData virtualisation.
#
# Windows PowerShell 5.1 (the Appx cmdlets, Invoke-CommandInDesktopPackage included, are native there).
param(
    [string]$Work = (Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) "rr-msix")
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failures = @()
$findings = @()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures += $what }
}
function Finding([string]$what) { Write-Host "FINDING  $what"; $script:findings += $what }

Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null

# ---------------------------------------------------------------- sign, build, install
[xml]$manifest = Get-Content (Join-Path $root 'msix\Package.appxmanifest')
$publisher = $manifest.Package.Identity.Publisher
$packageName = $manifest.Package.Identity.Name

$cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
    -FriendlyName 'RailReader2 MSIX test (throwaway)' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$pfx = Join-Path $Work 'test.pfx'
$password = [Guid]::NewGuid().ToString('N')
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
$cer = Join-Path $Work 'test.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

$msix = Join-Path $Work 'railreader2-test.msix'
& (Join-Path $root 'msix\build-msix.ps1') -CertPath $pfx -CertPassword $password -Output $msix
Check (Test-Path $msix) "MSIX built and signed"

$installed = $false
try {
    Add-AppxPackage -Path $msix
    $pkg = Get-AppxPackage -Name $packageName
    $installed = $null -ne $pkg
    Check $installed "package installs (Add-AppxPackage)"
    if (-not $installed) { throw "not installed" }
    $pfn = $pkg.PackageFamilyName
    Write-Host "Installed $($pkg.PackageFullName) at $($pkg.InstallLocation)"
    Check (Test-Path (Join-Path $pkg.InstallLocation 'RailReader2.Mcp.exe')) "RailReader2.Mcp.exe is in the package"

    # ------------------------------------------------------------ the alias
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\railreader2-mcp.exe'
    Check (Test-Path $alias) "execution alias exists at $alias"

    $realDir = Join-Path $env:APPDATA 'railreader2'
    $virtDir = Join-Path $env:LOCALAPPDATA "Packages\$pfn\LocalCache\Roaming\railreader2"
    if (Test-Path $realDir) { Finding "real $realDir existed before the test (results below assume a fresh profile)" }

    $out = & $alias enable | Out-String  # no 2>&1: under Stop, 5.1 turns native stderr into errors
    Write-Host $out
    Check ($LASTEXITCODE -eq 0) "railreader2-mcp enable through the alias exits 0 (was $LASTEXITCODE)"

    $inReal = Test-Path (Join-Path $realDir 'ai_tools_prefs.json')
    $inVirt = Test-Path (Join-Path $virtDir 'ai_tools_prefs.json')
    Check ($inReal -or $inVirt) "the preferences file was written somewhere we can find"
    if ($inVirt) { Finding "AppData is VIRTUALISED for the package: ConfigDir is $virtDir (the real $realDir is not used)" }
    if ($inReal) { Finding "AppData is NOT virtualised for the package: ConfigDir is the real $realDir" }
    $configDir = if ($inVirt) { $virtDir } else { $realDir }

    # ------------------------------------------------------------ a one-document index
    # A minimal one-page PDF (PDFium rebuilds the cross-reference table, so offsets need not be exact).
    $pdfDir = Join-Path $Work 'pdfs'
    New-Item -ItemType Directory -Force $pdfDir | Out-Null
    $pdf = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>endobj`n" +
        "4 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`n" +
        "5 0 obj<</Length 66>>stream`nBT /F1 18 Tf 72 700 Td (The identification strategy matters) Tj ET`nendstream endobj`n" +
        "trailer<</Root 1 0 R>>`n%%EOF`n"
    [IO.File]::WriteAllText((Join-Path $pdfDir 'identification-paper.pdf'), $pdf, [Text.Encoding]::ASCII)
    $seed = Join-Path $Work 'seed.db'
    dotnet run --project (Join-Path $root 'src\RailReader2.Cli') -c Release -- library bench $pdfDir --db $seed | Out-Host
    Check (Test-Path $seed) "seed index built"
    New-Item -ItemType Directory -Force (Join-Path $configDir 'library') | Out-Null
    Copy-Item $seed (Join-Path $configDir 'library\library.db') -Force

    # ------------------------------------------------------------ stdio through the alias
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $alias
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"msix-test","version":"0"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
        '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_documents","arguments":{}}}',
        '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_pages","arguments":{"query":"identification"}}}'
    )
    foreach ($r in $requests) { $p.StandardInput.Write($r + "`n") }
    $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    if (-not $p.WaitForExit(30000)) { $p.Kill(); Check $false "server exits when stdin closes" }
    Write-Host "---- stdout ----"; Write-Host $stdout
    if ($stderr) { Write-Host "---- stderr ----"; Write-Host $stderr }
    Check ($p.ExitCode -eq 0) "server exit code 0 (was $($p.ExitCode))"

    $replies = @($stdout -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    Check ($replies.Count -eq 4) "four replies on stdout, nothing else (got $($replies.Count))"
    $byId = @{}; foreach ($r in $replies) { $byId["$($r.id)"] = $r }
    Check ($byId['1'].result.protocolVersion -eq '2025-06-18') "initialize negotiates 2025-06-18"
    Check (@($byId['2'].result.tools).Count -eq 4) "tools/list lists four tools"
    $list = $byId['3'].result
    Check ($list -and -not $list.isError -and $list.content[0].text -like '*identification-paper.pdf*') "list_documents sees the index in the package's ConfigDir"
    $search = $byId['4'].result
    Check ($search -and -not $search.isError -and $search.content[0].text -like '*"page":1*') "search_pages finds the page"
    # JSON doubles backslashes, so look for a folder name rather than the path itself.
    Check (-not ($stdout -like "*$(Split-Path $Work -Leaf)*")) "no file path in the output"

    $status = & $alias status | Out-String
    Write-Host $status

    # ------------------------------------------------------------ findings: another program's AppData config
    $claudeDir = Join-Path $env:APPDATA 'Claude'
    New-Item -ItemType Directory -Force $claudeDir | Out-Null
    $claudeConfig = Join-Path $claudeDir 'claude_desktop_config.json'
    Set-Content -Path $claudeConfig -Value '{"mcpServers":{}}' -Encoding ASCII
    $done = Join-Path $Work 'probe-done.txt'
    $probe = Join-Path $Work 'probe.ps1'
    Set-Content -Path $probe -Encoding ASCII -Value @"
Set-Content -Path '$claudeConfig' -Value 'MODIFIED-IN-PACKAGE' -Encoding ASCII
Set-Content -Path '$claudeDir\new-in-package.json' -Value 'NEW' -Encoding ASCII
Set-Content -Path '$done' -Value 'done' -Encoding ASCII
"@
    try {
        Invoke-CommandInDesktopPackage -PackageFamilyName $pfn -AppId 'RailReader2Mcp' `
            -Command "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -Args "-NoProfile -ExecutionPolicy Bypass -File `"$probe`""
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        if (-not (Test-Path $done)) {
            Finding "the in-package probe did not finish within 60 s (no result for the Claude Desktop case)"
        } else {
            $real = (Get-Content $claudeConfig -Raw).Trim()
            $virtClaude = Join-Path $env:LOCALAPPDATA "Packages\$pfn\LocalCache\Roaming\Claude"
            if ($real -eq 'MODIFIED-IN-PACKAGE') { Finding "editing an EXISTING file in real %APPDATA%\Claude from the package changes the real file" }
            else { Finding "editing an EXISTING file in real %APPDATA%\Claude from the package does NOT change the real file (real still: $real)" }
            if (Test-Path (Join-Path $claudeDir 'new-in-package.json')) { Finding "a NEW file in real %APPDATA%\Claude from the package lands in the real folder" }
            elseif (Test-Path (Join-Path $virtClaude 'new-in-package.json')) { Finding "a NEW file in real %APPDATA%\Claude from the package is redirected to $virtClaude" }
            else { Finding "a NEW file in real %APPDATA%\Claude from the package was not found in either place" }
        }
    }
    catch { Finding "Invoke-CommandInDesktopPackage failed, no result for the Claude Desktop case: $($_.Exception.Message)" }
}
finally {
    if ($installed) { Get-AppxPackage -Name $packageName | Remove-AppxPackage -ErrorAction SilentlyContinue }
    Get-ChildItem 'Cert:\LocalMachine\TrustedPeople', 'Cert:\CurrentUser\My' |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint } | Remove-Item -ErrorAction SilentlyContinue
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @('## MSIX install check', '') + ($findings | ForEach-Object { "- $_" }) + @('', "Failures: $($failures.Count)")
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n")
}
Write-Host ""
Write-Host "Findings:"; $findings | ForEach-Object { Write-Host "  $_" }
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed:"; $failures | ForEach-Object { Write-Host "  $_" }; exit 1 }
Write-Host "All MSIX checks passed."

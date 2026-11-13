<#
.SYNOPSIS
  nx-ai-bridge self-check, part 1 of 2 (outside NX). No administrator rights, no Python needed.

.DESCRIPTION
  1. Copies the receiver journals to %USERPROFILE%\.nx-ai-bridge (same as install.ps1).
  2. Records the environment: Windows, .NET, NX location and version, run_journal.exe, licence variables.
  3. Tries BATCH mode: runs the self-check journal through run_journal.exe (no NX window).
  4. Writes %USERPROFILE%\.nx-ai-bridge\reports\env-report.json and tells you what to do next:

     PART 2: in NX press Alt+F8, play NxAiBridgeSelfCheck.cs, answer one question.
     The journal writes ONE report file (also copied to the Desktop) that includes everything
     collected here. Bring that file back / attach it to a GitHub issue.

  Works with Windows PowerShell 5.1. ASCII-only on purpose.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File selfcheck.ps1
#>
param(
    [string]$HomeDir = "",
    [string]$NxDir = "",
    [switch]$SkipBatch,
    [int]$BatchTimeoutSeconds = 600,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
if (-not $HomeDir) {
    if ($env:NX_AI_BRIDGE_HOME) { $HomeDir = $env:NX_AI_BRIDGE_HOME } else { $HomeDir = Join-Path $env:USERPROFILE ".nx-ai-bridge" }
}

Write-Host "nx-ai-bridge self-check (part 1: outside NX)"
$installArgs = @{ HomeDir = $HomeDir; SkipPython = $true; Quiet = $true }
if ($NxDir) { $installArgs["NxDir"] = $NxDir }
$install = (& (Join-Path $RepoDir "install.ps1") @installArgs | Out-String) | ConvertFrom-Json

$reports = Join-Path $HomeDir "reports"
New-Item -ItemType Directory -Force -Path $reports | Out-Null

function Try-Get([scriptblock]$block) { try { & $block } catch { "ERROR: $($_.Exception.Message)" } }

$env_ = [ordered]@{
    generated          = (Get-Date).ToString("s")
    windows            = Try-Get { (Get-CimInstance Win32_OperatingSystem).Caption + " " + [System.Environment]::OSVersion.Version.ToString() }
    powershell         = $PSVersionTable.PSVersion.ToString()
    is_admin           = Try-Get { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    execution_policy   = Try-Get { (Get-ExecutionPolicy).ToString() }
    dotnet_release     = Try-Get { (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full").Release }
    csc_exists         = Test-Path (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe")
    vbc_exists         = Test-Path (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\vbc.exe")
    home               = $HomeDir
    home_writable      = Try-Get { $t = Join-Path $HomeDir "write-test.tmp"; "x" | Out-File $t; Remove-Item $t; $true }
    nx_found           = $install.nx_found
    nx_base_dir        = $install.nx_base_dir
    nx_version         = $install.nx_version
    run_journal_exe    = $install.run_journal_exe
    run_journal_exists = [bool]($install.run_journal_exe -and (Test-Path $install.run_journal_exe))
    nx_running         = [bool](Get-Process -Name ugraf -ErrorAction SilentlyContinue)
    UGII_BASE_DIR      = $env:UGII_BASE_DIR
    UGII_ROOT_DIR      = $env:UGII_ROOT_DIR
    UGII_LANG          = $env:UGII_LANG
    SPLM_LICENSE_SERVER = $env:SPLM_LICENSE_SERVER
    UGS_LICENSE_SERVER = $env:UGS_LICENSE_SERVER
    UGII_LICENSE_BUNDLE = $env:UGII_LICENSE_BUNDLE
    python             = Try-Get { $c = Get-Command python -ErrorAction SilentlyContinue; if ($c) { (& $c.Source --version 2>&1 | Out-String).Trim() + " (" + $c.Source + ")" } else { $null } }
    uv                 = Try-Get { $c = Get-Command uv -ErrorAction SilentlyContinue; if ($c) { $c.Source } else { $null } }
    selfcheck_journal  = $install.selfcheck_journal
}

# ---------------------------------------------------------------- batch mode through run_journal.exe
$batch = [ordered]@{ attempted = $false }
if ($SkipBatch) {
    $batch["skipped"] = "-SkipBatch"
} elseif (-not $env_.run_journal_exists) {
    $batch["skipped"] = "run_journal.exe not found"
} else {
    Write-Host "Trying batch mode (run_journal.exe, no NX window). This can take a few minutes..."
    $batch["attempted"] = $true
    $before = @(Get-ChildItem -Path $reports -Filter "nx-ai-bridge-selfcheck-*-batch.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $outFile = Join-Path $reports "run_journal-stdout.txt"
    $errFile = Join-Path $reports "run_journal-stderr.txt"
    $saved = @{ home = $env:NX_AI_BRIDGE_HOME; base = $env:UGII_BASE_DIR; root = $env:UGII_ROOT_DIR }
    try {
        $env:NX_AI_BRIDGE_HOME = $HomeDir
        if (-not $env:UGII_BASE_DIR) { $env:UGII_BASE_DIR = $install.nx_base_dir }
        if (-not $env:UGII_ROOT_DIR) { $env:UGII_ROOT_DIR = (Join-Path $install.nx_base_dir "UGII") + "\" }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $argLine = '"' + $install.selfcheck_journal + '" -args --no-ui'
        $proc = Start-Process -FilePath $install.run_journal_exe -ArgumentList $argLine -PassThru -NoNewWindow `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        if (-not $proc.WaitForExit($BatchTimeoutSeconds * 1000)) {
            try { $proc.Kill() } catch { }
            $batch["timed_out"] = $true
        } else {
            $batch["exit_code"] = $proc.ExitCode
        }
        $batch["seconds"] = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    } catch {
        $batch["error"] = $_.Exception.Message
    } finally {
        $env:NX_AI_BRIDGE_HOME = $saved.home
        $env:UGII_BASE_DIR = $saved.base
        $env:UGII_ROOT_DIR = $saved.root
    }
    foreach ($pair in @(@("stdout_tail", $outFile), @("stderr_tail", $errFile))) {
        if (Test-Path $pair[1]) {
            $text = Get-Content -Raw -Path $pair[1] -ErrorAction SilentlyContinue
            if ($text) { $batch[$pair[0]] = $text.Substring([math]::Max(0, $text.Length - 3000)) }
        }
    }
    $new = @(Get-ChildItem -Path $reports -Filter "nx-ai-bridge-selfcheck-*-batch.json" -ErrorAction SilentlyContinue |
        Where-Object { $before -notcontains $_.Name } | ForEach-Object { $_.Name })
    $batch["report_written"] = ($new.Count -gt 0)
    if ($new.Count -gt 0) { $batch["report_file"] = $new[0] }
    $loaderError = Join-Path $HomeDir "logs\loader-error.txt"
    if (Test-Path $loaderError) { $batch["loader_error"] = (Get-Content -Raw -Path $loaderError) }
    if ($batch["report_written"]) { Write-Host "  batch mode WORKS (report: $($batch['report_file']))" }
    else { Write-Host "  batch mode did not produce a report (details are recorded; this is useful information too)" }
}
$env_["batch"] = $batch

$envFile = Join-Path $reports "env-report.json"
[System.IO.File]::WriteAllText($envFile, ($env_ | ConvertTo-Json -Depth 6), $Utf8NoBom)

Write-Host ""
Write-Host "Part 1 done: $envFile"
Write-Host ""
Write-Host "PART 2 (inside NX, about one minute):"
Write-Host "  1. Start NX. Close your own parts first (the check creates and closes test parts)."
Write-Host "  2. Press Alt+F8  (menu: Tools > Journal > Play...)."
Write-Host "  3. Select this file and click Run:"
Write-Host "       $($install.selfcheck_journal)"
Write-Host "     (if NX cannot play .cs files, use the .vb file in the 'vb' sub-folder instead)"
Write-Host "  4. Wait for the window 'NX AI Bridge - Self-check', try to rotate the model, answer the question."
Write-Host "  5. Bring back the report shown at the end (a copy is put on the Desktop):"
Write-Host "       $reports\nx-ai-bridge-selfcheck-<time>.json"
Write-Host ""
if (-not $NoPause -and $Host.Name -eq "ConsoleHost") { Read-Host "Press Enter to close" | Out-Null }

<#
.SYNOPSIS
  nx-ai-bridge installer for Windows. No administrator rights needed, nothing is written
  outside your user profile.

.DESCRIPTION
  1. Finds the NX installation (UGII_BASE_DIR, running NX, registry, common folders).
  2. Copies the receiver journals to  %USERPROFILE%\.nx-ai-bridge\nx_receiver  (fixed location).
  3. Writes  %USERPROFILE%\.nx-ai-bridge\config.json.
  4. Prepares Python (uses uv or an existing Python 3.10+; never installs anything system-wide).
  5. Prints the MCP configuration for common AI tools and the one manual step in NX.

  Works with Windows PowerShell 5.1 (built into Windows 10). This file is ASCII-only on purpose.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -NxDir "D:\Siemens\NX 10.0" -InstallUv
#>
param(
    [string]$HomeDir = "",
    [string]$NxDir = "",
    [int]$Port = 47110,
    [switch]$SkipPython,
    [switch]$InstallUv,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Say([string]$text) { if (-not $Quiet) { Write-Host $text } }

if (-not $HomeDir) {
    if ($env:NX_AI_BRIDGE_HOME) { $HomeDir = $env:NX_AI_BRIDGE_HOME } else { $HomeDir = Join-Path $env:USERPROFILE ".nx-ai-bridge" }
}
New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null

# ---------------------------------------------------------------- 1. find NX
# Join-Path fails for drives that do not exist (e.g. "D:\Siemens" on a PC without D:), so combine as plain strings.
function Join-Safe([string]$a, [string]$b) { return [System.IO.Path]::Combine($a, $b) }

function Test-NxDir([string]$dir) {
    if (-not $dir) { return $false }
    foreach ($sub in @("NXBIN", "UGII")) {
        if (Test-Path -LiteralPath (Join-Safe (Join-Safe $dir $sub) "ugraf.exe") -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

function Find-Nx {
    $candidates = New-Object System.Collections.ArrayList
    if ($NxDir) { [void]$candidates.Add($NxDir) }
    if ($env:UGII_BASE_DIR) { [void]$candidates.Add($env:UGII_BASE_DIR) }
    try {
        Get-Process -Name ugraf -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Path) { [void]$candidates.Add((Split-Path -Parent (Split-Path -Parent $_.Path))) }
        }
    } catch { }
    foreach ($key in @("HKLM:\SOFTWARE\Unigraphics Solutions\Installed Applications",
                       "HKLM:\SOFTWARE\WOW6432Node\Unigraphics Solutions\Installed Applications",
                       "HKLM:\SOFTWARE\Siemens\NX")) {
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($p in $props.PSObject.Properties) {
                if ($p.Value -is [string] -and $p.Value -match '^[A-Za-z]:\\') { [void]$candidates.Add($p.Value.TrimEnd('\')) }
            }
        } catch { }
    }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "C:\Siemens", "D:\Siemens", "D:\Program Files", "E:\Siemens")) {
        if (-not $root) { continue }
        foreach ($parent in @($root, (Join-Safe $root "Siemens"), (Join-Safe $root "UGS"))) {
            if (Test-Path -LiteralPath $parent -ErrorAction SilentlyContinue) {
                Get-ChildItem -Path $parent -Directory -Filter "NX*" -ErrorAction SilentlyContinue |
                    Sort-Object Name | ForEach-Object { [void]$candidates.Add($_.FullName) }
            }
        }
    }
    foreach ($c in $candidates) { if (Test-NxDir $c) { return $c.TrimEnd('\') } }
    return $null
}

$nx = Find-Nx
$runJournal = $null
$nxVersion = $null
if ($nx) {
    foreach ($sub in @("NXBIN", "UGII")) {
        $cand = Join-Safe (Join-Safe $nx $sub) "run_journal.exe"
        if ((-not $runJournal) -and (Test-Path $cand)) { $runJournal = $cand }
        $ugraf = Join-Safe (Join-Safe $nx $sub) "ugraf.exe"
        if ((-not $nxVersion) -and (Test-Path $ugraf)) {
            try { $nxVersion = (Get-Item $ugraf).VersionInfo.ProductVersion } catch { }
        }
    }
    Say "NX found:        $nx  (version $nxVersion)"
} else {
    Say "NX NOT found. Pass -NxDir ""C:\Program Files\Siemens\NX 10.0"" or set UGII_BASE_DIR. Live mode still works: the receiver is started from inside NX."
}

# ---------------------------------------------------------------- 2. copy the receiver
$src = Join-Path $RepoDir "nx_receiver"
$dst = Join-Path $HomeDir "nx_receiver"
if (-not (Test-Path (Join-Path $src "NxAiBridgeReceiver.cs"))) { throw "nx_receiver folder not found next to install.ps1 ($src)" }
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
Copy-Item -Recurse -Force $src $dst
$journal = Join-Path $dst "NxAiBridgeReceiver.cs"
$selfcheckJournal = Join-Path $dst "NxAiBridgeSelfCheck.cs"
Say "Receiver copied: $dst"

# ---------------------------------------------------------------- 3. config.json
$configFile = Join-Path $HomeDir "config.json"
$config = @{}
if (Test-Path $configFile) {
    try {
        $old = Get-Content -Raw -Path $configFile | ConvertFrom-Json
        foreach ($p in $old.PSObject.Properties) { $config[$p.Name] = $p.Value }
    } catch { }
}
$config["port"] = $Port
if ($nx) { $config["nx_base_dir"] = $nx }
if ($runJournal) { $config["run_journal_exe"] = $runJournal }
if ($nxVersion) { $config["nx_version"] = $nxVersion }
[System.IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), $Utf8NoBom)

# ---------------------------------------------------------------- 4. Python
$command = $null      # command line that starts the MCP server
$pythonNote = ""
if (-not $SkipPython) {
    $uv = Get-Command uv -ErrorAction SilentlyContinue
    if ((-not $uv) -and $InstallUv) {
        Say "Installing uv into your user profile (no admin rights needed)..."
        try {
            Invoke-Expression (Invoke-RestMethod -Uri "https://astral.sh/uv/install.ps1")
            $env:Path = (Join-Path $env:USERPROFILE ".local\bin") + ";" + $env:Path
            $uv = Get-Command uv -ErrorAction SilentlyContinue
        } catch { $pythonNote = "uv installation failed: $($_.Exception.Message)" }
    }
    if ($uv) {
        # uv creates and caches an isolated environment on first start; nothing else to install
        $command = @($uv.Source, "run", "--project", $RepoDir, "nx-ai-bridge", "serve")
        $pythonNote = "using uv ($($uv.Source))"
    } else {
        $python = $null
        foreach ($name in @("python", "python3", "py")) {
            $cmd = Get-Command $name -ErrorAction SilentlyContinue
            if (-not $cmd) { continue }
            try {
                $v = & $cmd.Source -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
                if ($v -and ([version]$v -ge [version]"3.10")) { $python = $cmd.Source; break }
            } catch { }
        }
        if ($python) {
            $venv = Join-Path $HomeDir "venv"
            try {
                if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) { & $python -m venv $venv | Out-Null }
                & (Join-Path $venv "Scripts\python.exe") -m pip install --quiet --upgrade $RepoDir | Out-Null
                $exe = Join-Path $venv "Scripts\nx-ai-bridge.exe"
                if (Test-Path $exe) { $command = @($exe, "serve"); $pythonNote = "installed into $venv with $python" }
                else { $pythonNote = "pip install did not produce $exe" }
            } catch { $pythonNote = "pip install failed: $($_.Exception.Message)" }
        } else {
            $pythonNote = "No Python 3.10+ and no uv found. Easiest fix (no admin rights): run install.ps1 again with -InstallUv"
        }
    }
    Say "Python:          $pythonNote"
}

# ---------------------------------------------------------------- 5. summary (JSON, for AI agents) + instructions
$mcp = $null
if ($command) {
    $args2 = @()
    if ($command.Count -gt 1) { $args2 = $command[1..($command.Count - 1)] }
    $mcp = @{ mcpServers = @{ nx = @{ command = $command[0]; args = $args2 } } }
}
$summary = [ordered]@{
    ok                 = $true
    home               = $HomeDir
    nx_found           = [bool]$nx
    nx_base_dir        = $nx
    nx_version         = $nxVersion
    run_journal_exe    = $runJournal
    receiver_journal   = $journal
    selfcheck_journal  = $selfcheckJournal
    python             = $pythonNote
    mcp_server_command = $command
    mcp_config         = $mcp
    next_step_in_nx    = "In NX press Alt+F8 (Tools > Journal > Play...), select $journal and click Run. Keep the small 'NX AI Bridge' window open."
}
$summaryFile = Join-Path $HomeDir "install-summary.json"
[System.IO.File]::WriteAllText($summaryFile, ($summary | ConvertTo-Json -Depth 6), $Utf8NoBom)

if (-not $Quiet) {
    Write-Host ""
    Write-Host "==================== nx-ai-bridge is set up (ALPHA) ===================="
    if ($command) {
        Write-Host "MCP server command:"
        Write-Host ("  " + ($command -join " "))
        Write-Host "Claude Code:"
        Write-Host ("  claude mcp add nx -- " + (($command | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join " "))
        Write-Host "Cursor / Claude Desktop / Gemini CLI / Windsurf (mcpServers JSON):"
        Write-Host ($mcp | ConvertTo-Json -Depth 6)
    }
    Write-Host ""
    Write-Host "THE ONE MANUAL STEP - start the receiver inside NX:"
    Write-Host "  1. Start NX and open or create a part."
    Write-Host "  2. Press Alt+F8  (menu: Tools > Journal > Play...)."
    Write-Host "  3. Select  $journal  and click Run."
    Write-Host "  4. A small 'NX AI Bridge' window appears. Leave it open. Done."
    Write-Host ""
    Write-Host "Summary for AI agents: $summaryFile"
}
$summary | ConvertTo-Json -Depth 6

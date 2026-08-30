#Requires -Version 5.1

<#
.SYNOPSIS
    Install or upgrade Periphery.Cli into a per-user location and add it to PATH.

.DESCRIPTION
    Ships inside the Periphery.Cli release zip alongside the self-contained
    Periphery.Cli.exe binary. Run this from the extracted folder; it copies
    the binary into %LOCALAPPDATA%\Programs\Periphery (renamed to
    periphery.exe so the command matches the dotnet-tool ToolCommandName)
    and adds that folder to the per-user PATH.

    Idempotent. Re-running on a machine that already has Periphery installed
    performs an in-place upgrade. The PATH entry is added once and reused.

    No admin rights required — everything is per-user. The PATH change is
    picked up by new processes only; close and reopen any open terminals.

.PARAMETER Source
    Full path to the source Periphery.Cli.exe to install. Defaults to
    Periphery.Cli.exe sitting next to this script.

.PARAMETER InstallPath
    Folder to install into. Defaults to %LOCALAPPDATA%\Programs\Periphery.

.PARAMETER Force
    If periphery.exe is currently running, kill it before replacing the
    binary. Without -Force, the installer aborts with a clear message.

.PARAMETER Uninstall
    Remove the installed binary and the PATH entry. No-op if the
    binary isn't found.

.PARAMETER Quiet
    Suppress informational output. Errors still write to stderr.

.EXAMPLE
    .\Install-Periphery.ps1
    Fresh install, or in-place upgrade if already present.

.EXAMPLE
    .\Install-Periphery.ps1 -Force
    Same, but kill any running periphery.exe first.

.EXAMPLE
    .\Install-Periphery.ps1 -Uninstall
    Remove Periphery from this user profile.
#>

[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [string] $Source,

    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Uninstall')]
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\Periphery'),

    [Parameter(ParameterSetName = 'Install')]
    [switch] $Force,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch] $Uninstall,

    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'

# Use periphery.exe (not Periphery.Cli.exe) at the install site so the
# command users type matches <ToolCommandName>periphery</ToolCommandName>
# in Periphery.Cli.csproj. Stays consistent with `dotnet tool install -g`.
$InstalledExe = Join-Path $InstallPath 'periphery.exe'

function Write-Info {
    param([string] $Message)
    if (-not $Quiet) { Write-Host $Message }
}

function Test-PathHasEntry {
    param([string] $UserPath, [string] $Entry)
    if (-not $UserPath) { return $false }
    $target = [System.IO.Path]::GetFullPath($Entry.TrimEnd('\'))
    foreach ($e in ($UserPath -split ';' | Where-Object { $_ -ne '' })) {
        try {
            $resolved = [System.IO.Path]::GetFullPath($e.TrimEnd('\'))
            if ([string]::Equals($resolved, $target, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        } catch {
            # GetFullPath can throw on PATH entries containing env-var refs
            # like %SOMETHING%; treat those as not-our-target and move on.
        }
    }
    return $false
}

function Add-ToUserPath {
    param([string] $Folder)
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (Test-PathHasEntry -UserPath $user -Entry $Folder) {
        Write-Info "PATH already contains $Folder (no change)."
        return
    }
    $newPath = if ($user) { "$user;$Folder" } else { $Folder }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Info "Added $Folder to user PATH."
    Write-Info "  (Open a new terminal to pick it up.)"
}

function Remove-FromUserPath {
    param([string] $Folder)
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $user) { return }
    $target = [System.IO.Path]::GetFullPath($Folder.TrimEnd('\'))
    $entries = $user -split ';' | Where-Object { $_ -ne '' }
    $kept = @($entries | Where-Object {
        try {
            $resolved = [System.IO.Path]::GetFullPath($_.TrimEnd('\'))
            -not [string]::Equals($resolved, $target, [System.StringComparison]::OrdinalIgnoreCase)
        } catch {
            $true   # keep entries we couldn't resolve
        }
    })
    if ($kept.Count -eq $entries.Count) {
        Write-Info "PATH does not contain $Folder (no change)."
        return
    }
    [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
    Write-Info "Removed $Folder from user PATH."
}

function Stop-RunningInstance {
    param([string] $ExePath)
    # Match by full Path, not just by name — there may be unrelated processes
    # also named "periphery" on the user's machine.
    $procs = @(
        Get-Process -ErrorAction SilentlyContinue
            | Where-Object { $_.Path -and [string]::Equals($_.Path, $ExePath, [System.StringComparison]::OrdinalIgnoreCase) }
    )
    if (-not $procs) { return }
    if (-not $Force -and -not $Uninstall) {
        throw "periphery.exe is currently running (PID $($procs.Id -join ', ')). Close it and retry, or re-run with -Force."
    }
    Write-Info "Stopping running periphery.exe (PID $($procs.Id -join ', '))..."
    $procs | Stop-Process -Force
    # Wait for the OS to release the file handle. Without this, Copy-Item
    # below can race the kernel and fail with "file is in use."
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        try {
            $stream = [System.IO.File]::Open($ExePath, 'Open', 'ReadWrite', 'None')
            $stream.Close()
            return
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }
    throw "periphery.exe stopped but its file handle is still held after 5s. Try again, or reboot."
}

# ── Uninstall path ──────────────────────────────────────────────────────

if ($Uninstall) {
    Write-Info "Uninstalling Periphery from $InstallPath ..."
    if (Test-Path $InstalledExe) {
        Stop-RunningInstance -ExePath $InstalledExe
        Remove-Item -Path $InstalledExe -Force
        Write-Info "Removed $InstalledExe."
    } else {
        Write-Info "No installation found at $InstallPath."
    }
    if (Test-Path $InstallPath) {
        if (-not (Get-ChildItem -Path $InstallPath -Force)) {
            Remove-Item -Path $InstallPath -Force
            Write-Info "Removed empty folder $InstallPath."
        }
    }
    Remove-FromUserPath -Folder $InstallPath
    Write-Info "Done."
    exit 0
}

# ── Install / upgrade path ──────────────────────────────────────────────

if (-not $Source) {
    $Source = Join-Path $PSScriptRoot 'Periphery.Cli.exe'
}
if (-not (Test-Path $Source)) {
    throw "Source binary not found: $Source. Pass -Source <path>, or place Periphery.Cli.exe next to this script."
}

if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

# Capture old version (if any) for the upgrade-vs-fresh-install message.
$oldVersion = $null
if (Test-Path $InstalledExe) {
    $oldVersion = (Get-Item $InstalledExe).VersionInfo.ProductVersion
    Stop-RunningInstance -ExePath $InstalledExe
}

Copy-Item -Path $Source -Destination $InstalledExe -Force
$newVersion = (Get-Item $InstalledExe).VersionInfo.ProductVersion

if ($oldVersion) {
    if ([string]::Equals($oldVersion, $newVersion, [System.StringComparison]::Ordinal)) {
        Write-Info "Reinstalled $InstalledExe (version unchanged: $newVersion)."
    } else {
        Write-Info "Upgraded $InstalledExe ($oldVersion -> $newVersion)."
    }
} else {
    Write-Info "Installed $InstalledExe (version $newVersion)."
}

Add-ToUserPath -Folder $InstallPath

Write-Info ""
Write-Info "Periphery is installed."
Write-Info "  Location: $InstalledExe"
Write-Info "  Version:  $newVersion"
Write-Info ""
Write-Info "Open a new terminal and run:  periphery devices list"
exit 0

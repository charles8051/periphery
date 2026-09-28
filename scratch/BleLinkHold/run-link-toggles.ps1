# Copyright 2026 Charles Lee
# SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

<#
.SYNOPSIS
Toggles a bonded BLE bench peripheral's link while the Windows probes listen.

.DESCRIPTION
Starts BluetoothHciEventProbe, BleOsProbe and BleLinkHold together, then drives the DK's shell:
start advertising, wait for the peripheral to log the connection, then drop the link, and repeat.
Every step waits for the peripheral's own `bench:` log line. Each tool's output goes to
<OutDir>\<timestamp>\, with the DK's lines stamped on the same clock as the tools' start offsets.

Build the three projects first. The DK must run a bench image and be paired with this PC.

.PARAMETER Advertise
The arguments to `bench adv start`: `identity` (default), or `rpa 1` for identity 1 on a rotating
resolvable private address under the privacy image.

.PARAMETER Device
The paired peripheral BleLinkHold holds a session with: its address as its BTHLE\DEV_ node carries
it, or a name substring. Default "Periphery", which needs exactly one paired match.

.PARAMETER Drop
disconnect: the peripheral ends the link with `bt disconnect`.
reset: the DK is reset through its J-Link, which ends the link without a disconnect, as leaving
range does.
#>
param(
    [Parameter(Mandatory)][string]$Port,
    [Parameter(Mandatory)][string]$Serial,
    [ValidateSet('disconnect', 'reset')][string]$Drop = 'disconnect',
    [string]$Advertise = 'identity',
    [string]$Device = 'Periphery',
    [int]$Cycles = 3,
    [int]$ProbeSeconds = 120,
    [string]$OutDir = 'C:\blebench\runs'
)

$ErrorActionPreference = 'Stop'
$scratch = Split-Path $PSScriptRoot -Parent
$run = Join-Path $OutDir (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Force $run | Out-Null

function Exe($project, $tfm) { Join-Path $scratch "$project\bin\Debug\$tfm\$project.exe" }
$tools = @(
    @{ Name = 'hci';  Exe = (Exe 'BluetoothHciEventProbe' 'net10.0-windows');  Args = "$ProbeSeconds" }
    @{ Name = 'aep';  Exe = (Exe 'BleOsProbe' 'net10.0-windows10.0.19041.0');  Args = "$ProbeSeconds" }
    @{ Name = 'hold'; Exe = (Exe 'BleLinkHold' 'net10.0-windows10.0.19041.0'); Args = "$Device $($ProbeSeconds - 10)" }
)

$origin = Get-Date
$procs = foreach ($t in $tools) {
    $p = Start-Process -FilePath $t.Exe -ArgumentList $t.Args -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $run "$($t.Name).txt") -RedirectStandardError (Join-Path $run "$($t.Name).err.txt")
    '{0,-5} starts at +{1:F3}s' -f $t.Name, ($p.StartTime - $origin).TotalSeconds | Add-Content (Join-Path $run 'offsets.txt')
    $p
}

$sp = [System.IO.Ports.SerialPort]::new($Port, 115200, 'None', 8, 'One')
$sp.Open()
$dk = Join-Path $run 'dk.txt'
$buffer = ''

function Stamp { '{0,8:F3}s' -f ((Get-Date) - $origin).TotalSeconds }
function Pump {
    $script:buffer += $sp.ReadExisting()
    while (($i = $script:buffer.IndexOf("`n")) -ge 0) {
        $line = ($script:buffer.Substring(0, $i) -replace "`e\[[0-9;]*[A-Za-z]", '').Trim()
        $script:buffer = $script:buffer.Substring($i + 1)
        if ($line) { "$(Stamp)  dk         $line" | Add-Content $dk; $line }
    }
}
function Send($cmd) { "$(Stamp)  send       $cmd" | Add-Content $dk; $sp.Write("$cmd`r") }
function WaitFor($pattern, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($l in Pump) { if ($l -match $pattern) { return $true } }
        Start-Sleep -Milliseconds 20
    }
    "$(Stamp)  timeout    waiting for $pattern" | Add-Content $dk
    return $false
}
function Dwell($seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { [void](Pump); Start-Sleep -Milliseconds 20 }
}

try {
    # Let the probes finish their startup snapshots before the first transition.
    Dwell 8
    for ($c = 1; $c -le $Cycles; $c++) {
        Send "bench adv start $Advertise"
        if (-not (WaitFor 'bench: connected' 40)) { break }
        Dwell 8
        if ($Drop -eq 'disconnect') {
            Send 'bt disconnect'
            if (-not (WaitFor 'bench: disconnected' 10)) { break }
        } else {
            "$(Stamp)  reset      nrfutil device reset" | Add-Content $dk
            & nrfutil device reset --serial-number $Serial | Out-Null
            if (-not (WaitFor 'bench: ready' 10)) { break }
            Send 'bt init'
            if (-not (WaitFor 'Settings Loaded' 10)) { break }
        }
        Dwell 10
    }
    Send 'bench adv stop'
    Dwell 2
}
finally {
    $sp.Close()
}

$procs | Wait-Process -Timeout ($ProbeSeconds + 60) -ErrorAction SilentlyContinue
$run

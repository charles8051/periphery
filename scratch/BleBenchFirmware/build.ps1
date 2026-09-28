# Copyright 2026 Charles Lee
# SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

<#
.SYNOPSIS
Builds the BLE bench firmware images described in docs/patterns/ble-bench-testing.md.

.DESCRIPTION
Runs west inside the nRF Connect SDK toolchain and copies each image to <OutDir>\images.
The sources are copied to <OutDir>\app first. west computes the source path relative to the SDK
workspace, which fails when the two are on different drives, and a Zephyr build tree nested in a
deep checkout overruns the Windows path limit.
#>
param(
    [string]$NcsVersion = 'v3.4.1',
    [string]$NcsRoot = 'C:\ncs',
    [string]$OutDir = 'C:\blebench'
)

$ErrorActionPreference = 'Stop'

$nrfutil = (Get-Command nrfutil -ErrorAction Stop).Source
$workspace = Join-Path $NcsRoot $NcsVersion
$staged = Join-Path $OutDir 'app'
Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staged | Out-Null
Copy-Item (Join-Path $PSScriptRoot '*') $staged -Recurse -Exclude 'build.ps1'
$app = $staged -replace '\\', '/'
$hrSample = (Join-Path $workspace 'zephyr/samples/bluetooth/peripheral_hr') -replace '\\', '/'
$imagesDir = Join-Path $OutDir 'images'
New-Item -ItemType Directory -Force $imagesDir | Out-Null

$images = @(
    @{ Name = 'nrf52833dk-bench';         Board = 'nrf52833dk/nrf52833'; Source = $app;      Extra = '' }
    @{ Name = 'nrf52833dk-bench-privacy'; Board = 'nrf52833dk/nrf52833'; Source = $app;      Extra = "$app/privacy.conf" }
    @{ Name = 'nrf52833dk-peripheral-hr'; Board = 'nrf52833dk/nrf52833'; Source = $hrSample; Extra = "$app/peripheral_hr.conf" }
    @{ Name = 'thingy52-peripheral-hr';   Board = 'thingy52/nrf52832';   Source = $hrSample; Extra = "$app/peripheral_hr.conf" }
)

foreach ($image in $images) {
    $buildDir = (Join-Path $OutDir "build/$($image.Name)") -replace '\\', '/'
    $westArgs = @('west', 'build', '--pristine', 'always', '--board', $image.Board, '--build-dir', $buildDir, $image.Source)
    if ($image.Extra) {
        $westArgs += @('--', "-DEXTRA_CONF_FILE=$($image.Extra)")
    }

    Write-Host "== $($image.Name)"
    & $nrfutil sdk-manager toolchain launch --ncs-version $NcsVersion --chdir $workspace -- @westArgs
    if ($LASTEXITCODE -ne 0) {
        throw "west build failed for $($image.Name)"
    }

    # Sysbuild puts the application under a domain directory named in domains.yaml.
    $domain = (Select-String -Path "$buildDir/domains.yaml" -Pattern '^default:\s*(\S+)').Matches[0].Groups[1].Value
    $hex = @("$buildDir/merged.hex", "$buildDir/$domain/zephyr/zephyr.hex") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hex) {
        throw "no hex produced for $($image.Name)"
    }
    Copy-Item $hex (Join-Path $imagesDir "$($image.Name).hex") -Force
}

$sniffer = Get-ChildItem (Join-Path $HOME '.nrfutil/share/nrfutil-ble-sniffer/firmware') `
    -Filter 'sniffer_nrf52833dk_*.hex' -ErrorAction SilentlyContinue
if (-not $sniffer) {
    throw "no nRF Sniffer hex for the nRF52833 DK; run 'nrfutil install ble-sniffer'"
}
$sniffer | Copy-Item -Destination $imagesDir -Force

Get-ChildItem $imagesDir | Select-Object Name, Length

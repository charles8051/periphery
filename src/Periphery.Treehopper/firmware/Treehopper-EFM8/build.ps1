<#
  Treehopper EFM8UB1 firmware build using the Keil C51 toolchain embedded in
  Simplicity Studio 5 - no Simplicity Studio / Eclipse needed. Drives the
  compiler / assembler / linker / hex-converter directly.

  Run from a real Windows console:  pwsh -File build.ps1
  (Do NOT pipe the Keil tools through a Git Bash / MSYS pseudo-console - C51.exe
   heap-corrupts and crashes there. A native pwsh/cmd console is required.)

  Build recipe mirrors the Simplicity Studio managed build extracted from
  Firmware-EFM8/.cproject (Release config):
    Part   : EFM8UB10F16G (EFM8UB1, 16 KB flash)
    Model  : SMALL (C51 default), code size ROM(LARGE)
    C51    : OPTIMIZE(9,SIZE)   (see $CFLAGS below - this header used to say SPEED)
    Link   : LX51 (extended linker), AX51 startup, OHX51 -> Intel HEX
#>
[CmdletBinding()]
param(
    [string]$ToolchainRoot = "C:\SiliconLabs\SimplicityStudio\v5\developer\toolchains\keil_8051\9.60",
    [string]$SdkRoot       = "C:\SiliconLabs\SimplicityStudio\v5\developer\sdks\8051\v4.3.1"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

$bin = Join-Path $ToolchainRoot "BIN"
if (-not (Test-Path (Join-Path $bin "C51.exe"))) { throw "Keil C51 not found under $bin" }
if (-not (Test-Path (Join-Path $SdkRoot "Device\efm8ub1\inc"))) { throw "8051 SDK not found under $SdkRoot" }

$env:PATH    = "$bin;$env:PATH"
$env:C51INC  = (Join-Path $ToolchainRoot "INC")
$env:C51LIB  = (Join-Path $ToolchainRoot "LIB")

$out = Join-Path $root "build"
$obj = Join-Path $out "obj"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $obj -Force | Out-Null

# Compiler include path: project-local first, then SDK (matches .cproject order).
$incDirs = @(
    "lib\efm8_assert",
    "$SdkRoot\Device\shared\si8051Base",
    "lib\efm8ub1\peripheralDrivers\inc",
    "inc\config",
    "inc",
    "lib\efm8_usb\inc",
    "$SdkRoot\Device\efm8ub1\inc",
    "$SdkRoot\Lib\efm8_usb\inc",
    "$SdkRoot\Lib\efm8_usbc\lib_kernel\inc",
    "$SdkRoot\Lib\efm8_usbc\lib_usbc_pd\inc",
    "$SdkRoot\Lib\efm8_assert",
    "$SdkRoot\Device\EFM8UB1\peripheral_driver\inc"
)
$INCDIR = "INCDIR(" + ($incDirs -join ";") + ")"
# NOPRINT: don't emit a per-source .lst listing next to each source (the Keil
# default). The LX51 map (build/link.lst) is the only listing we keep.
$CFLAGS = @("OPTIMIZE(9,SIZE)", "ROM(LARGE)", "DEBUG", "OBJECTEXTEND", "NOPRINT")

function Invoke-Tool([string]$exe, [string[]]$toolArgs, [string]$what, [int]$maxOkExit = 0) {
    & (Join-Path $bin $exe) @toolArgs 2>&1 | ForEach-Object { $_ } | Out-String | Write-Verbose
    $rc = $LASTEXITCODE
    if ($rc -gt $maxOkExit) { throw "$what failed (exit $rc): $exe $($toolArgs -join ' ')" }
    return $rc
}

# 1) Startup (AX51 extended assembler).
Write-Host "[ASM] src\SILABS_STARTUP.A51"
$asmInc = "INCDIR(inc\config;$SdkRoot\Device\shared\si8051Base)"
& (Join-Path $bin "AX51.exe") "src\SILABS_STARTUP.A51" $asmInc "OBJECT($obj\SILABS_STARTUP.OBJ)" "NOPRINT" | Out-Null
if ($LASTEXITCODE -gt 1) { throw "AX51 failed (exit $LASTEXITCODE)" }

# 2) Compile every C source under src\ + the selected lib dirs.
$srcDirs = @("src", "lib\efm8_assert", "lib\efm8_usb\src", "lib\efm8ub1\peripheralDrivers\src")
$cFiles  = foreach ($d in $srcDirs) { Get-ChildItem (Join-Path $root $d) -Filter *.c -File }
foreach ($c in $cFiles) {
    $rel = Resolve-Path -Relative $c.FullName
    Write-Host "[CC ] $rel"
    $o = Join-Path $obj ($c.BaseName + ".OBJ")
    # Relative path, not $c.FullName: C51 pools __FILE__ (via assert.h's SLAB_ASSERT) once per
    # module, so an absolute path makes flash size depend on checkout location - a source at
    # a deeply nested checkout path spends ~94 more bytes on that string than
    # the same file relative, which is the entire gap BUILD.md's headroom tables were chasing.
    & (Join-Path $bin "C51.exe") $rel @CFLAGS $INCDIR "OBJECT($o)" | Out-Null
    if ($LASTEXITCODE -gt 1) { throw "C51 failed on $rel (exit $LASTEXITCODE)" }
    if (-not (Test-Path $o)) { throw "C51 produced no object for $rel" }
}

# 3) Link (LX51 extended linker). Startup first, then the rest.
$objs = Get-ChildItem $obj -Filter *.OBJ | Sort-Object { if ($_.Name -eq "SILABS_STARTUP.OBJ") { 0 } else { 1 } }, Name
$omf  = Join-Path $out "Treehopper.omf"
$resp = Join-Path $out "lx51.inp"
(($objs.FullName -join ", ") + " TO $omf") | Set-Content -Encoding Ascii $resp
Write-Host "[LNK] LX51 -> build\Treehopper.omf"
$linkLog = & (Join-Path $bin "LX51.exe") "@$resp" 2>&1 | Out-String
Set-Content -Encoding Ascii (Join-Path $out "link.lst") $linkLog
# LX51 returns exit 1 for warnings only (e.g. the benign L15/L57 uncalled-function
# notes the SiLabs peripheral libs always emit); >=2 is a real error.
if ($LASTEXITCODE -gt 1) { Write-Host $linkLog; throw "LX51 failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $omf)) { Write-Host $linkLog; throw "LX51 produced no OMF" }

# 4) Intel HEX (OHX51).
$hex = Join-Path $out "Treehopper.hex"
Write-Host "[HEX] OHX51 -> build\Treehopper.hex"
& (Join-Path $bin "Ohx51.exe") $omf "HEXFILE($hex)" | Out-Null
if ($LASTEXITCODE -gt 1) { throw "OHX51 failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $hex)) { throw "OHX51 produced no HEX" }

Write-Host ""
Write-Host "[OK ] Build succeeded."
Write-Host ("      HEX : build\Treehopper.hex  ({0} bytes)" -f (Get-Item $hex).Length)
Write-Host  "      OMF : build\Treehopper.omf"
Write-Host  "--- linker size summary ---"
($linkLog -split "`r?`n" | Select-String -Pattern "PROGRAM SIZE|CODE SIZE|DATA SIZE|Program Size") | ForEach-Object { "      $_" }

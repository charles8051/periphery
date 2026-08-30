#!/usr/bin/env bash
# Adaptive-chunk verification sweep: with the bounded mask + adaptive chunk, every
# SPI clock should be CLEAN under USB load -- the 4-5 MHz danger band (was wedging)
# AND slow clocks (where a fixed chunk would mask too long and starve USB). Each
# cell starts from a C2 hard-reset for a known-good state. Danger-band bypass on so
# in-band clocks actually clock in-band.
set -u
# Resolve the script directory ONCE, before any cd: ${BASH_SOURCE[0]} can be a
# relative path, and it stops resolving the moment the working directory moves.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."
HARNESS="examples/Periphery.Examples.TreehopperSpiStress/Periphery.Examples.TreehopperSpiStress.csproj"
JLINK="/c/Program Files/SEGGER/JLink_V950/JLink.exe"
RESET="$SCRIPT_DIR/jlink/reset.jlink"
export TREEHOPPER_SPI_DANGER_BAND=1
DUR=12

reset_board() {
  "$JLINK" -device EFM8UB10F16G -if C2 -speed 1000 -autoconnect 1 -ExitOnError 1 \
           -CommandFile "$RESET" >/dev/null 2>&1
  sleep 4
}

run() {
  local clock=$1
  for attempt in 1 2 3; do
    reset_board
    out=$(dotnet run --project "$HARNESS" -c Release --no-build -- \
          --clock-mhz "$clock" --burst 200 --cs-pin 5 \
          --duration "$DUR" --xfer-timeout-ms 2000 --log-file ./freqcell.log 2>&1)
    echo "$out" | grep -q "FATAL" && continue
    if echo "$out" | grep -q "WEDGED"; then
      n=$(echo "$out" | grep -oE 'after +[0-9]+ SPI transfers' | grep -oE '[0-9]+' | head -1)
      printf '  %-8s MHz -> WEDGED after %s transfers\n' "$clock" "$n"
    else
      n=$(echo "$out" | grep -oE 'spi=[0-9]+' | tail -1 | grep -oE '[0-9]+')
      printf '  %-8s MHz -> CLEAN  (~%s transfers / %ss)\n' "$clock" "${n:-?}" "$DUR"
    fi
    return
  done
  printf '  %-8s MHz -> OPEN-FAILED\n' "$clock"
}

echo "=== Frequency sweep @ 200B burst, adaptive-chunk bounded mask ==="
for c in 0.094 0.5 1 2 4 5 6 8 12; do run "$c"; done
echo "=== done ==="

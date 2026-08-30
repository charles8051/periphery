#!/usr/bin/env bash
# 4 MHz SPI-FIFO lockup sweep: does failure track transfer DURATION (burst size)
# or clock VALUE? Each cell starts from a C2 hard-reset (guaranteed clean state,
# since a wedge leaves the board stuck in USB suspend), then runs the stress
# harness (SPI flood + USB noise) for a short window. Danger-band bypass on.
set -u
# Resolve the script directory ONCE, before any cd: ${BASH_SOURCE[0]} can be a
# relative path, and it stops resolving the moment the working directory moves.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."
HARNESS="examples/Periphery.Examples.TreehopperSpiStress/Periphery.Examples.TreehopperSpiStress.csproj"
JLINK="/c/Program Files/SEGGER/JLink_V950/JLink.exe"
RESET="$SCRIPT_DIR/jlink/reset.jlink"
export TREEHOPPER_SPI_DANGER_BAND=1
DUR=15
LOG=./sweep-cell.log

reset_board() {
  "$JLINK" -device EFM8UB10F16G -if C2 -speed 1000 -autoconnect 1 -ExitOnError 1 \
           -CommandFile "$RESET" >/dev/null 2>&1
  sleep 4   # let the firmware restart + USB re-enumerate
}

run() {
  local clock=$1 burst=$2
  for attempt in 1 2 3; do
    reset_board
    out=$(dotnet run --project "$HARNESS" -c Release --no-build -- \
          --clock-mhz "$clock" --burst "$burst" --cs-pin 5 \
          --duration "$DUR" --xfer-timeout-ms 2000 --log-file "$LOG" 2>&1)
    echo "$out" | grep -q "FATAL" && continue   # open failed; reset + retry
    if echo "$out" | grep -q "WEDGED"; then
      n=$(echo "$out" | grep -oE 'after +[0-9]+ SPI transfers' | grep -oE '[0-9]+' | head -1)
      printf 'clock=%-6s burst=%-4s -> WEDGED after %s transfers\n' "${clock}MHz" "${burst}B" "$n"
    else
      n=$(echo "$out" | grep -oE 'spi=[0-9]+' | tail -1 | grep -oE '[0-9]+')
      printf 'clock=%-6s burst=%-4s -> CLEAN  (~%s transfers / %ss)\n' "${clock}MHz" "${burst}B" "${n:-?}" "$DUR"
    fi
    return
  done
  printf 'clock=%-6s burst=%-4s -> OPEN-FAILED\n' "${clock}MHz" "${burst}B"
}

echo "=== Burst-size sweep @ 4 MHz (duration vs clock-value test) ==="
for b in 8 32 64 128 200; do run 4 "$b"; done
echo ""
echo "=== Clock sweep @ burst=200 ==="
for c in 1 2 5 5.9; do run "$c" 200; done
echo ""
echo "(ref: 6.25MHz/200B = CLEAN x5 soaks; 4MHz/200B no-noise = CLEAN)"
echo "=== sweep done ==="

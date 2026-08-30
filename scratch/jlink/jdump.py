#!/usr/bin/env python3
"""Run a J-Link C2 dump against the EFM8 and decode the halted PC to a function.

Drives JLink.exe head-less with a CommandFile, captures stdout, extracts the PC
(and SFRPAGE), and resolves the PC against the Keil LX51 map's CODE segment table.
The point: turn a wedged-board halt into "PC is inside SPI0_pollTransfer" in one
command, no psmux / send-keys.

    python jdump.py [wedge.jlink]
"""
import re, subprocess, sys, os

HERE = os.path.dirname(os.path.abspath(__file__))
JLINK = r"C:\Program Files\SEGGER\JLink_V950\JLink.exe"
MAP = os.path.join(HERE, "..", "..", "src", "Periphery.Treehopper", "firmware",
                   "Treehopper-EFM8", "build", "TREEHOPPER.MAP")
CMDFILE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "wedge.jlink")


def load_segments(path):
    segs = []
    for ln in open(path, errors="ignore"):
        m = re.match(r"\s*([0-9A-F]+)H\s+([0-9A-F]+)H\s+[0-9A-F]+H\s+\w+\s+\w+\s+CODE\s+(\S+)", ln)
        if m:
            segs.append((int(m.group(1), 16), int(m.group(2), 16), m.group(3)))
    segs.sort()
    return segs


def resolve(segs, pc):
    for s, e, n in segs:
        if s <= pc <= e:
            return f"{n}  [0x{s:04X}-0x{e:04X}, +0x{pc - s:X}]"
    return "NOT FOUND in any CODE segment"


def main():
    out = subprocess.run(
        [JLINK, "-device", "EFM8UB10F16G", "-if", "C2", "-speed", "1000",
         "-autoconnect", "1", "-ExitOnError", "1", "-CommandFile", os.path.abspath(CMDFILE)],
        capture_output=True, text=True, timeout=60).stdout
    print(out)
    print("=" * 60)
    pcm = re.search(r"PC = ([0-9A-Fa-f]+)", out)
    spm = re.search(r"000000A7 = ([0-9A-Fa-f]{2})", out)
    if not pcm:
        print("No PC found (connect failed?).")
        return 1
    pc = int(pcm.group(1), 16)
    segs = load_segments(MAP)
    print(f"PC      = 0x{pc:04X}  ->  {resolve(segs, pc)}")
    if spm:
        sp = int(spm.group(1), 16)
        page = {0x00: "page 0 (default)", 0x10: "page 0x10", 0x20: "page 0x20 (SPI/SMBus/etc.)"}.get(sp, f"page 0x{sp:02X}")
        print(f"SFRPAGE = 0x{sp:02X}  ({page})")
    # Is it a suspected hang site?
    if 0x1C73 <= pc <= 0x1D00:
        print(">>> PC is inside SPI0_pollTransfer (0x1C73-0x1D00) -- SPI poll busy-wait hang.")
    elif 0x1445 <= pc <= 0x14F8:
        print(">>> PC is inside SPI_Transaction (0x1445-0x14F8).")
    elif 0x097D <= pc <= 0x0A7E:
        print(">>> PC is inside UART_Transaction (0x097D-0x0A7E) -- UART TX `while(!SCON0_TI)` busy-wait hang.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

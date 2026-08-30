# Test assets

## `synthetic.efm8` / `synthetic.hex`

A real SiLabs boot-record file, generated from a synthetic Intel HEX. It serves two
purposes: the parser and chunker are exercised against genuine `hex2boot` output (not
just hand-built frames), and the **matched `.hex` -> `.efm8` pair is the golden fixture
for `Efm8BootRecordGenerator`** — the in-house hex2boot replacement must reproduce
`synthetic.efm8` byte-for-byte from `synthetic.hex` (see
`Efm8BootRecordGeneratorTests.FromIntelHex_Ub1_MatchesRealHex2bootOutput_ByteForByte`).
Regenerate with:

```bash
# 1. Build a 200-byte synthetic Intel HEX (reset vector nonzero -> exercises the
#    hex2boot reset-vector-written-last failsafe).
python - <<'PY'
from intelhex import IntelHex
out = r"tests\Periphery.Efm8Bootloader.Tests\Assets\synthetic.hex"
ih = IntelHex()
for i in range(200):
    ih[i] = (i * 7 + 3) & 0xFF
ih.write_hex_file(out)
PY

# 2. Convert to a boot-record file with the recovered+verified hex2boot
#    (needs: pip install intelhex). Same flags Treehopper uses: -m ub1 -b 0.
cd <the SiLabs EFM8 tools directory>
python hex2boot.py -o "<repo>\tests\Periphery.Efm8Bootloader.Tests\Assets\synthetic.efm8" \
  -m ub1 -b 0 "<repo>\tests\Periphery.Efm8Bootloader.Tests\Assets\synthetic.hex"
```

The resulting `synthetic.efm8` is a 236-byte stream of 6 records:
`Setup(0x31)`, `Erase-with-data(0x32, 128 bytes)`, `Write(0x33)`, `Verify(0x34)`,
the failsafe reset-vector `Write(0x33)`, and `RunApp(0x36)`. The erase record's
133-byte frame spans three 64-byte output reports, so the real file covers the
chunk-boundary path end to end.

Sources (both verified):
- `hex2boot.py` — the converter.
- `efm8load.py` — the reference uploader.

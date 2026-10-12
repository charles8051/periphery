# Pinned CMSIS-Pack inputs

The flash algorithm records in `Periphery.DebugProbe.StLink/Targets/` are generated from the files
here (ADR-0094 D9). Each directory holds one pack release:

- `manifest.json`: the pack, its tag, the SHA-256 of its `.pdsc`, and per algorithm the FLM, its
  SHA-256, its flash range, its RAM window and its notice file.
- The FLM files the manifest lists, copied from the tag.
- The pack's `LICENSES` file, and a notice per algorithm source.

The pack's `.pdsc` is not checked in. The generator needs only each algorithm's range and RAM
window from it, which `extract` copies into the manifest, and the pinned SHA-256 lets anyone check
those values against the tag.

| Directory | Pack | Tag | Algorithms |
|---|---|---|---|
| `STM32G4xx_DFP-2.2.0` | `Open-CMSIS-Pack/STM32G4xx_DFP` | `v2.2.0` | `STM32G43x-4x_128.FLM` |

## Regenerate the records

From the repository root, with the pack's `.pdsc` downloaded from its tag:

```bash
dotnet run --project src/Periphery.DebugProbe.AlgorithmGen -- generate --manifest src/Periphery.DebugProbe.AlgorithmGen/Packs/STM32G4xx_DFP-2.2.0/manifest.json --out-dir src/Periphery.DebugProbe.StLink/Targets --pdsc <downloaded .pdsc>
```

`generate` checks the manifest before it writes anything:

- It refuses a `.pdsc` whose SHA-256 is not the one the manifest pins, and any algorithm whose
  range or RAM window differs from the pack's entry. It does not run without `--pdsc` unless
  `--skip-pdsc-check` replaces it, which prints a warning. Use that only to regenerate from a
  manifest you are not changing.
- It refuses an FLM whose SHA-256 differs from the manifest's.
- It refuses a RAM window that is empty, not word-aligned, outside the SRAM region
  `0x20000000..0x3FFFFFFF`, or overlapping the flash it programs.

`AlgorithmGeneratorTests` fails if a checked-in record differs from what `generate` writes. At flash
time the programmer writes the whole RAM span the algorithm uses, reads it back, and refuses to call
the algorithm if any of it is not RAM on the attached part.

## Add an algorithm

1. Confirm the algorithm's source file carries a permissive grant of its own, or that the pack's
   license covers it (ADR-0094 D9). Never add an option-byte algorithm.
2. Download the pack's `.pdsc` and the FLM from the pack's release tag.
3. Read the manifest values from them:

   ```bash
   dotnet run --project src/Periphery.DebugProbe.AlgorithmGen -- extract --pdsc <downloaded .pdsc> --flm <downloaded FLM> --flm-path CMSIS/Flash/<name>.FLM
   ```

4. Add an entry to the pack's `manifest.json` with those values, a C# field name, and a notice file
   carrying the algorithm source's license notice. A new pack also gets its own directory with
   `LICENSES` and the `.pdsc`'s SHA-256.
5. Check in the FLM, the notice and the manifest. Do not check in the `.pdsc`.
6. Run `generate` with `--pdsc` pointing at the downloaded `.pdsc`. Add the part's device ID to
   `Stm32Catalog` only after its hardware gate passes.

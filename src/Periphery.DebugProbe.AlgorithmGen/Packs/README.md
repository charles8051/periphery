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

From the repository root:

```bash
dotnet run --project src/Periphery.DebugProbe.AlgorithmGen -- generate --manifest src/Periphery.DebugProbe.AlgorithmGen/Packs/STM32G4xx_DFP-2.2.0/manifest.json --out-dir src/Periphery.DebugProbe.StLink/Targets
```

The generator refuses an FLM whose SHA-256 differs from the manifest's. `AlgorithmGeneratorTests`
fails if a checked-in record differs from what this command writes.

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
6. Run `generate`, and add the part's device ID to `Stm32Catalog` only after its hardware gate
   passes.

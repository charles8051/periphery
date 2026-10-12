# Pinned CMSIS-Pack inputs

The flash algorithm records in `Periphery.DebugProbe.StLink/Targets/` are generated from the files
here (ADR-0094 D9). Each directory holds one pack release, copied from its GitHub tag: the FLM
files used, the pack description, the pack's `LICENSES`, and a notice per algorithm source.

| Directory | Pack | Tag | Algorithm | SHA-256 of the FLM |
|---|---|---|---|---|
| `STM32G4xx_DFP-2.2.0` | `Open-CMSIS-Pack/STM32G4xx_DFP` | `v2.2.0` | `CMSIS/Flash/STM32G43x-4x_128.FLM` | `27CB62458B4F45E23D91C64B901464AB1688ABD8DD4F5971054AED83F3B0970A` |

Regenerate the STM32G43x record from the repository root:

```bash
dotnet run --project src/Periphery.DebugProbe.AlgorithmGen -- --pack-dir src/Periphery.DebugProbe.AlgorithmGen/Packs/STM32G4xx_DFP-2.2.0 --pack Open-CMSIS-Pack/STM32G4xx_DFP --version 2.2.0 --flm CMSIS/Flash/STM32G43x-4x_128.FLM --pdsc Keil.STM32G4xx_DFP.pdsc --notice STM32G4xx_Flash.NOTICE.txt --field Stm32G43x4x128 --out src/Periphery.DebugProbe.StLink/Targets/FlashAlgorithms.Stm32G43x4x128.g.cs
```

`AlgorithmGeneratorTests` fails if the checked-in record differs from what this command writes.

Before adding an algorithm, confirm its source file carries a permissive grant of its own, or that
the pack's license covers it (ADR-0094 D9). Never add an option-byte algorithm.

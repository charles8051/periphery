# Third-party notices: Periphery.DebugProbe.StLink

ADR-0094 D8 and D9 require these notices to travel with the code they cover.

## probe-rs

This package and `Periphery.DebugProbe.AlgorithmGen` adapt facts and conventions from
[probe-rs](https://github.com/probe-rs/probe-rs). No probe-rs source file is copied. What was
adapted, and where it lives:

| Adapted from | Into |
|---|---|
| `probe-rs/src/probe/stlink/constants.rs`, `usb_interface.rs`, `mod.rs`: command codes, reply layouts, status codes, the PID-to-endpoint table, firmware floors | `StLink/StLinkProtocol.cs` |
| `probe-rs/src/probe/stlink/mod.rs`: the open sequence and the read-then-status pattern | `StLink/StLinkSession.cs` |
| `probe-rs/src/flashing/flasher.rs` and `flash_algorithm.rs`: the flash algorithm call convention, breakpoint header and stack size | `Flash/AlgorithmCall.cs`, `Flash/RamLayout.cs` |
| `target-gen/src/algorithm_binary.rs`: the FLM section layout | `Periphery.DebugProbe.AlgorithmGen/FlmReader.cs` |

Changes: re-expressed as C#. probe-rs is licensed MIT OR Apache-2.0; this package uses it under
the MIT license:

    MIT License

    Copyright (c) 2019 probe-rs

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.

## Open-CMSIS-Pack STM32G4xx_DFP

`Targets/FlashAlgorithms.Stm32G43x4x128.g.cs` holds the code and data of
`CMSIS/Flash/STM32G43x-4x_128.FLM` from
[Open-CMSIS-Pack/STM32G4xx_DFP](https://github.com/Open-CMSIS-Pack/STM32G4xx_DFP) v2.2.0, copied
unaltered. Arm built that algorithm from `FlashPrg.c` and `FlashDev.c`, whose notice the
generated file carries in full: Copyright (c) 2014 - 2021 ARM Ltd., under the zlib license. The
pack is licensed Apache-2.0 for CMSIS add-ons and BSD-3-Clause for ST's HAL; its `LICENSES` file,
with both texts, is at `src/Periphery.DebugProbe.AlgorithmGen/Packs/STM32G4xx_DFP-2.2.0/LICENSES`.

The register addresses in `Targets/Stm32Target.cs` are taken from ST's `STM32G431.svd` in the
same pack.

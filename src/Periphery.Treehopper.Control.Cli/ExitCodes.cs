// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control.Cli;

/// <summary>Process exit codes so fleet automation over SSH can branch on the outcome.</summary>
internal static class ExitCodes
{
    public const int Success = 0;       // all good / clean dry run
    public const int FlashFailed = 1;   // at least one board failed to flash
    public const int Usage = 2;         // bad command line / refused operation
    public const int NoImage = 3;       // no firmware image available
    public const int NoBoards = 4;      // no matching board found
}

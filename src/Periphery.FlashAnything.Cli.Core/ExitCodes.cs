// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.FlashAnything.Cli;

/// <summary>
/// Process exit codes (stable for fleet/SSH automation). Public so a front-end-contributed
/// <see cref="CliVerb"/> returns the same codes as the built-in verbs — one contract per tool.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;     // ok, or a clean dry run
    public const int Usage = 2;       // bad command line / refused
    public const int NoImage = 3;     // firmware file missing
    public const int NoTarget = 4;    // no matching flashable target / board

    /// <summary>
    /// The work was attempted and did not succeed for at least one target: a flash that failed, a
    /// rename that did not land, a reboot with no effect. Not <c>FlashFailed</c> — contributed verbs
    /// (<see cref="CliVerb"/>) return it too, and naming it for one verb misdescribes the rest.
    /// </summary>
    public const int OperationFailed = 1;
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader;

/// <summary>
/// The outcome of <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/>.
/// </summary>
/// <param name="FlashResult">Whatever the last flash attempt's callback returned.</param>
/// <param name="ApplicationReturned">
/// Whether the application was confirmed back after the <b>last</b> round-trip performed (the
/// verify round's app-wait when <see cref="Verified"/> could be determined, otherwise the flash
/// round's).
/// </param>
/// <param name="Verified">
/// <c>true</c> only when an independent, later bootloader-session verify confirmed the flashed
/// content matches. <c>false</c> covers every other case — the flash itself failed, the
/// application never confirmed returning (so a second entry could not safely be attempted), or
/// every verify attempt reported a mismatch.
/// </param>
/// <param name="Attempts">How many flash-then-verify cycles this took, including the last one.</param>
public sealed record VerifiedFlashResult<TResult>(
    TResult FlashResult, bool ApplicationReturned, bool Verified, int Attempts);

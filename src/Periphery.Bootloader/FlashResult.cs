// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery.Bootloader;

/// <summary>The outcome of a flash.</summary>
public sealed record FlashResult(bool Success, long BytesWritten, bool Verified, string? Error = null)
{
    /// <summary>A successful flash of <paramref name="bytesWritten"/> bytes.</summary>
    public static FlashResult Ok(long bytesWritten, bool verified) => new(true, bytesWritten, verified);

    /// <summary>A failed flash, with a human-readable <paramref name="error"/>.</summary>
    public static FlashResult Fail(string error) => new(false, 0, false, error);

    /// <summary>
    /// A failed flash caused by <paramref name="exception"/>. The error carries the whole cause
    /// chain (<see cref="ExceptionSummary.Describe"/>), not just the outermost wrapper's message —
    /// the wrapper is usually the generic half ("Treehopper reconcile failed.") and the inner
    /// exception the actionable one.
    /// </summary>
    public static FlashResult Fail(Exception exception) => Fail(ExceptionSummary.Describe(exception));

    /// <summary>A one-line summary for logs and UIs.</summary>
    public string Describe() => Success
        ? $"flashed {BytesWritten} bytes{(Verified ? ", verified" : "")}"
        : $"failed: {Error}";
}

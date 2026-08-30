// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text;

namespace Periphery.Bootloader;

/// <summary>
/// Pure, total formatting of an exception <em>chain</em> into the one line an operator can act on.
/// </summary>
/// <remarks>
/// Periphery's device layers wrap: a <c>TreehopperException("Treehopper reconcile failed.", inner:
/// UsbException("Access denied ..."))</c> carries the actionable cause in its
/// <see cref="Exception.InnerException"/>. Reducing such an exception with
/// <see cref="Exception.Message"/> alone throws that away, which is how a fleet flash reports
/// <c>FAILED - Treehopper reconcile failed.</c> and nothing else — the same text whether the board
/// was busy, access-denied, or absent. Every failure string derived from a caught exception should
/// go through here.
/// </remarks>
public static class ExceptionSummary
{
    /// <summary>How many links of the chain <see cref="Describe"/> renders by default.</summary>
    public const int DefaultMaxDepth = 4;

    /// <summary>
    /// Renders <paramref name="exception"/> and its inner chain as
    /// <c>outer message -&gt; InnerType: inner message -&gt; ...</c>, stopping after
    /// <paramref name="maxDepth"/> links. Links whose message repeats the one before are skipped (a
    /// wrapper that only re-states its cause adds nothing), single-inner
    /// <see cref="AggregateException"/>s are unwrapped (their own message is boilerplate), and an
    /// exception with a blank message falls back to its type name.
    /// </summary>
    /// <param name="exception">The exception to summarize.</param>
    /// <param name="maxDepth">Maximum number of links to render, including the outermost. Must be at least 1.</param>
    public static string Describe(Exception exception, int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        var head = Unwrap(exception)!;
        var text = new StringBuilder(MessageOf(head));
        string previous = MessageOf(head);

        int rendered = 1;
        for (var link = Unwrap(head.InnerException); link is not null && rendered < maxDepth; link = Unwrap(link.InnerException))
        {
            string message = MessageOf(link);
            if (!string.Equals(message, previous, StringComparison.Ordinal))
            {
                text.Append(" -> ").Append(link.GetType().Name).Append(": ").Append(message);
                previous = message;
                rendered++;
            }
        }

        return text.ToString();
    }

    // An AggregateException's own message ("One or more errors occurred. (...)") is boilerplate; when
    // it wraps exactly one fault, that fault is the real link. Several faults keep the aggregate,
    // whose message already enumerates them.
    private static Exception? Unwrap(Exception? exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];
        return exception;
    }

    private static string MessageOf(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}

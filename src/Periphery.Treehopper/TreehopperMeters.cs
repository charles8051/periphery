// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics.Metrics;
using System.Reflection;

namespace Periphery.Treehopper;

/// <summary>
/// The <c>Periphery.Treehopper</c> package's <see cref="Meter"/> and canonical
/// instruments, per the repo logging-and-diagnostics standard (one Meter per package,
/// OpenTelemetry-style instrument names). These sit one layer above the
/// <c>Periphery.Usb</c> transfer metrics: a <em>transaction</em> is one logical board
/// command (a write plus an optional response read, possibly several USB packets),
/// whereas a <c>periphery.usb.transfer_*</c> is a single bulk packet.
/// </summary>
internal static class TreehopperMeters
{
    /// <summary>The single Meter for the <c>Periphery.Treehopper</c> package.</summary>
    internal static readonly Meter Meter = new(
        name: "Periphery.Treehopper",
        version: typeof(TreehopperMeters).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(TreehopperMeters).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");

    /// <summary>Peripheral transactions (SPI / I²C / UART / 1-Wire) that completed successfully.</summary>
    internal static readonly Counter<long> TransactionsTotal = Meter.CreateCounter<long>(
        "periphery.treehopper.transactions_total",
        unit: "{transaction}",
        description: "Treehopper peripheral transactions that completed successfully.");

    /// <summary>Transactions or reconciles that faulted (transport error or timeout).</summary>
    internal static readonly Counter<long> TransactionErrorsTotal = Meter.CreateCounter<long>(
        "periphery.treehopper.transaction_errors_total",
        unit: "{transaction}",
        description: "Treehopper transactions or reconciles that faulted (transport error / timeout).");

    /// <summary>Transaction latency (write + optional response read) — the per-flush cost.</summary>
    internal static readonly Histogram<double> TransactionDuration = Meter.CreateHistogram<double>(
        "periphery.treehopper.transaction_ms",
        unit: "ms",
        description: "Treehopper transaction latency (write plus optional response read).");

    /// <summary>
    /// Boards that latched a response-endpoint desync — a transaction owed a reply and did
    /// not consume one, so the endpoint may hold bytes belonging to a command that has
    /// already given up (#263 item 3).
    /// </summary>
    /// <remarks>
    /// Counts boards, not transactions: the flag latches, so each board contributes at most
    /// one. Non-zero means transactions are timing out or being cancelled mid-reply often
    /// enough to cost connections, since the only recovery is a re-open. Pairs with
    /// <see cref="TransactionErrorsTotal"/> — errors without desyncs are faults that left
    /// nothing stranded; the ratio is how often a fault costs the connection.
    /// </remarks>
    internal static readonly Counter<long> ResponsePipeDesyncsTotal = Meter.CreateCounter<long>(
        "periphery.treehopper.response_pipe_desyncs_total",
        unit: "{board}",
        description: "Boards that latched a peripheral-response-endpoint desync.");

    /// <summary>Pin-state reports decoded from the board and published to subscribers.</summary>
    internal static readonly Counter<long> ReportsTotal = Meter.CreateCounter<long>(
        "periphery.treehopper.reports_total",
        unit: "{report}",
        description: "Pin-state reports decoded from the board and published to subscribers.");
}

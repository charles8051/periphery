// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Periphery.FlashAnything.Cli;

/// <summary>
/// A verb contributed by a front-end, for the commands the shared toolkit cannot know about.
/// </summary>
/// <remarks>
/// <para>
/// A branded flasher is a curated <em>composition</em> of this toolkit (ADR-0063 DEC-006), so the
/// shared CLI owns argv, help, and dispatch. But some devices have identity/maintenance commands
/// that are theirs alone — the Treehopper Flasher's <c>rename</c>, which speaks the Treehopper HID
/// protocol to a board that is *not* in a bootloader. Those cannot live in this composition-agnostic
/// toolkit, and forking the parser to add one would undo DEC-006.
/// </para>
/// <para>
/// So the toolkit only <b>routes</b>: <see cref="Cli.Parse"/> hands a matching first token, and every
/// argument after it, straight to <see cref="RunAsync"/>. The verb owns its own (pure, total) argument
/// parsing, its own output, and its own exit code — conventionally one of <see cref="ExitCodes"/>, so
/// fleet automation sees one exit-code contract across the whole tool. Built-in verbs win a name
/// collision; <c>list</c>, <c>flash</c>, and <c>autoflash</c> cannot be shadowed.
/// </para>
/// <para>
/// <b>Contract for implementors</b> — three things the routing does not do for you:
/// </para>
/// <list type="number">
/// <item>
/// <b>Handle <c>-h</c> / <c>--help</c> yourself.</b> They are not intercepted after the verb token, so
/// a verb that does not recognise them will answer its own help request with "unknown option". They are
/// left to the verb deliberately: a useful verb help carries examples and exit codes that
/// <see cref="Usage"/> and <see cref="Summary"/> cannot.
/// </item>
/// <item>
/// <b>Global flags must follow the verb.</b> <c>tool rename -v x</c> works; <c>tool -v rename x</c>
/// does not — a leading flag routes to the default <c>list</c> command. That is the pre-existing rule
/// for the built-in verbs and is not special-cased here, but the parser recognises the shape and says
/// so rather than reporting the verb as an unknown option.
/// </item>
/// </list>
/// <para>
/// <b>What the seam does own:</b> <c>-v</c> / <c>--verbose</c>. It installs the log sink process-wide
/// before dispatch and <em>removes the flag</em> from your arguments, so a verb never sees it and can
/// never disagree with the run loop about whether verbosity was asked for. Use the
/// <see cref="ILoggerFactory"/> parameter, not a flag of your own.
/// </para>
/// </remarks>
/// <param name="Name">The verb, as typed (e.g. <c>rename</c>).</param>
/// <param name="Usage">
/// The usage line after the tool name (e.g. <c>rename &lt;name&gt; [opts]</c>), spliced into
/// <c>--help</c>.
/// </param>
/// <param name="Summary">The one-line description shown under <paramref name="Usage"/>.</param>
/// <param name="RunAsync">
/// Runs the verb over the arguments that followed it. The <see cref="ILoggerFactory"/> is the
/// <c>--verbose</c> stderr sink (already installed as the Periphery factory), or <c>null</c> when the
/// command line did not ask for one.
/// </param>
public sealed record CliVerb(
    string Name,
    string Usage,
    string Summary,
    Func<string[], ILoggerFactory?, CancellationToken, Task<int>> RunAsync)
{
    /// <summary>
    /// An optional block of extra <c>--help</c> lines for this verb's own options, appended after the
    /// shared OPTIONS section and indented to match it. Empty (the default) contributes nothing.
    /// </summary>
    public string OptionsHelp { get; init; } = "";
}

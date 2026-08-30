// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// The pure core behind <see cref="MonitorLayout.Availability"/>: given what the
/// topology read produced and which session it ran in, decide what an empty
/// result <i>means</i>. No IO, no OS call, no clock — the shell
/// (<c>Windows.CcdLayout</c>) supplies both inputs.
/// </summary>
/// <remarks>
/// Split out so the interesting judgement is exhaustively unit-testable with no
/// display attached and, more to the point, <b>without having to be in session
/// 0 to test the session-0 case</b> — which is exactly the kind of condition
/// that otherwise only ever gets exercised in production (issue #207).
/// </remarks>
internal static class MonitorSessionVisibility
{
    /// <summary>
    /// The Windows services session. Session 0 has no interactive desktop and no
    /// display configuration of its own — this is Session 0 Isolation, in place
    /// since Vista, not a policy that varies by machine. A process there sees an
    /// empty topology no matter what is physically plugged in.
    /// </summary>
    internal const uint ServicesSessionId = 0;

    /// <summary>
    /// Classifies a completed topology read. Total: every input maps to a member.
    /// </summary>
    /// <param name="entryCount">How many entries the read produced.</param>
    /// <param name="sessionId">The session the reading process is in.</param>
    internal static MonitorLayoutAvailability Classify(int entryCount, uint sessionId)
    {
        if (entryCount > 0)
            return MonitorLayoutAvailability.Available;

        // Only session 0 is claimed, deliberately. It is the one case that is
        // decidable from the session id alone: session 0 can NEVER hold display
        // configuration. The tempting generalisation -- "any session that is not
        // the console session is blind" -- is FALSE: an RDP session has its own
        // display configuration and legitimately sees its own monitors. Claiming
        // blindness there would trade a known ambiguity for a wrong answer.
        return sessionId == ServicesSessionId
            ? MonitorLayoutAvailability.NotVisibleFromThisSession
            : MonitorLayoutAvailability.NoActiveDisplays;
    }
}

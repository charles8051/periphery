// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.DebugProbe.StLink;

/// <summary>An ST-Link refused a command, replied with an error status, or replied malformed.</summary>
public sealed class StLinkException : Exception
{
    public StLinkException(string message) : base(message) { }

    public StLinkException(string message, Exception innerException) : base(message, innerException) { }

    internal StLinkException(string message, StLinkStatus status) : base($"{message} (status {status}, 0x{(byte)status:X2}).")
    {
        Status = status;
    }

    /// <summary>The status byte, when the failure came from one.</summary>
    internal StLinkStatus? Status { get; }
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Base exception for all Periphery device enumeration failures.
/// </summary>
public class DeviceEnumerationException : Exception
{
    public DeviceEnumerationException()
    {
    }

    public DeviceEnumerationException(string message)
        : base(message)
    {
    }

    public DeviceEnumerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a platform-specific device provider encounters an error
/// communicating with the operating system's device enumeration APIs.
/// </summary>
public class DeviceProviderException : DeviceEnumerationException
{
    public DeviceProviderException()
    {
    }

    public DeviceProviderException(string message)
        : base(message)
    {
    }

    public DeviceProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

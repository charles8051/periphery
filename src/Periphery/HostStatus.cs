// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;

namespace Periphery;

/// <summary>
/// Discriminated union describing the current observable status of a
/// <see cref="DeviceSessionHost{TSession}"/>.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
public abstract record HostStatus<TSession>
    where TSession : class;

/// <summary>
/// No matching active device is currently available for session creation.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
public sealed record DeviceAbsent<TSession>() : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// A matching device is active and session creation is currently in flight.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
/// <param name="Device">The active device snapshot.</param>
public sealed record SessionStarting<TSession>(DeviceInfo Device) : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// A session is active and ready for use.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
/// <param name="Session">The active session.</param>
/// <param name="Device">The active device snapshot.</param>
public sealed record SessionActive<TSession>(TSession Session, DeviceInfo Device)
    : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// A matching device is active, but session creation most recently failed and
/// reconnect is pending.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
/// <param name="Device">The active device snapshot.</param>
/// <param name="LastError">The most recent session-creation failure, if any.</param>
/// <param name="Attempt">The reconnect attempt count for the current outage window.</param>
public sealed record SessionUnavailable<TSession>(
    DeviceInfo Device,
    Exception? LastError,
    int Attempt) : HostStatus<TSession>
    where TSession : class;

/// <summary>
/// Terminal status: the injected reconnect policy stopped retrying, so the
/// underlying device handle reached <see cref="ConnectionState.GaveUp"/>. The
/// device is still enumerated but unopenable; the host stays here until the
/// device re-enumerates (which resets the attempt budget). This is the
/// "enumerated but unopenable" signal a health probe maps to Degraded /
/// Unhealthy — distinct from the transient
/// <see cref="SessionUnavailable{TSession}"/>, which means a retry is still
/// pending.
/// </summary>
/// <typeparam name="TSession">The published session type.</typeparam>
/// <param name="LastError">The most recent session-creation / open failure, if any.</param>
/// <param name="Attempt">The reconnect attempt count reached before giving up.</param>
public sealed record SessionGaveUp<TSession>(
    Exception? LastError,
    int Attempt) : HostStatus<TSession>
    where TSession : class;

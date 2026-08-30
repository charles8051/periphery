# Example: Generic Session Host Pattern

## Goal

Show the recommended pattern for hosting a Periphery-managed device session and
publishing a session-scoped client/service for use by the rest of the
application.

This example is intentionally abstract. Replace:

- `TResource` with your opened device/resource,
- `IByteTransport` with your raw byte abstraction,
- `ICommandClient` with your protocol/application client.

---

## Pattern overview

With ADR-0032 applied, the application no longer hand-rolls:

- a nullable current-session field,
- a store for publishing and clearing that session,
- a `Task.Delay(Timeout.Infinite, ct)` availability loop,
- or the `host is null` two-phase initialization workaround.

Instead:

1. `DeviceSessionHost<TSession>` owns connect/disconnect/reconnect.
2. `createSession` builds the resource, transport, and client for one active device.
3. `onSessionEnded` cleans up the session-owned resource.
4. Consumers use `GetRequiredSession()`, `WaitForSessionAsync()`, or `Status`.

This keeps Periphery responsible for lifecycle and keeps the application focused
on session construction and policy.

---

## Sketch

```csharp name=SessionHostExample.cs
public sealed class SessionHost : IAsyncDisposable
{
    private readonly DeviceSessionHost<ActiveSession> _host;

    private SessionHost(DeviceSessionHost<ActiveSession> host)
    {
        _host = host;
    }

    public static async Task<SessionHost> OpenAsync(
        DeviceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var host = await DeviceSessionHost<ActiveSession>.OpenAsync(
            profile: profile,

            createSession: async (deviceInfo, ct) =>
            {
                TResource resource = await OpenResourceAsync(deviceInfo, ct)
                    .ConfigureAwait(false);

                IByteTransport transport = CreateTransport(resource);
                ICommandClient client = CreateClient(transport);

                return new ActiveSession(resource, client);
            },

            onSessionEnded: session =>
            {
                DisposeResource(session.Resource);
                return Task.CompletedTask;
            },

            ct: cancellationToken).ConfigureAwait(false);

        return new SessionHost(host);
    }

    public HostStatus<ActiveSession> Status => _host.Status;

    public ActiveSession GetRequiredSession() => _host.GetRequiredSession();

    public Task<ActiveSession> WaitForSessionAsync(
        CancellationToken cancellationToken = default)
        => _host.WaitForSessionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

public sealed class ActiveSession
{
    public ActiveSession(TResource resource, ICommandClient client)
    {
        Resource = resource;
        Client = client;
    }

    public TResource Resource { get; }
    public ICommandClient Client { get; }
}
```

---

## Status-aware consumption

```csharp name=SessionHostStatusUsage.cs
switch (host.Status)
{
    case SessionActive<ActiveSession> { Session: var session }:
        await session.Client.DoWorkAsync(cancellationToken);
        break;

    case SessionStarting<ActiveSession>:
        // Device is present and session creation is in flight.
        await host.WaitForSessionAsync(cancellationToken);
        break;

    case SessionUnavailable<ActiveSession> { LastError: var error, Attempt: var attempt }:
        // Reconnect is already running; decide whether to wait, log, or surface degraded state.
        LogUnavailable(error, attempt);
        break;

    case DeviceAbsent<ActiveSession>:
        // No matching device is currently present in the OS device tree.
        ReportDeviceMissing();
        break;
}
```

---

## Notes

### Why `createSession` is now the important seam

The application's only lifecycle coupling point is the `createSession` delegate.
That delegate receives the active `DeviceInfo`, opens whatever underlying
resource is needed, adapts it into raw transport, and returns a fully ready
session object.

### Why the session owns the per-connection resource

The session exists only for the lifetime of one connection. Letting the session
carry the active resource keeps ownership obvious:

- `createSession` constructs it,
- `SessionActive<TSession>` exposes it,
- `onSessionEnded` disposes it.

### Why `Status` matters in addition to `GetRequiredSession()`

`GetRequiredSession()` is still the ergonomic path when a caller requires an
active session right now.

`Status` matters when the caller needs to distinguish:

- device not present (`DeviceAbsent`),
- session creation in progress (`SessionStarting`),
- session available (`SessionActive`),
- reconnect/backoff in progress (`SessionUnavailable`).

### Where heartbeat fits

If you later add heartbeat, it still belongs above the session host as
application/session policy. `DeviceSessionHost<TSession>` should expose the
session cleanly; it should not silently embed protocol-specific health policy.

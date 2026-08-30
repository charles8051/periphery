# Example: Modbus Client Facade over a Periphery-managed Session

> **Stale — `DeviceFacade<TSession>` no longer exists.** It and `DeviceUseResult`
> were deleted from the library; [ADR-0033](../adr/0033-device-facade.md) is
> Superseded. Consumers use `DeviceSessionHost<TSession>` directly, which already
> carries the fail-fast surface this example reaches for: `GetRequiredSession()`,
> `TryGetCurrentSession(out …)`, `WaitForSessionAsync(ct)`, `HasSession`,
> `CurrentSession`, and `StatusChanged`. The *shape* the example teaches — a typed
> client owning its own serialization gate over a Periphery-managed session — still
> holds. The type name does not. See
> [examples_generic-session-host-example.md](examples_generic-session-host-example.md)
> for the same pattern written against the current API.

## Goal

Show the recommended top-level consumer shape after ADR-0033:

- `DeviceSessionHost<TSession>` remains the lifecycle primitive,
- `DeviceFacade<TSession>` provides the fail-fast invocation surface,
- a typed wrapper such as `ModbusClient` becomes the application-facing API.

This is the closest Periphery analogue to `HttpClient`: a long-lived client
object with operation methods, while device/session churn stays underneath.

---

## Typed facade sketch

```csharp name=ModbusClient.cs
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using Periphery;

public sealed class ModbusClient : IAsyncDisposable
{
    private readonly DeviceFacade<ActiveModbusSession> _facade;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private ModbusClient(DeviceFacade<ActiveModbusSession> facade)
    {
        _facade = facade;
    }

    public static async Task<ModbusClient> OpenAsync(
        DeviceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var facade = await DeviceFacade<ActiveModbusSession>.OpenAsync(
            profile,
            createSession: (deviceInfo, ct) =>
            {
                var portName = deviceInfo.PortName?.Value.Value
                    ?? throw new InvalidOperationException("No port name.");

                var port = new SerialPort(portName, 19200)
                {
                    Parity = Parity.Even,
                    DataBits = 8,
                    StopBits = StopBits.One
                };

                port.Open();

                var byteSource = new SerialPortByteSource(port);
                var transceiver = Transceiver.Wrap(byteSource);
                var protocol = new ModbusRtuClient(transceiver);

                return Task.FromResult(new ActiveModbusSession(protocol, port));
            },
            onSessionEnded: session =>
            {
                try
                {
                    if (session.Port.IsOpen)
                        session.Port.Close();
                }
                finally
                {
                    session.Port.Dispose();
                }

                return Task.CompletedTask;
            },
            ct: cancellationToken).ConfigureAwait(false);

        return new ModbusClient(facade);
    }

    public HostStatus<ActiveModbusSession> Status => _facade.Status;

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte unitIdentifier,
        ushort startingAddress,
        ushort numberOfRegisters,
        CancellationToken cancellationToken = default)
    {
        return UseSerializedAsync(
            (session, ct) => session.ReadHoldingRegistersAsync(
                unitIdentifier,
                startingAddress,
                numberOfRegisters,
                ct),
            cancellationToken);
    }

    public async Task<DeviceUseResult<ushort[]>> TryReadHoldingRegistersAsync(
        byte unitIdentifier,
        ushort startingAddress,
        ushort numberOfRegisters,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _facade.TryUseAsync(
                (session, ct) => session.ReadHoldingRegistersAsync(
                    unitIdentifier,
                    startingAddress,
                    numberOfRegisters,
                    ct),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public ValueTask DisposeAsync() => _facade.DisposeAsync();

    private async Task<TResult> UseSerializedAsync<TResult>(
        Func<ActiveModbusSession, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _facade.UseAsync(action, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
```

---

## Consumer experience

```csharp name=ModbusClientUsage.cs
ushort[] registers = await modbusClient.ReadHoldingRegistersAsync(
    unitIdentifier: 1,
    startingAddress: 0,
    numberOfRegisters: 2,
    cancellationToken);
```

That is the recommended happy path:

- the caller holds a stable `ModbusClient`,
- the operation fails fast if no session is active,
- and lifecycle/state details remain available through `Status` when needed.

---

## Status-aware usage

```csharp name=ModbusClientStatusUsage.cs
switch (modbusClient.Status)
{
    case SessionActive<ActiveModbusSession>:
        var values = await modbusClient.ReadHoldingRegistersAsync(1, 0, 2, cancellationToken);
        break;

    case SessionStarting<ActiveModbusSession>:
        logger.LogInformation("Modbus session is starting.");
        break;

    case SessionUnavailable<ActiveModbusSession> { LastError: var error, Attempt: var attempt }:
        logger.LogWarning(error, "Modbus reconnect pending (attempt {Attempt}).", attempt);
        break;

    case DeviceAbsent<ActiveModbusSession>:
        logger.LogInformation("Modbus device is not present.");
        break;
}
```

---

## Why `SemaphoreSlim` lives here

The serialization gate is in `ModbusClient`, not in `DeviceFacade<TSession>`.

That is intentional:

- `DeviceFacade<TSession>` is a generic invocation facade,
- concurrency requirements are protocol-specific,
- Modbus request/response over one serial connection is typically single-flight.

Put single-flight policy in the typed client/session layer, where the protocol
semantics are actually known.

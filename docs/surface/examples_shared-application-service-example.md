# Example: Shared Application Service over a DeviceFacade-backed Client

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

Show how another service in the application should depend on a typed client
built on `DeviceFacade<TSession>` rather than depending on the raw lifecycle
host, raw transport, or raw protocol client.

This example is intentionally general in spirit but uses Modbus for concreteness.

---

## Application-facing service

```csharp name=ModbusApplicationService.cs
using System.Threading;
using System.Threading.Tasks;

public sealed class ModbusApplicationService
{
    private readonly ModbusClient _client;

    public ModbusApplicationService(ModbusClient client)
    {
        _client = client;
    }

    public HostStatus<ActiveModbusSession> Status => _client.Status;

    public Task<ushort[]> ReadTemperatureAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.ReadHoldingRegistersAsync(
            unitIdentifier: 1,
            startingAddress: 0,
            numberOfRegisters: 2,
            cancellationToken: cancellationToken);
    }

    public Task<DeviceUseResult<ushort[]>> TryReadTemperatureAsync(
        CancellationToken cancellationToken = default)
        => _client.TryReadHoldingRegistersAsync(
            unitIdentifier: 1,
            startingAddress: 0,
            numberOfRegisters: 2,
            cancellationToken: cancellationToken);

    public string DescribeAvailability() => Status switch
    {
        SessionActive<ActiveModbusSession> => "Modbus session is ready.",
        SessionStarting<ActiveModbusSession> => "Modbus session is starting.",
        SessionUnavailable<ActiveModbusSession> { Attempt: var attempt } =>
            $"Modbus reconnect pending (attempt {attempt}).",
        DeviceAbsent<ActiveModbusSession> => "Modbus device is not present.",
        _ => "Unknown host status."
    };
}
```

---

## Another service consuming it

```csharp name=TelemetryWorker.cs
using System.Threading;
using System.Threading.Tasks;

public sealed class TelemetryWorker
{
    private readonly ModbusApplicationService _modbus;

    public TelemetryWorker(ModbusApplicationService modbus)
    {
        _modbus = modbus;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        switch (_modbus.Status)
        {
            case SessionActive<ActiveModbusSession>:
                var values = await _modbus.ReadTemperatureAsync(cancellationToken);
                // interpret and publish telemetry
                break;

            case SessionStarting<ActiveModbusSession>:
            case SessionUnavailable<ActiveModbusSession>:
                var result = await _modbus.TryReadTemperatureAsync(cancellationToken);
                if (result.Success)
                {
                    var readyValues = result.Result!;
                    // interpret and publish telemetry
                }
                break;

            case DeviceAbsent<ActiveModbusSession>:
                // Skip this cycle or publish a "device missing" health signal.
                break;
        }
    }
}
```

---

## Why this shape is better than exposing the raw host

If another service depends directly on:

- `DeviceSessionHost<ActiveModbusSession>`,
- `DeviceFacade<ActiveModbusSession>`,
- `SerialPort`,
- or `ModbusRtuClient` as if it were a permanent singleton,

then application policy starts to leak into too many places.

By depending on an application-facing service instead:

- the rest of the app does not care how the session is hosted,
- request serialization stays centralized in the typed client/application layer,
- disconnected behavior can be made explicit through `HostStatus<TSession>`,
- fail-fast and try-use behavior can be centralized in the client/service boundary,
- heartbeat or background supervision can be added later without changing every
  consumer.

---

## Extensions

This shape generalizes well to:

- protocol-specific services,
- multi-device coordinators,
- background telemetry workers,
- UI-facing state services,
- command queues or actor-style supervisors.

The important point is that the application depends on a stable service boundary,
while the underlying session comes and goes cleanly underneath it and exposes
precise lifecycle state when needed.

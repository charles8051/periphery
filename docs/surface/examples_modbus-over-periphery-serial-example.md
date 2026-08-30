# Example: Modbus over Periphery-managed Serial Session

## Goal

Show one concrete instantiation of the new session-host pattern:

- Periphery owns lifecycle,
- `System.IO.Ports.SerialPort` is the underlying resource,
- CallAndResponse provides framed communication,
- `ModbusRtuClient` is created per active session,
- `DeviceSessionHost<ActiveModbusSession>` publishes the session to the rest of
  the application.

This is an example of the broader architecture, not a special case that changes
the rules.

---

## Architectural shape

- `DeviceSessionHost<ActiveModbusSession>` owns connect/disconnect/reconnect.
- `SerialPort` is opened inside `createSession`.
- a byte adapter exposes the serial port as `IByteSource`.
- `Transceiver.Wrap(...)` creates the active communication primitive.
- `ModbusRtuClient` is created for that active session.
- disconnect or failure withdraws the session immediately.
- consumers can inspect `HostStatus<ActiveModbusSession>` when they need richer
  state than a simple connected/disconnected boolean.

This is exactly the composition described in ADR-0032 and the session
integration guide: Periphery owns lifecycle, while the communication and
protocol layers are created per active session.

---

## Example adapter

```csharp name=SerialPortByteSource.cs
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;

public sealed class SerialPortByteSource : IByteSource
{
    private readonly SerialPort _port;

    public SerialPortByteSource(SerialPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    public bool IsConnected => _port.IsOpen;

    public Task OpenAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CloseAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
        => _port.BaseStream.WriteAsync(buffer, cancellationToken).AsTask();

    public async Task<byte> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        byte[] one = new byte[1];
        int read = await ReadChunkAsync(one, cancellationToken).ConfigureAwait(false);
        if (read <= 0)
            throw new InvalidOperationException("End of stream.");
        return one[0];
    }

    public Task<int> ReadChunkAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => _port.BaseStream.ReadAsync(buffer, cancellationToken).AsTask();
}
```

---

## Session wrapper

```csharp name=ActiveModbusSession.cs
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Protocol.Modbus;

public sealed class ActiveModbusSession
{
    private readonly ModbusRtuClient _client;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ActiveModbusSession(ModbusRtuClient client, SerialPort port)
    {
        _client = client;
        Port = port;
    }

    public SerialPort Port { get; }

    public async Task<ushort[]> ReadHoldingRegistersAsync(
        byte unitIdentifier,
        ushort startingAddress,
        ushort numberOfRegisters,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await _client.ReadHoldingRegisters(
                unitIdentifier,
                startingAddress,
                numberOfRegisters,
                cancellationToken).ConfigureAwait(false);

            var result = new ushort[numberOfRegisters];
            var span = bytes.Span;

            for (int i = 0; i < numberOfRegisters; i++)
                result[i] = (ushort)((span[2 * i] << 8) | span[2 * i + 1]);

            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
```

---

## Host sketch

```csharp name=ModbusDeviceHost.cs
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using Periphery;

public sealed class ModbusDeviceHost : IAsyncDisposable
{
    private readonly DeviceSessionHost<ActiveModbusSession> _host;

    private ModbusDeviceHost(DeviceSessionHost<ActiveModbusSession> host)
    {
        _host = host;
    }

    public static async Task<ModbusDeviceHost> OpenAsync(
        DeviceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var host = await DeviceSessionHost<ActiveModbusSession>.OpenAsync(
            profile: profile,

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
                var client = new ModbusRtuClient(transceiver);

                return Task.FromResult(new ActiveModbusSession(client, port));
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

        return new ModbusDeviceHost(host);
    }

    public HostStatus<ActiveModbusSession> Status => _host.Status;

    public ActiveModbusSession GetRequiredSession() => _host.GetRequiredSession();

    public Task<ActiveModbusSession> WaitForSessionAsync(
        CancellationToken cancellationToken = default)
        => _host.WaitForSessionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
```

---

## Status-aware usage

```csharp name=ModbusHostStatusUsage.cs
switch (modbusHost.Status)
{
    case SessionActive<ActiveModbusSession> { Session: var session }:
        await session.ReadHoldingRegistersAsync(1, 0, 2, cancellationToken);
        break;

    case SessionStarting<ActiveModbusSession>:
        await modbusHost.WaitForSessionAsync(cancellationToken);
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

## Why this is the correct shape

### Periphery owns lifecycle

Only `DeviceSessionHost<ActiveModbusSession>` opens, withdraws, and re-establishes
the session.

### CallAndResponse owns framing

The transceiver is a communication wrapper over the already-open serial session.

### Modbus owns protocol semantics

`ModbusRtuClient` translates Modbus requests/responses; it does not own the
serial port lifecycle.

### The application owns orchestration

If other services need Modbus, they should depend on a host/application service,
not on the raw port or the internal session-construction mechanics.

For most top-level consumers, the next layer should usually be a typed client
such as `ModbusClient` built directly on `DeviceSessionHost<ActiveModbusSession>`.
See [examples_generic-session-host-example.md](examples_generic-session-host-example.md).
(`DeviceFacade<TSession>`, which earlier revisions of this doc pointed at, was
removed — see [ADR-0033](../adr/0033-device-facade.md).)

### Heartbeat placement

If you add heartbeat to this example:

- an initial one-shot probe belongs at session creation time if it is required
  before publishing the session,
- ongoing heartbeat belongs in a session supervisor or application service above
  `ActiveModbusSession`,
- `DeviceSessionHost<TSession>` should remain the lifecycle boundary, not the
  application policy engine.

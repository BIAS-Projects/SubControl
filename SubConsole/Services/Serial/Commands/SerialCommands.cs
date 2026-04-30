using SubConsole.Helpers;
using SubConsole.Models;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial.Commands;

// ═════════════════════════════════════════════════════════════════════════════
// Base contract
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A self-contained unit of work executed against the serial port manager.
/// Commands are created by the TCP/command layer, dispatched by
/// <see cref="ISerialCommandDispatcher"/>, and executed synchronously
/// within a single async scope.
/// </summary>
public interface ISerialCommand
{
    Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token);
}

// ═════════════════════════════════════════════════════════════════════════════
// List commands
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Enumerate USB devices currently visible to the OS and return their
/// identifiers. Does not modify the registry.
/// </summary>
public sealed class ListUsbDevicesCommand : ISerialCommand
{
    public List<UsbSerialPortInfo>? Result { get; private set; }

    public async Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        Result = (await UsbSerialPortMapper.GetUsbSerialPortsAsync(token)).ToList();
        return OperationResult.Success();
    }
}

/// <summary>
/// Return all currently registered devices from the registry.
/// </summary>
public sealed class ListRegisteredDevicesCommand : ISerialCommand
{
    public IReadOnlyList<DeviceRegistration>? Result { get; private set; }

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        Result = manager.GetRegisteredDevices();
        return Task.FromResult(OperationResult.Success());
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Registration commands
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Register a USB device in the registry and associate it with one or
/// more logical function names. Optionally auto-opens the port.
/// </summary>
public sealed class RegisterDeviceCommand : ISerialCommand
{
    public required UsbSerialPortInfo Identifier { get; init; }
    //public required IEnumerable<string> FunctionNames { get; init; }
    public required string FunctionName { get; init; }

    /// <summary>
    /// When true the manager will also open the serial port immediately
    /// using <see cref="BaudRate"/> and <see cref="WorkerType"/>.
    /// </summary>
    public bool AutoOpen { get; init; } = false;

    public int BaudRate { get; init; } = 115_200;
    public SerialWorkerType WorkerType { get; init; } = SerialWorkerType.Text;

    public async Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        manager.RegisterDevice(Identifier, FunctionName, BaudRate, WorkerType);

        if (!AutoOpen)
            return OperationResult.Success();

        return await manager.OpenPortAsync(Identifier.Key, token);
    }
}

/// <summary>
/// Remove a device from the registry and close its port if open.
/// </summary>
public sealed class UnregisterDeviceCommand : ISerialCommand
{
    public required string DeviceKey { get; init; }

    public async Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
        => await manager.UnregisterDeviceAsync(DeviceKey, token);
}

// ═════════════════════════════════════════════════════════════════════════════
// Port lifecycle commands
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Open the serial port for a registered device.
/// The device must already be registered and have a port path assigned.
/// </summary>
public sealed class OpenPortCommand : ISerialCommand
{
    /// <summary>Device registry key (VID:PID:SN).</summary>
    public required string DeviceKey { get; init; }
    public int BaudRate { get; init; } = 115_200;
    public SerialWorkerType WorkerType { get; init; } = SerialWorkerType.Text;

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
        => manager.OpenPortAsync(DeviceKey, token);
}

/// <summary>
/// Close the serial port for a device (device remains registered).
/// </summary>
public sealed class ClosePortCommand : ISerialCommand
{
    public required string DeviceKey { get; init; }

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
        => manager.ClosePortAsync(DeviceKey, token);
}

// ═════════════════════════════════════════════════════════════════════════════
// I/O commands
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Write bytes to the port whose device is mapped to <see cref="FunctionName"/>.
/// </summary>
public sealed class WriteCommand : ISerialCommand
{
    public required string FunctionName { get; init; }
    public required byte[] Data { get; init; }

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
        => manager.WriteAsync(FunctionName, Data, token);
}

/// <summary>
/// Write a UTF-8 text string to the port mapped to <see cref="FunctionName"/>.
/// A newline is appended when <see cref="AppendNewline"/> is true (default).
/// </summary>
public sealed class WriteTextCommand : ISerialCommand
{
    public required string FunctionName { get; init; }
    public required string Text { get; init; }
    public bool AppendNewline { get; init; } = true;

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        var payload = AppendNewline ? Text + "\r\n" : Text;
        return manager.WriteTextAsync(FunctionName, payload, token);
    }
}




/// <summary>
/// Subscribe to incoming messages for one or more function names.
/// Returns the shared <see cref="System.Threading.Channels.ChannelReader{T}"/>
/// from the manager; callers must not complete it.
/// </summary>
public sealed class SubscribeCommand : ISerialCommand
{
    public required IEnumerable<string> FunctionNames { get; init; }

    public System.Threading.Channels.ChannelReader<SerialMessage>? Reader { get; private set; }

    public Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        Reader = manager.GetMessageReader(FunctionNames);
        return Task.FromResult(OperationResult.Success());
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Auto-discovery command
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Scan USB devices, reconcile with the registry, and optionally
/// open ports for all newly found registered devices.
/// </summary>
public sealed class AutoDiscoverCommand : ISerialCommand
{
    public bool AutoOpenFound { get; init; } = true;
    public int DefaultBaudRate { get; init; } = 115_200;
    public SerialWorkerType DefaultWorkerType { get; init; } = SerialWorkerType.Text;

    public int NewPortsOpened { get; private set; }

    public async Task<OperationResult> ExecuteAsync(
        ISerialPortManagerService manager,
        CancellationToken token)
    {
        var result = await manager.AutoDiscoverAsync(
            AutoOpenFound, DefaultBaudRate, DefaultWorkerType, token);
        if (result.IsSuccess)
        {
            NewPortsOpened = result.Value;
        }
        else
        {
            return OperationResult.Failure(result.Message);
        }

        return OperationResult.Success();
    }
}

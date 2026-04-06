using SubConsole.Models;
using SubConsole.Services;
using System.IO.Ports;

namespace SubConsole.Serial;

public class FlirCameraClient : ISerialWorker
{
    private readonly string _portName;
    private readonly int _baudRate;

    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen == true;

    public FlirCameraClient(string portName, int baudRate = 921600)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    // ---------------- START ----------------

    public Task StartAsync(CancellationToken token)
    {
        _port = new SerialPort(_portName, _baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = 5000,
            WriteTimeout = 5000
        };

        _port.Open();

        return Task.CompletedTask;
    }

    // ---------------- STOP ----------------

    public Task StopAsync()
    {
        try
        {
            if (_port?.IsOpen == true)
                _port.Close();
        }
        finally
        {
            _port?.Dispose();
            _port = null;
        }

        return Task.CompletedTask;
    }

    // =========================================================
    // 🔥 CORE BINARY METHODS (THIS REPLACES YOUR TEXT PIPELINE)
    // =========================================================

    public async Task WriteAsync(byte[] data, CancellationToken token)
    {
        if (_port == null || !_port.IsOpen)
            throw new InvalidOperationException("Port not open");

        await _port.BaseStream.WriteAsync(data, token);
    }

    public async Task<byte[]> ReadExactAsync(int size, CancellationToken token)
    {
        if (_port == null || !_port.IsOpen)
            throw new InvalidOperationException("Port not open");

        byte[] buffer = new byte[size];
        int offset = 0;

        while (offset < size)
        {
            int read = await _port.BaseStream.ReadAsync(buffer, offset, size - offset, token);

            if (read == 0)
                throw new IOException("Device disconnected");

            offset += read;
        }

        return buffer;
    }

    // =========================================================
    // 🔧 GENERIC REQUEST/RESPONSE HELPER
    // =========================================================

    public async Task<byte[]> SendCommandAsync(
        byte[] command,
        int expectedResponseBytes,
        CancellationToken token)
    {
        await WriteAsync(command, token);

        return await ReadExactAsync(expectedResponseBytes, token);
    }

    // =========================================================
    // 📷 FLIR-LIKE HIGH LEVEL METHODS (SKELETONS)
    // =========================================================

    // NOTE:
    // These don't implement the actual FLIR packet format yet.
    // They give you the structure to plug it in cleanly.

    public async Task<byte[]> CaptureFrameAsync(CancellationToken token)
    {
        // TODO: Replace with actual FLIR command packet
        byte[] command = BuildCaptureCommand();

        // TODO: Replace with actual expected response size logic
        int expectedSize = 1024;

        return await SendCommandAsync(command, expectedSize, token);
    }

    public async Task<byte[]> ReadMemoryAsync(uint offset, ushort size, CancellationToken token)
    {
        byte[] command = BuildMemReadCommand(offset, size);

        return await SendCommandAsync(command, size, token);
    }

    public async Task WriteMemoryAsync(uint offset, byte[] data, CancellationToken token)
    {
        byte[] command = BuildMemWriteCommand(offset, data);

        await WriteAsync(command, token);

        // Optionally read ACK if protocol requires it
    }

    // =========================================================
    // 🧩 COMMAND BUILDERS (PLACEHOLDERS)
    // =========================================================

    private byte[] BuildCaptureCommand()
    {
        // Replace with real FLIR packet structure
        return new byte[] { 0x00 };
    }

    private byte[] BuildMemReadCommand(uint offset, ushort size)
    {
        // Replace with real FLIR packet structure
        var buffer = new byte[6];

        BitConverter.GetBytes(offset).CopyTo(buffer, 0);
        BitConverter.GetBytes(size).CopyTo(buffer, 4);

        return buffer;
    }

    private byte[] BuildMemWriteCommand(uint offset, byte[] data)
    {
        var buffer = new byte[4 + data.Length];

        BitConverter.GetBytes(offset).CopyTo(buffer, 0);
        data.CopyTo(buffer, 4);

        return buffer;
    }

    public async Task<OperationResult> WriteAsync(string data, CancellationToken token)
    {
        // Only if meaningful — otherwise return failure
        return OperationResult.Failure("Text write not supported for FLIR device");
    }
}
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial.Commands;

namespace SubConsole.Services.Serial;

/// <summary>
/// Accepts an <see cref="ISerialCommand"/>, executes it against the
/// <see cref="ISerialPortManagerService"/>, and returns the result.
///
/// This is the single entry point for all callers (TCP handler, internal
/// services, tests) — nothing calls the manager directly.
/// </summary>
public interface ISerialCommandDispatcher
{
    Task<OperationResult> DispatchAsync(ISerialCommand command, CancellationToken token = default);
}

public sealed class SerialCommandDispatcher : ISerialCommandDispatcher
{
    private readonly ISerialPortManagerService _manager;
    private readonly ILogger<SerialCommandDispatcher> _logger;

    public SerialCommandDispatcher(
        ISerialPortManagerService manager,
        ILogger<SerialCommandDispatcher> logger)
    {
        _manager = manager;
        _logger  = logger;
    }

    public async Task<OperationResult> DispatchAsync(
        ISerialCommand command, CancellationToken token = default)
    {
        var commandName = command.GetType().Name;

        _logger.LogInformation(
            "Executing serial command {Command}",
            commandName);

        var start = DateTime.UtcNow;
        try
        {
            var result = await _manager.ExecuteAsync(command, token);

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Serial command {Command} failed: {Message}",
                    commandName,
                    result.Message);
            }
            var durationMs = (DateTime.UtcNow - start).TotalMilliseconds;

            _logger.LogInformation(
                "Completed serial command {Command}: {Success} in {Duration}ms",
                commandName,
                result.IsSuccess,
                durationMs);

            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (DateTime.UtcNow - start).TotalMilliseconds;

            _logger.LogError(
                ex,
                "Exception during serial command {Command}: {Success} in {Duration}ms",
                commandName,
                false,
                durationMs);
            return OperationResult.Failure(ex.Message);
        }
    }
}

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
        _logger.LogDebug("Dispatching {Command}", command.GetType().Name);

        try
        {
            var result = await _manager.ExecuteAsync(command, token);

            if (!result.IsSuccess)
                _logger.LogWarning(
                    "{Command} failed: {Message}", command.GetType().Name, result.Message);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Command} threw an exception", command.GetType().Name);
            return OperationResult.Failure(ex.Message);
        }
    }
}

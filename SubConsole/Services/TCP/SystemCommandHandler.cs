using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using SubConsole.Models;
using System.Reflection;
using System.Text.Json;

namespace SubConsole.Services.TCP;

/// <summary>
/// Handles system-level TCP commands — commands that operate on the
/// SubConsole process itself rather than on serial devices or cameras.
///
/// Routed via message.Function.Contains("SYSTEM").
///
/// Supported commands:
///   LOGGING  — change the Serilog minimum log level at runtime
///   VERSION  — report the running assembly version / build info
/// </summary>
public sealed class TcpSystemCommandHandler
{
    private readonly ILogger<TcpSystemCommandHandler> _logger;
    private readonly LoggingLevelSwitch _levelSwitch;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { WriteIndented = false };

    public TcpSystemCommandHandler(
        ILogger<TcpSystemCommandHandler> logger,
        LoggingLevelSwitch levelSwitch)
    {
        _logger = logger;
        _levelSwitch = levelSwitch;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parse and execute a TCP system command. Returns a JSON ACK/NACK string
    /// sent back on the response channel.
    /// </summary>
    public async Task<string> HandleAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug(
            "Handling system command {Command} for {Function}",
            message.Command,
            message.Function);

        try
        {
            string data = message.Command switch
            {
                "LOGGING" => await HandleLoggingAsync(message, token),
                "VERSION" => await HandleVersionAsync(message, token),
                _ => await UnknownCommand(message.Command)
            };

            var response = new TCPMessageBody<string>(
                Function: message.Function,
                Command: message.Command,
                Data: data);

            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "System command {Command} failed for {Function}",
                message.Command,
                message.Function);

            var errorResponse = new TCPMessageBody<string>(
                Function: message.Function,
                Command: message.Command,
                Data: ErrorResponse(ex.Message));

            return JsonSerializer.Serialize(errorResponse);
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private Task<string> UnknownCommand(string command)
    {
        _logger.LogWarning("Unknown System Command: {Command}", command);
        return Task.FromResult(ErrorResponse($"Unknown System Command: {command}"));
    }

    /// <summary>
    /// Changes the Serilog minimum log level at runtime via the shared
    /// LoggingLevelSwitch. Expects message.Data to be one of the
    /// Serilog.Events.LogEventLevel names (case-sensitive, matching the enum).
    /// </summary>
    private Task<string> HandleLoggingAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogCritical(
            "Changing log level to {Level}",
            message.Data);

        if (string.IsNullOrWhiteSpace(message.Data))
            return Task.FromResult(ErrorResponse("HandleLoggingAsync log level cannot be empty"));

        if (!Enum.TryParse<LogEventLevel>(message.Data, out var level))
        {
            _logger.LogCritical("LOGGING failed: unrecognised level {Level}", message.Data);
            return Task.FromResult(ErrorResponse($"Unknown logging level: {message.Data}"));
        }

        var previous = _levelSwitch.MinimumLevel;
        _levelSwitch.MinimumLevel = level;

        _logger.LogCritical(
            "Log level changed from {Previous} to {New}",
            previous, level);

        return Task.FromResult(SuccessResponse(new
        {
            previousLevel = previous.ToString(),
            currentLevel = level.ToString()
        }));
    }

    /// <summary>
    /// Reports the running assembly's informational version, file version,
    /// and build timestamp so clients can confirm what's deployed.
    /// </summary>
    private Task<string> HandleVersionAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug("Executing VERSION");

        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;
        var buildTime = File.Exists(assembly.Location)
            ? File.GetLastWriteTimeUtc(assembly.Location)
            : (DateTime?)null;

        var payload = new
        {
            version = informational ?? assembly.GetName().Version?.ToString() ?? "unknown",
            fileVersion = fileVersion ?? "unknown",
            assemblyName = assembly.GetName().Name,
            buildTimeUtc = buildTime,
            runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString()
        };

        _logger.LogInformation("VERSION reported {Version}", payload.version);

        return Task.FromResult(SuccessResponse(payload));
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static string SuccessResponse(object? data = null)
        => JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);

    private static string ErrorResponse(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
}
namespace SubConsole.Models;

/// <summary>Simple discriminated-union result without a payload.</summary>
public sealed class OperationResult
{
    public bool IsSuccess { get; }
    public string Message { get; }

    private OperationResult(bool success, string message)
    {
        IsSuccess = success;
        Message = message;
    }

    public static OperationResult Success(string message = "OK") => new(true, message);
    public static OperationResult Failure(string message) => new(false, message);

    public override string ToString() => $"{(IsSuccess ? "OK" : "FAIL")}: {Message}";
}





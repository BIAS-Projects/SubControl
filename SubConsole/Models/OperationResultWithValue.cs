/// <summary>Result that also carries a typed value on success.</summary>
public sealed class OperationResultWithValue<T>
{
    public bool IsSuccess { get; }
    public string Message { get; }
    public T? Value { get; }

    private OperationResultWithValue(bool success, string message, T? value)
    {
        IsSuccess = success;
        Message = message;
        Value = value;
    }

    public static OperationResultWithValue<T> Success(T value, string message = "OK") =>
        new(true, message, value);

    public static OperationResultWithValue<T> Failure(string message) =>
        new(false, message, default);
}
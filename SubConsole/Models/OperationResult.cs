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





//using Microsoft.Extensions.Logging;
//using SubConsole.Helpers;
//using SubConsole.Models;
//using System;
//using System.Collections.Generic;
//using System.Text;

//namespace SubConsole.Models
//{
//    public record OperationResult
//    {
//        public bool IsSuccess { get; init; }
//        public string? Error { get; init; }
//        public Exception? Exception { get; init; }

//        private OperationResult() { }

//        public static OperationResult Success() => new() { IsSuccess = true };

//        public static OperationResult Failure(string error, Exception? ex = null) => new()
//        {
//            IsSuccess = false,
//            Error = error,
//            Exception = ex
//        };
//    }


//}

////Doesn't return a value
////public record Result
////{
////    public bool IsSuccess { get; init; }
////    public string? Error { get; init; }
////    public Exception? Exception { get; init; }

////    private Result() { }

////    public static Result Success() => new() { IsSuccess = true };

////    public static Result Failure(string error, Exception? ex = null) => new()
////    {
////        IsSuccess = false,
////        Error = error,
////        Exception = ex
////    };
////}




//// Returning results
////public async Task<Result> WriteAsync(string text, CancellationToken token)
////{
////    if (_port == null || !_port.IsOpen)
////        return Result.Failure("Port is not open");
////    try
////    {
////        var bytes = Encoding.UTF8.GetBytes(text);
////        await _port.BaseStream.WriteAsync(bytes, token);
////        return Result.Success();
////    }
////    catch (IOException ex)
////    {
////        return Result.Failure("IO error during write", ex);
////    }
////}

////public async Task<Result<UsbSerialPortInfo>> GetPortInfoAsync(string portName)
////{
////    var port = UsbPortRegistry.Instance.TryGetPort(portName, out var info);
////    return port
////        ? Result<UsbSerialPortInfo>.Success(info)
////        : Result<UsbSerialPortInfo>.Failure($"Port {portName} not found");
////}

////// Consuming results
////var result = await WriteAsync("hello", token);

////if (!result.IsSuccess)
////{
////    logger.LogError(result.Exception, result.Error);
////    return;
////}

////// With a value
////var portResult = await GetPortInfoAsync("COM3");

////if (portResult.IsSuccess)
////    Console.WriteLine(portResult.Value!.Description);
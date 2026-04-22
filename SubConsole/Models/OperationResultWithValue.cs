//using Microsoft.Extensions.Logging;
//using SubConsole.Helpers;
//using SubConsole.Models;
//using System;
//using System.Collections.Generic;
//using System.Text;

//namespace SubConsole.Models
//{
//    public record OperationResultWithValue<T>
//    {
//        public bool IsSuccess { get; init; }
//        public T? Value { get; init; }
//        public string? Error { get; init; }
//        public Exception? Exception { get; init; }

//        private OperationResultWithValue() { }

//        public static OperationResultWithValue<T> Success(T value) => new()
//        {
//            IsSuccess = true,
//            Value = value
//        };

//        public static OperationResultWithValue<T> Failure(string error, Exception? ex = null) => new()
//        {
//            IsSuccess = false,
//            Error = error,
//            Exception = ex
//        };
//    }
//}


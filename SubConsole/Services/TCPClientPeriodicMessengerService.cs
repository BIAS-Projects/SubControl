//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;
//using SQLite;
//using System;
//using System.Collections.Generic;
//using System.Text;

//namespace SubConsole.Services
//{
//    public class TCPClientPeriodicMessengerService : BackgroundService
//    {

//        private readonly ILogger<TCPClientPeriodicMessengerService> _logger;
//        public TCPClientPeriodicMessengerService(ILogger<TCPClientPeriodicMessengerService> logger)
//        {
//            _logger = logger;
//        }

//        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//        {
//            _logger.LogError("ExecuteAsync");
//            try
//            {
//                await Task.Delay(Timeout.Infinite, stoppingToken);
//            }
//            catch (OperationCanceledException)
//            {
//            }
//        }

//        //public override async Task StartAsync(CancellationToken stoppingToken)
//        //{
//        //    _logger.LogError("Start Async");
//        //}

//        public override async Task StopAsync(CancellationToken stoppingToken)
//        {
//            _logger.LogError("Stop Async");
//        }
//    }
//}

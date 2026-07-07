using Renci.SshNet;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SubControlMAUI.Services
{
    public sealed class ConsoleSshStreamSession : IAsyncDisposable
    {
        private readonly ShellStream _shell;
        private readonly CancellationTokenSource _cts;
        private readonly Task _readerTask;

        public event Action<string>? LineReceived;

        internal ConsoleSshStreamSession(
            ShellStream shell,
            CancellationTokenSource cts,
            Task readerTask)
        {
            _shell = shell;
            _cts = cts;
            _readerTask = readerTask;
        }

        internal void RaiseLine(string line)
            => LineReceived?.Invoke(line);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore shutdown timing issues
            }

            try { _shell.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
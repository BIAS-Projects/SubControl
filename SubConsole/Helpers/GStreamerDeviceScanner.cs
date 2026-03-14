using Gst;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SysTask = System.Threading.Tasks.Task;

namespace SubConsole.Helpers
{
    public static class GStreamerDeviceScanner
    {
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public static async Task<List<string>> GetVideoDevicesAsync()
        {
            await _lock.WaitAsync();

            try
            {
                return await SysTask.Run(() =>
                {
                    var devices = new List<string>();

                    var monitor = new DeviceMonitor();
                    monitor.AddFilter("Video/Source", null);
                    monitor.Start();

                    foreach (var device in monitor.Devices)
                    {
                        var props = device.Properties;

                        if (props != null && props.HasField("device.path"))
                        {
                            devices.Add(props.GetString("device.path"));
                        }
                    }

                    monitor.Stop();

                    return devices;
                });
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
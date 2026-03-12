using SubConsole;
using SubConsole.Models;
using SubConsole.Services;

class Program
{
    static async Task Main()
    {
        var host = new TcpHostService(9000);
        //   var host = new TcpStreamingServer(9000);


        CommPortService commPortService = new CommPortService();

        foreach (SerialDevice device in commPortService.GetSerialDevices())
        {
            Console.WriteLine($"{device.DeviceId}");
        }







        host.ClientConnected += c =>
            Console.WriteLine($"Client {c.Client.RemoteEndPoint} connected");

        host.MessageReceived += async (client, message) =>
        {
            Console.WriteLine($"Received: {message}");

            // Echo back
            await host.SendAsync(client, $"Echo: {message}");
        };

        await host.StartAsync();

        //Linux await serial.StartAsync("/dev/ttyUSB0", 115200);
        await commPortService.StartAsync("COM6", 115200);

        await foreach (var line in commPortService.Reader.ReadAllAsync())
        {
            Console.WriteLine($"RX: {line}");
        }

    }
}

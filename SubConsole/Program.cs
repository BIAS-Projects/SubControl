using SubConsole;

class Program
{
    static async Task Main()
    {
        var host = new TcpHostService(9000);
     //   var host = new TcpStreamingServer(9000);


        host.ClientConnected += c =>
            Console.WriteLine($"Client {c.Client.RemoteEndPoint} connected");

        host.MessageReceived += async (client, message) =>
        {
            Console.WriteLine($"Received: {message}");

            // Echo back
            await host.SendAsync(client, $"Echo: {message}");
        };

        await host.StartAsync();
    }
}

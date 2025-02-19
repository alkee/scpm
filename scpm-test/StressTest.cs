using System.Diagnostics;
using scpm.Net;

namespace scpm_test;

public class StressTest
{
    // 각 TestClass 별로 서로다른 port 를 사용하도록 해야 동시 테스트가 가능
    const int TEST_PORT = 9060;

    [Theory]
    [InlineData(100, 15.0f)]
    public async Task ConnectionTest(int numberOfConnection, float durationSeconds)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var server = new Server(TEST_PORT);
        var connectedCount = 0;
        var messageCount = 0;
        server.Handshaked += (c) => { ++connectedCount; };
        server.Closed += (c) => { --connectedCount; };
        server.MessageReceived += (c, m) => { ++messageCount; };
        _ = server.StartAsync(cts.Token);

        var clients = new List<Client>();
        for (var i = 0; i < numberOfConnection; ++i)
        {
            var client = new Client();
            lock (clients) clients.Add(client);
            var task = client.ConnectAsync("localhost", TEST_PORT, cts.Token);
            _ = task.ContinueWith((t) =>
            {
                RandomAction(client, cts.Token);
            }, cts.Token);
        }

        while (cts.IsCancellationRequested == false)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            Debug.WriteLine($"---- connected: {connectedCount}, total message: {messageCount}");
        }
        Debug.WriteLine($"DONE.");
    }

    private static void RandomAction(Client client, CancellationToken ct)
    {
        if (client.IsConnected == false)
        {
            Debug.WriteLine($"Connection closed. Stopping RandomAction");
        }

        var rnd = new Random().Next(30);
        if (rnd == 0)
        {
            client.Close();
            return;
        }
        Task.Run(async () =>
        {
            await Task.Delay(new Random().Next(250));
            await client.SendAsync(new TestMessage1 { Message1 = "testing", Message2 = "testing" });
            RandomAction(client, ct);
        }, ct);
    }
}

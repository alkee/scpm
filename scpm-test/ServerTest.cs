using System.Diagnostics;
using Google.Protobuf;
using scpm.Net;

namespace scpm_test;

public class ServerTest
{
    private const int TEST_PORT = 4684;

    [Fact]
    public async Task TestStart()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var server = new Server(TEST_PORT);
        await server.StartAsync(cts.Token);
    }

    [Fact]
    public async Task TestEvent()
    { // TODO: Task.Delay 대신 실제 완료 확인방식 강구
        var cts = new CancellationTokenSource();
        var server = new Server(TEST_PORT);
        bool handshaked = false;
        server.Handshaked += (c) =>
        {
            handshaked = true;
        };
        bool closed = false;
        server.Closed += (c) =>
        {
            closed = true;
        };
        IMessage? message = null;
        server.MessageReceived += (c, m) =>
        {
            message = m;
        };
        Assert.False(handshaked);
        Assert.False(closed);
        Assert.Null(message);
        _ = server.StartAsync(cts.Token);

        var client = new Client();
        await client.ConnectAsync("localhost", TEST_PORT, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1)); // wait for server process
        Assert.True(handshaked);
        await client.SendAsync(new TestMessage1 { }, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1)); // wait for server process
        Assert.NotNull(message);

        client.Close();
        await Task.Delay(TimeSpan.FromSeconds(1)); // wait for server process
        Assert.True(closed);

        cts.Cancel();
    }
}

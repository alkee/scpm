using Google.Protobuf;
using scpm.Net;

namespace scpm_test;

public class ServerClientTest
{
    // 각 TestClass 별로 서로다른 port 를 사용하도록 해야 동시 테스트가 가능
    private const int TEST_PORT = 4684;

    [Fact]
    public async Task TestStart()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var server = new Server(TEST_PORT);
        await server.StartAsync(cts.Token);
    }

    [Fact]
    public async Task TestTimeout()
    {
        const float TIMEOUT_SECONDS = 1.0f;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new Server(TEST_PORT, TimeSpan.FromSeconds(TIMEOUT_SECONDS));
        var closed = false;
        server.Closed += (c) => { closed = true; };
        _ = server.StartAsync(cts.Token);
        var client = new Client();
        Assert.False(client.IsConnected);
        await client.ConnectAsync("localhost", TEST_PORT);
        Assert.True(client.IsConnected);
        Assert.False(closed);
        await Task.Delay(TimeSpan.FromSeconds(TIMEOUT_SECONDS - 0.2f));
        await client.SendAsync(new TestMessage1 { });
        await Task.Delay(TimeSpan.FromSeconds(TIMEOUT_SECONDS - 0.2f));
        await client.SendAsync(new TestMessage2 { });
        Assert.False(closed); // 아직까지는 연결유지

        _ = client.ReadMessageAsync(cts.Token); // client 쪽에서 close 감지를 위해
        await Task.Delay(TimeSpan.FromSeconds(TIMEOUT_SECONDS + 0.5f));
        Assert.True(closed);
        Assert.False(client.IsConnected);
        await cts.CancelAsync();
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

        await cts.CancelAsync();
    }
}

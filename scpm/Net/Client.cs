using System.Net.Sockets;

using Google.Protobuf;

namespace scpm.Net;

public class Client
{
    public bool IsConnected => channel != null;

    private readonly TcpClient client;
    private Channel? channel = null;
    private static readonly Handshaker handshaker = new ClientHandshaker();

    public Client()
    {
        this.client = new();
    }

    public async Task ConnectAsync(string host, int tcpPort, CancellationToken ct = default)
    {
        await client.ConnectAsync(host, tcpPort, ct);
        var crpytor = await handshaker.HandshakeAsync(client.GetStream(), ct);
        channel = new Channel(client, crpytor);
    }

    public void Close()
    {
        channel?.Close();
    }

    public async Task<IMessage> ReadMessageAsync(CancellationToken ct)
    {
        if (channel == null)
            throw new InvalidOperationException($"Not connected");
        return await channel.ReadMessageAsync(ct);
    }

    public async Task SendAsync(IMessage message, CancellationToken ct = default)
    {
        if (channel == null)
            throw new InvalidOperationException($"Not connected");
        await channel.SendAsync(message, ct);
    }
}

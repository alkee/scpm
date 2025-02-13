using System.Diagnostics;
using System.Net.Sockets;

using Google.Protobuf;

namespace scpm.Net;

public class Client
{
    public event Action<Channel> Connected = delegate { };
    public event Action<Channel, Exception> Disconnected = delegate { };
    /// False before the security has handshaked
    public bool IsConnected { get; private set; } = false;

    private readonly TcpClient client;
    private readonly MessageDispatcher<Channel> dispatcher;
    private Channel? channel = null;

    public Client(MessageDispatcher<Channel> dispatcher)
    {
        this.dispatcher = dispatcher;
        this.client = new();
    }

    public Client(TcpClient connectedClient, MessageDispatcher<Channel> dispatcher)
    {
        this.channel = new(connectedClient, dispatcher);
        this.dispatcher = dispatcher;
        this.client = connectedClient;
    }

    public async Task SendAsync(IMessage message)
    {
        if (IsConnected == false || channel == null)
            throw new InvalidOperationException($"unable to send before handshake");
        await channel.SendAsync(message);
    }

    public async Task BeginAsync(string host, int tcpPort, CancellationToken ct = default)
    {
        IsConnected = false;
        Debug.WriteLine($"{GetType()} connecting: {host}:{tcpPort}");
        await client.ConnectAsync(host, tcpPort);
        if (client.Connected == false) return;
        channel = new Channel(client, dispatcher);
        channel.Closed += (channel, exception) =>
        {
            Debug.WriteLine($"[{channel.ID}] closed. error: {exception}");
            IsConnected = false;
            Disconnected(channel, exception);
        };
        channel.Connected += (channel) =>
        {
            Debug.WriteLine($"[{channel.ID}] handshaked on client");
            IsConnected = true;
            Connected(channel);
        };
        Debug.WriteLine($"[{channel.ID}] connected, now receiving.");
        await channel.BeginReceiveAsync(false, ct);
    }

    public void Close()
    {
        channel?.Close();
    }
}

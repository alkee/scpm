using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace scpm.Net;

public class Server
{
    public event Action<Channel> Connected = delegate { };
    public event Action<Channel> Closed = delegate { };

    private readonly int tcpPort;
    private readonly MessageDispatcher<Channel> dispatcher;
    private readonly List<Channel> channels = [];

    public bool IsListening { get; private set; } = false;

    public Server(MessageDispatcher<Channel> dispatcher, int tcpPort)
    {
        this.tcpPort = tcpPort;
        this.dispatcher = dispatcher;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Any, tcpPort);
        listener.Start();

        using var releaser = Defer.Create(() =>
        {
            foreach (var channel in channels)
                channel.Close();
            channels.Clear();
            IsListening = false;
        });

        IsListening = true;
        Debug.WriteLine($"{GetType()} listening: {tcpPort}");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = await listener.AcceptTcpClientAsync(ct);
            var channel = new Channel(client, dispatcher);
            Debug.WriteLine($"[{channel.ID}] Accepted");
            channel.Connected += (channel) =>
            {
                Debug.WriteLine($"[{channel.ID}] handshaked on server");
                Connected(channel);
            };
            channel.Closed += (channel, exception) =>
            {
                Debug.WriteLine($"[{channel.ID}] connection closed. error: {exception}");
                lock (channels) channels.Remove(channel);
                Closed(channel);
            };
            lock (channels) channels.Add(channel);
            channel.SetDispatcher(dispatcher);
            _ = channel.BeginReceiveAsync(true, ct);
        }
    }

}

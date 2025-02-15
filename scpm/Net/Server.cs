using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace scpm.Net;

public class Server
{
    public event Action<IChannel> Handshaked = delegate { };
    public event Action<IChannel, IMessage> MessageReceived = delegate { };
    public event Action<IChannel> Closed = delegate { };

    private readonly int tcpPort;
    private static readonly Handshaker handshaker = new ServerHandshaker();

    public Server(int tcpPort)
    {
        this.tcpPort = tcpPort;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Any, tcpPort);
        listener.Start();
        Debug.WriteLine($"{GetType()} listening: {tcpPort}");

        try
        {
            while (ct.IsCancellationRequested == false)
            {
                ct.ThrowIfCancellationRequested();
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = KeepReceivingMessage(client, ct);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error on accepting: {e}");
            return;
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    private async Task KeepReceivingMessage(TcpClient client, CancellationToken ct)
    {
        IChannel? c = null;
        try
        {
            var cryptor = await handshaker.HandshakeAsync(client.GetStream(), ct);
            var channel = new Channel(client, cryptor);
            c = channel;
            var available = client.GetStream().DataAvailable;
            Handshaked(channel);
            while (channel.IsConnected && ct.IsCancellationRequested == false)
            {
                ct.ThrowIfCancellationRequested();
                var message = await channel.ReadMessageAsync(ct);
                MessageReceived(channel, message);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error: {e}");
#if DEBUG
            throw;
#else
            return;
#endif
        }
        finally
        {
            if (c != null) Closed(c);
            client.Dispose();
        }
    }
}

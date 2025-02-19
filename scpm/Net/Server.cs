using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace scpm.Net;

/// <summary>
///     TCP listener(acceptor) for Protobuf message communication
/// </summary>
public class Server
{
    public event Action<IChannel> Handshaked = delegate { };
    public event Action<IChannel, IMessage> MessageReceived = delegate { };
    public event Action<IChannel> Closed = delegate { };

    private readonly int tcpPort;
    private readonly TimeSpan messageTimeout;
    private static readonly Handshaker handshaker = new ServerHandshaker();

    public Server(int tcpPort, TimeSpan messageTimeout)
    {
        this.tcpPort = tcpPort;
        this.messageTimeout = messageTimeout;
    }

    public Server(int tcpPort)
        : this(tcpPort, TimeSpan.MaxValue) // infinity timeout
    {
    }

    public async Task StartAsync(CancellationToken ct)
    {
#if NETSTANDARD
        var listener = new TcpListener(IPAddress.Any, tcpPort);
#else
        using var listener = new TcpListener(IPAddress.Any, tcpPort);
#endif
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
        }
    }

    private CancellationTokenSource CreateMessageTimeoutTokenSource()
    {
        return messageTimeout == TimeSpan.MaxValue
            ? new CancellationTokenSource()
            : new CancellationTokenSource(messageTimeout);
    }

    private async Task KeepReceivingMessage(TcpClient client, CancellationToken ct)
    {
        Debug.Assert(client.Connected);
        Channel? channel = null; // try/using scope 밖에서도 정보를 얻기 위해
        try
        {
            using (var messageTimeoutCts = CreateMessageTimeoutTokenSource())
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(messageTimeoutCts.Token, ct);
                var cryptor = await handshaker.HandshakeAsync(client.GetStream(), cts.Token);
                channel = new Channel(client, cryptor);
            }
            var available = client.GetStream().DataAvailable;
            Handshaked(channel);
            while (channel.IsConnected && ct.IsCancellationRequested == false)
            {
                using var messageTimeoutCts = CreateMessageTimeoutTokenSource();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(messageTimeoutCts.Token, ct);
                var message = await channel.ReadMessageAsync(cts.Token);
                MessageReceived(channel, message);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Failed to receive message: {e}");
#if DEBUG
            throw;
#else
            return;
#endif
        }
        finally
        {
            if (channel != null) Closed(channel);
            client.Close();
            client.Dispose();
        }
    }
}

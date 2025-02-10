using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using scpm.handshake; // proto messsages

namespace scpm.working;


public class MessageHandlerAttribute : Attribute { }

public class MessageDispatcher
{
    public MessageDispatcher(object container)
    {
        var count = AddContainer(container);
        // if (count == 0)
        //     throw new ApplicationException($"container has no handler : {container.GetType()}");
    }

    public int Dispatch(IMessage message)
    {
        return 0; // 몇개 handler ?
    }

    // public int Count<T>(Action<IScpmChannel, T> handler) where T : IMessage
    // {
    //     return 0;
    // }
    // public int Count<T>() where T : IMessage
    // {
    //     return 0;
    // }

    public int Add<T>(Action<Channel, T> handler) where T : IMessage
    { // returns handler 개수 : 중복된 handler 의 경우 1 보다 큰 값.
        return 0;
    }

    public int Remove<T>(Action<Channel, T> handler) where T : IMessage
    { // returns handler 개수 : 중복된 handler 의 경우 1 보다 큰 값.
        return 0;
    }

    public int AddContainer(object handlerContainer)
    { // returns 등록된 handler 개수
        return 0;
    }

    public int RemoveContainer(object handlerContainer)
    { // returns 제거된 handler 개수
        return 0;
    }
}


internal class Handshaker
{
    private const string HANDSHAKE_VERSION = "1.0.0";
    private readonly NetworkStream stream;

    public Handshaker(NetworkStream stream)
    {
        this.stream = stream;
    }

    public async Task<Cryptor> HandshakeAsClientAsync(CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20];
        var plain = new Cryptor();
        var whoareyou = await Channel.ReadMessageAsync<WhoAreYou>(stream, buffer, plain, ct);
        // TODO: validate whoareyou.Version
        using var rsa = new RSACryptor(whoareyou.PublicKey);
        var aes = new AESCryptor(); // TODO: whoareyou 의 정보 일부를 이용해 iv 또는 key 설정
        await Channel.SendMessageAsync(stream, new WhoIAm
        {
            Version = HANDSHAKE_VERSION,
            Key = aes.KeyBase64,
            Iv = aes.IVBase64
        }, rsa, ct);
        var handshake = await Channel.ReadMessageAsync<Handshake>(stream, buffer, aes, ct);
        // TODO: validate handshake.PublicKey
        return aes;
    }

    public async Task<Cryptor> HandshakeAsServerAsync(CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20];
        var plain = new Cryptor();
        using var rsa = new RSACryptor();
        await Channel.SendMessageAsync(stream, new WhoAreYou // plain text
        {
            Version = HANDSHAKE_VERSION,
            PublicKey = rsa.PublicKeyBase64,
        }, plain, ct);
        var whoiam = await Channel.ReadMessageAsync<WhoIAm>(stream, buffer, rsa, ct);
        // TODO: whoiam.Version validation
        var aes = new AESCryptor(whoiam.Key, whoiam.Iv);
        await Channel.SendMessageAsync(stream, new Handshake
        {
            PublicKey = rsa.PublicKeyBase64
        }, aes, ct);
        return aes;
    }
}

public class Channel
{
    public string ID { get; } = Guid.NewGuid().ToString();

    public Channel(TcpClient connectedClient, MessageDispatcher dispatcher)
    {
        Debug.Assert(connectedClient.Connected);
        this.client = connectedClient;
        this.dispatcher = dispatcher;
    }

    public event Action<Channel> Connected = delegate { };
    public event Action<Channel, Exception> Closed = delegate { };

    private readonly TcpClient client;
    private Cryptor cryptor = new();
    private MessageDispatcher dispatcher;
    // private readonly ConcurrentQueue<IMessage> unhandledMessages = [];


    public void SetCryptor(Cryptor cryptor)
    {
        this.cryptor = cryptor;
    }

    public void SetDispatcher(MessageDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public async Task SendAsync(IMessage message, CancellationToken ct = default)
    {
        try
        {
            await SendMessageAsync(client.GetStream(), message, cryptor, ct);
        }
        catch (Exception e)
        {
            // network error 인 경우 Read 쪽에서 Closed 발생할것이기에 logging 만.
            Debug.WriteLine($"[{ID}] error on send. message:{message.GetType()}, error:{e}");
        }
    }

    public void Close()
    {
        if (client.Connected == false) return;
        client.Close();
    }

    public static async Task SendMessageAsync(
        NetworkStream stream,
        IMessage message,
        Cryptor cryptor,
        CancellationToken ct)
    {
        var messageBytes = ProtoSerializer.Serialize(message);
        var encoded = cryptor.Encrypt(messageBytes);
        var dataSizeBytes = BitConverter.GetBytes(encoded.Length);
        var buffer = new byte[dataSizeBytes.Length + encoded.Length];
        Array.Copy(dataSizeBytes, buffer, dataSizeBytes.Length);
        Array.Copy(encoded, 0, buffer, dataSizeBytes.Length, encoded.Length);
        ct.ThrowIfCancellationRequested();
        await stream.WriteAsync(buffer, ct);
    }

    public static async Task<T> ReadMessageAsync<T>(
        NetworkStream stream, byte[] buffer, Cryptor cryptor, CancellationToken ct)
        where T : class, IMessage
    {
        var message = await ReadMessageAsync(stream, buffer, cryptor, ct);
        return message as T ?? throw new ApplicationException($"failed to deserialize type : {typeof(T)}");
    }

    public static async Task<IMessage> ReadMessageAsync(
        NetworkStream stream, byte[] buffer, Cryptor cryptor, CancellationToken ct)
    {
        var readSize = await stream.ReadAsync(buffer.AsMemory(0, sizeof(Int32)), ct);
        if (readSize == 0)
            throw new ApplicationException("connection closed");
        var messageBlockSize = BitConverter.ToInt32(buffer);
        readSize = await stream.ReadAsync(buffer.AsMemory(0, messageBlockSize), ct);
        var decoded = cryptor.Decrypt(buffer[..readSize]);
        var typeId = BitConverter.ToInt32(decoded);
        if (readSize != messageBlockSize)
            throw new ApplicationException($"invalid readata size of type: {typeId}, size: {readSize} / {messageBlockSize}");
        return ProtoSerializer.Deserialize(decoded)
            ?? throw new ApplicationException($"failed to deserialize. typeid: {BitConverter.ToInt32(buffer)}, size: {readSize}");
    }

    public async Task BeginReceiveAsync(bool handshakeAsServer, CancellationToken ct)
    {
        if (client.Connected == false)
            throw new InvalidOperationException($"not a connected channel : {ID}");
        try
        {
            var handshaker = new Handshaker(client.GetStream());
            cryptor = handshakeAsServer
                ? await handshaker.HandshakeAsServerAsync(ct)
                : await handshaker.HandshakeAsClientAsync(ct);
            Connected(this);
            ct.ThrowIfCancellationRequested();
            await LoopReadAsync(ct);
        }
        catch (Exception e)
        {
            Closed(this, e);
            Close();
        }
    }

    private async Task LoopReadAsync(CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20]; // 안정적인 서비스를 위해 가장 큰 메시지 크기로. (가변인 경우 잘못된 데이터에 의해 위험)
        var stream = client.GetStream();
        while (true)
        {
            var message = await ReadMessageAsync(stream, buffer, cryptor, ct);
            // TODO: 성능을 올리려면 message 를 별도 queue 로 빼고 worker thread 이용
            dispatcher.Dispatch(message);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
    }
}


public class ScmpmServer
{
    public event Action<Channel> Connected = delegate { };
    public event Action<Channel> Closed = delegate { };

    private readonly int tcpPort;
    private readonly MessageDispatcher dispatcher;
    private readonly List<Channel> channels = [];

    public bool IsListening { get; private set; } = false;

    public ScmpmServer(MessageDispatcher dispatcher, int tcpPort)
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

    public void Stop()
    { // close all connection
    }
}


public class ScpmClient
{

    private readonly TcpClient client;
    private readonly MessageDispatcher dispatcher;
    private Channel? channel = null;

    // ScpmClient 에서 Connect 개념은 handshake 가 이루어졌는지를 기준으로.
    public bool IsConnected { get; private set; } = false;

    public event Action<Channel> Connected = delegate { };
    public event Action<Channel, Exception> Disconnected = delegate { };

    public ScpmClient(MessageDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        this.client = new();
    }

    public ScpmClient(TcpClient connectedClient, MessageDispatcher dispatcher)
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

public static class Tester
{
    internal sealed class TestMessageHandler
    {
    }

    public static async Task<bool> Test()
    {
        const int TEST_PORT = 4684;
        var dispatcher = new MessageDispatcher(new TestMessageHandler());
        var server = new ScmpmServer(dispatcher, TEST_PORT);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = server.StartAsync(cts.Token);
        while (server.IsListening == false)
            await Task.Yield();
        var client = new ScpmClient(dispatcher);
        _ = client.BeginAsync("localhost", TEST_PORT, cts.Token);
        while (client.IsConnected == false && cts.IsCancellationRequested == false)
            await Task.Yield();

        cts.Cancel();
        return true;
    }
}
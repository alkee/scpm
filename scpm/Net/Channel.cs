using System.Diagnostics;
using System.Net.Sockets;

using Google.Protobuf;

using scpm.Security;

namespace scpm.Net;


public class Channel
{
    public string ID { get; } = Guid.NewGuid().ToString();

    public Channel(TcpClient connectedClient, MessageDispatcher<Channel> dispatcher)
    {
        Debug.Assert(connectedClient.Connected);
        this.client = connectedClient;
        this.dispatcher = dispatcher;
    }

    public event Action<Channel> Connected = delegate { };
    public event Action<Channel, Exception> Closed = delegate { };

    private Cryptor cryptor = new NullCryptor();
    private readonly TcpClient client;
    private MessageDispatcher<Channel> dispatcher;


    internal void SetCryptor(Cryptor cryptor)
    {
        this.cryptor = cryptor;
    }

    public void SetDispatcher(MessageDispatcher<Channel> dispatcher)
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
            dispatcher.Dispatch(this, message);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
    }

    internal static async Task SendMessageAsync(
        NetworkStream stream,
        IMessage message,
        IEncodable encoder,
        CancellationToken ct)
    {
        var messageBytes = ProtoSerializer.Serialize(message);
        var encoded = encoder.Encode(messageBytes);
        var dataSizeBytes = BitConverter.GetBytes(encoded.Length);
        var buffer = new byte[dataSizeBytes.Length + encoded.Length];
        Array.Copy(dataSizeBytes, buffer, dataSizeBytes.Length);
        Array.Copy(encoded, 0, buffer, dataSizeBytes.Length, encoded.Length);
        ct.ThrowIfCancellationRequested();
        await stream.WriteAsync(buffer, ct);
    }

    internal static async Task<T> ReadMessageAsync<T>(
        NetworkStream stream, byte[] buffer, IDecodable decoder, CancellationToken ct)
        where T : class, IMessage
    {
        var message = await ReadMessageAsync(stream, buffer, decoder, ct);
        return message as T ?? throw new ApplicationException($"failed to deserialize type : {typeof(T)}");
    }

    internal static async Task<IMessage> ReadMessageAsync(
        NetworkStream stream, byte[] buffer, IDecodable decoder, CancellationToken ct)
    {
        var readSize = await stream.ReadAsync(buffer.AsMemory(0, sizeof(Int32)), ct);
        if (readSize == 0)
            throw new ApplicationException("connection closed");
        var messageBlockSize = BitConverter.ToInt32(buffer);
        readSize = await stream.ReadAsync(buffer.AsMemory(0, messageBlockSize), ct);
        var decoded = decoder.Decode(buffer[..readSize]);
        var typeId = BitConverter.ToInt32(decoded);
        if (readSize != messageBlockSize)
            throw new ApplicationException($"invalid readata size of type: {typeId}, size: {readSize} / {messageBlockSize}");
        return ProtoSerializer.Deserialize(decoded)
            ?? throw new ApplicationException($"failed to deserialize. typeid: {BitConverter.ToInt32(buffer)}, size: {readSize}");
    }
}

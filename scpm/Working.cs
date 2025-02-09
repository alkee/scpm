using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using scpm.handshake;

namespace scpm.working;


// public interface IScpmChannel
// {
//     string Id { get; }
//     void SetCryptor(Cryptor cryptor);
//     void SetDispatcher(MessageDispatcher dispatcher);

//     Task SendAsync(IMessage message, CancellationToken ct);
//     // Task SendAsync(IMessage message, Cryptor cryptor);
//     // Task<IMessage> ReadAsync(CancellationToken ct);
//     // Task<IMessage> ReadAsync(Cryptor cryptor);
// }

public class MessageHandlerAttribute : Attribute { }

public class MessageDispatcher
{
    public MessageDispatcher(object container)
    {
        var count = AddContainer(container);
        if (count == 0)
            throw new ApplicationException($"container has no handler : {container.GetType()}");
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
        }, aes, ct);
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
    private readonly ConcurrentQueue<IMessage> unhandledMessages = [];


    public void SetCryptor(Cryptor cryptor)
    {
    }

    public void SetDispatcher(MessageDispatcher dispatcher)
    {
    }

    public async Task SendAsync(IMessage message, CancellationToken ct)
    {
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
        var typeId = BitConverter.ToInt32(buffer);
        if (readSize != messageBlockSize)
            throw new ApplicationException($"invalid readata size of type: {typeId}, size: {readSize} / {messageBlockSize}");
        return ProtoSerializer.Deserialize(buffer)
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
        var dataSize = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();


            // try
            // {
            //     dataSize = await ReadDataAsync(stream, buffer, dataSize, ct);
            // }
            // catch (Exception e)
            // {
            //     Closed(this, e);
            //     throw;
            // }
            // var blocks = SplitMessageBlocks(buffer, out var processed);
            // if (processed == 0) continue;
            // foreach (var block in blocks)
            // {
            //     var message = GetMessage(block.Span, cryptor)
            //         ?? throw new ApplicationException($"invalid message");
            //     unhandledMessages.Enqueue(message);
            // }

            // // 모든 작업이 끝난 이후에 메모리 정리
            // var remain = dataSize - processed;
            // CopyToHeadPosition(buffer, processed, remain);
            // await Task.Yield();
        }
    }

    private async Task<int> ReadDataAsync(NetworkStream stream, byte[] buffer, int offset, CancellationToken ct)
    {
        var receivedSize = await stream.ReadAsync(
            buffer.AsMemory(offset, buffer.Length - offset), ct
        );
        ct.ThrowIfCancellationRequested();
        if (receivedSize == 0)
        {
            Debug.Assert(client.Connected == false);
            throw new ApplicationException("closed by peer");
        }
        offset += receivedSize; // next receive point
        return offset;
    }

    private void CopyToHeadPosition(byte[] buffer, int offset, int length)
    {
        if (offset == 0)
            return;
        if (offset + length > buffer.Length)
            throw new IndexOutOfRangeException();
        Array.Copy(buffer, offset, buffer, 0, length);
    }

    // private List<IMessage> Parse(byte[] buffer, Cryptor crpytor, ref int dataSize)
    // {
    //     var messages = GetMessages(buffer, crpytor, out var processedSize);
    //     if (processedSize == 0) return [];
    //     var remainSize = dataSize - processedSize; // remained data size
    //     if (remainSize > 0) // offset 을 0 으로 옮기기
    //     { // TODO: blockcopy 가 더 성능이 좋으려나?
    //         Array.Copy(buffer, processedSize, buffer, 0, remainSize);
    //     }
    //     dataSize = remainSize;
    //     return messages;
    // }

    private IMessage? GetMessage(ReadOnlySpan<byte> buffer, Cryptor cryptor)
    {
        var decodedBytes = cryptor.Decrypt(buffer.ToArray());
        return ProtoSerializer.Deserialize(decodedBytes.AsSpan());
    }


    // private List<IMessage> GetMessages(Span<byte> buffer, Cryptor cryptor, out int processed)
    // {
    //     var parsed = new List<IMessage>();
    //     processed = 0;
    //     for (var message = GetMessage(buffer, cryptor, out var parsedSize)
    //         ; message != null
    //         ; message = GetMessage(buffer.Slice(processed), cryptor, out parsedSize))
    //     {
    //         processed += parsedSize;
    //         parsed.Add(message);
    //     }
    //     return parsed;
    // }

    private List<ReadOnlyMemory<byte>> SplitMessageBlocks(ReadOnlyMemory<byte> buffer, out int processed)
    { // Span 은 ref struct 여서 type parameter 가 될 수 없어 CS9244
        processed = 0;
        const int SIZE_HEADER_LENGTH = sizeof(int);

        var blocks = new List<ReadOnlyMemory<byte>>();
        while (processed + SIZE_HEADER_LENGTH < buffer.Length)
        {
            var header = buffer.Slice(processed, SIZE_HEADER_LENGTH).Span;
            var contentSize = BitConverter.ToInt32(header);
            var blockSize = SIZE_HEADER_LENGTH + contentSize;
            if (processed + blockSize > buffer.Length)
                return blocks;
            blocks.Add(buffer.Slice(processed + SIZE_HEADER_LENGTH, contentSize));
            processed += blockSize;
        }
        return blocks;
    }

    // private IMessage? GetMessage(Span<byte> buffer, Cryptor cryptor, out int processed)
    // {
    //     processed = 0;
    //     const int SIZE_HEADER_LENGTH = sizeof(int);
    //     // not enough size data ; 첫 4 byte 는 항상 크기 정보
    //     if (buffer.Length <= SIZE_HEADER_LENGTH) return null;
    //     var messageSize = BitConverter.ToInt32(buffer);
    //     if (buffer.Length < messageSize + SIZE_HEADER_LENGTH) return null;

    //     // TODO: 복사(ToArray) 피하기. Decrypt 시 MemoryStream 이 Span 을 지원하지 않아 parameter 로 할 수 없음.
    //     var messageBytes = buffer.Slice(SIZE_HEADER_LENGTH).ToArray();
    //     var decrypted = cryptor.Decrypt(messageBytes);
    //     processed = SIZE_HEADER_LENGTH + messageSize;
    //     return ProtoSerializer.Deserialize(decrypted);
    // }
}


public class ScmpmServer
{
    public event Action<Channel> Connected = delegate { };
    public event Action<Channel> Closed = delegate { };

    private readonly int tcpPort;
    private readonly MessageDispatcher dispatcher;
    private readonly List<Channel> channels = [];

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
        });

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = await listener.AcceptTcpClientAsync(ct);
            var channel = new Channel(client, dispatcher);
            channel.Closed += (channel, exception) =>
            {
                Debug.WriteLine($"connection closed. {channel.ID} : {exception.Message}");
                lock (channels) channels.Remove(channel);
            };
            lock (channels) channels.Add(channel);
            channel.SetDispatcher(dispatcher);
            _ = channel.BeginReceiveAsync(true, ct);
        }
    }

    public void Stop()
    { // close all connection
    }

    // private async Task ReadAsync(TcpClient client, CancellationToken ct)
    // {
    //     using var _ = Defer.Create(() => { channels.Remove(client, out var _); });

    //     var buffer = new byte[1_024 * 20];
    //     var stream = client.GetStream();
    //     var currentDispatcher = new MessageDispatcher(new HandshakeHandler());
    //     var currentCrpytor = new Cryptor();
    //     var dataSize = 0;

    //     while (true)
    //     {
    //         var receivedSize = await stream.ReadAsync(
    //             buffer.AsMemory(dataSize, buffer.Length - dataSize), ct
    //         );
    //         ct.ThrowIfCancellationRequested();
    //         var channel = channels[client];
    //         if (receivedSize == 0)
    //         { // graceful close
    //             if (channel != null) Closed(channel);
    //             return;
    //         }
    //         dataSize += receivedSize; // next receive point
    //         var messages = GetMessages(buffer, currentCrpytor, out var processedSize);
    //         if (processedSize == 0) continue;
    //         var remainSize = dataSize - processedSize; // remained data size
    //         if (remainSize > 0) // offset 을 0 으로 옮기기
    //         { // TODO: blockcopy 가 더 성능이 좋으려나?
    //             Array.Copy(buffer, processedSize, buffer, 0, remainSize);
    //         }
    //         dataSize = remainSize;
    //     }
    // }




}

public class ServerChannel
{
}


public class ScpmClient
{
    public ScpmClient(MessageDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public event Action<ScpmClient> Disconnected = delegate { };

    public async Task SendAsync(IMessage message)
    {
    }

    public async Task<Channel?> ConnectAsync(string host, int port)
    {
        return null;
    }

    private readonly TcpClient client = new();
    private readonly MessageDispatcher dispatcher;
}

public static class Tester
{
    public static async Task<bool> Test()
    {
        await Task.Yield();
        return true;
    }
}
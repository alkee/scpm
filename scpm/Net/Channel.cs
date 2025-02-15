using System.Diagnostics;
using System.Net.Sockets;

using Google.Protobuf;

using scpm.Security;

namespace scpm.Net;

public interface IChannel
    : IIdentifiable
{
    bool IsConnected { get; }
    NetworkStream GetStream();
    void Close();
    Task SendAsync(IMessage message, CancellationToken ct = default);
    Task<IMessage> ReadMessageAsync(CancellationToken ct);
}

internal class Channel
    : SerialObject
    , IChannel
{
    public Channel(TcpClient handshakedClient, Cryptor crpytor)
    {
        Debug.Assert(handshakedClient.Connected);
        this.client = handshakedClient;
        this.cryptor = crpytor;
    }

    private readonly TcpClient client;
    private readonly Cryptor cryptor;
    private readonly byte[] buffer = new byte[1_024 * 20]; // 안정적인 서비스를 위해 가장 큰 메시지 크기로. (가변인 경우 잘못된 데이터에 의해 위험)

    #region IChannel implementation
    public bool IsConnected => client.Connected;

    public NetworkStream GetStream() => client.GetStream();

    public async Task SendAsync(IMessage message, CancellationToken ct)
    {
        await SendMessageAsync(client.GetStream(), message, cryptor, ct);
    }

    public void Close()
    {
        if (client.Connected == false) return;
        client.Close();
    }

    public async Task<IMessage> ReadMessageAsync(CancellationToken ct)
    {
        var stream = client.GetStream();
        return await ReadMessageAsync(stream, buffer, cryptor, ct);
    }
    #endregion IChannel implementation

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

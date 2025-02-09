using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf;
using scpm.handshake;
using tcrp;

namespace scpm;

public class MessageDispatcher
{
    // attribute 를 이용(tcrp)하는 경우, 상황에 따라 동적으로 빼거나 더하기
    // 어려울 수 있어 개별 handler 등록

    public void AddHandler<T>(Action<Session, T> handler) where T : IMessage
    {
        lock (handlers)
        {
            if (handlers.TryGetValue(typeof(T), out var list) == false)
            {
                list = [];
                handlers.Add(typeof(T), list);
            }
            list.Add(new Handler
            {
                Instance = handler.Target,
                Method = handler.Method,
            });
        }
    }

    public void RemoveHandler<T>(Action<Session, T> handler) where T : IMessage
    {
        lock (handlers)
        {
            if (handlers.TryGetValue(typeof(T), out var list) == false)
            {
                return;
            }
            list.RemoveAll(h => h.Instance == handler.Target && h.Method == handler.Method);
        }
    }

    public int Invoke(Session session, IMessage message)
    {
        List<Handler>? list;
        lock (handlers)
        {
            if (handlers.TryGetValue(message.GetType(), out list) == false)
            {
                return 0;
            }
            list = list.ToList(); // copy to prevent deadlock
        }

        foreach (var handler in list)
        {
            handler.Method.Invoke(handler.Instance, [session, message]);
        }
        return list.Count;
    }

    private readonly Dictionary<Type, List<Handler>> handlers = [];

    private class Handler
    {
        public object? Instance; // static 이면 null 일 수 있음
        public required MethodInfo Method;
    }
}


public class Session
    : IDisposable
{
    public string Guid { get; private set; } = System.Guid.NewGuid().ToString();
    public Session(TcpClient channel, MessageDispatcher dispatcher, int readBufferSize = 20480)
    {
        if (channel.Connected == false)
        {
            throw new InvalidOperationException("channel is not connected");
        }
        this.channel = channel;
        this.dispatcher = dispatcher;
        this.readBuffer = new byte[readBufferSize];

        channel.NoDelay = true;
        channel.GetStream().BeginRead(readBuffer, 0, readBuffer.Length, ReadCallback_channel, null);
    }

    public async Task Begin()
    {
        Debug.WriteLine($"{Guid} : handshake completed. {Cryptor.GetType()}");
        await Task.Yield();

        // client - server 공통 초기화
        // TODO: event 가 IO thread(message queue) 에서 불리도록
        Handshaked = true; // Send 함수 사용 가능
        Initialized(this);
    }

    ~Session()
    {
        Dispose();
    }

    public event Action<Session, Exception?> Closed = delegate { };
    public event Action<Session> Initialized = delegate { };
    public bool Handshaked { get; private set; }
    public Cryptor Cryptor { get; set; } = new Cryptor();

    public void Send(IMessage message)
    {
        if (Handshaked == false)
            throw new InvalidOperationException($"not handshaked yet");
        Send(message, Cryptor, channel);
    }

    public void HandshakeSend(IMessage message)
    {
        Send(message, Cryptor, channel);
    }

    private static void Send(IMessage message, Cryptor encoder, TcpClient channel)
    {
        var messageBytes = ProtoSerializer.Serialize(message);

        // encrypt
        var encrypted = encoder.Encrypt(messageBytes);
        var dataSizeBytes = BitConverter.GetBytes(encrypted.Length);

        lock (channel)
        { // 다른 thread 에서의 send 와 섞이지 않도록
            // 암호화 등을 위해 항상 크기 데이터(plain)를 header 로 삽입.
            channel.GetStream().Write(dataSizeBytes);
            channel.GetStream().Write(encrypted);
        }
    }

    public void Dispose()
    {
        channel.Close();
        channel.Dispose();
    }

    private readonly TcpClient channel;
    private readonly MessageDispatcher dispatcher;
    private readonly byte[] readBuffer;
    private int readBufferSize = 0;

    private void ReadCallback_channel(IAsyncResult ar)
    {
#if !DEBUG
        try
        {
#endif
        var readSize = channel.GetStream().EndRead(ar);
        if (readSize < 1)
        {
            Close();
            Closed(this, null); // closed by peer(graceful disconnection)
            return;
        }
        readBufferSize += readSize;
        var offset = 0; // total processed byte size
        for (var processed = GetFirstMessage(readBuffer.AsSpan(offset, readBufferSize - offset), out var message)
            ; processed > 0
            ; processed = GetFirstMessage(readBuffer.AsSpan(offset, readBufferSize - offset), out message))
        {
            offset += processed;
            Debug.Assert(message != null);
            // TODO: message 를 queuing 해서 단일 thread 에서 호출되도록
            //     모든 event 도 같은 thread 에서 호출하도록
            dispatcher.Invoke(this, message);
        }
        var restSize = readBufferSize - offset;
        if (offset > 0 && restSize > 0)
        { // 더 큰 데이터를 받을 수 있도록 비어있는 buffer 앞쪽으로 당기기
            Array.Copy(readBuffer, offset, readBuffer, 0, restSize);
        }
        // keep receiving
        channel
            .GetStream()
            .BeginRead(
                readBuffer,
                restSize,
                readBuffer.Length - restSize,
                ReadCallback_channel,
                null);
#if !DEBUG
        }
        catch (Exception? e)
        // when (
        //         e is InvalidIOException // socket 이 끊어진 경우
        //         || e is BufferOverflowException // buffer 가 모자라는 경우
        //                                         // || e is InvalidProtocolException // 알맞은 protocol(protobuf)을 사용하지 않은 경우
        //         || e is ObjectDisposedException // NetworkStream 이 닫힌 경우(The TcpClient has been closed.)
        //         || e is InvalidOperationException // NetworkStream 이 닫힌 경우(The TcpClient is not connected to a remote host.)
        //         )
        {
            Close();
    Closed(this, e);
}
#endif
    }

    /// buffer 내에 첫번째 message 를 추출해 
    ///
    /// Returns:
    ///     processed bytes size
    private int GetFirstMessage(Span<byte> buffer, out IMessage? message)
    {
        message = null;
        const int SIZE_HEADER_LENGTH = sizeof(int);
        // not enough size data ; 첫 4 byte 는 항상 크기 정보
        if (buffer.Length <= SIZE_HEADER_LENGTH) return 0;
        var messageSize = BitConverter.ToInt32(buffer);
        if (buffer.Length < messageSize + SIZE_HEADER_LENGTH) return 0;

        // TODO: 복사(ToArray) 피하기. Decrypt 시 MemoryStream 이 Span 을 지원하지 않아 parameter 로 할 수 없음.
        var messageBytes = buffer.Slice(SIZE_HEADER_LENGTH).ToArray();
        var decrypted = Cryptor.Decrypt(messageBytes);
        message = ProtoSerializer.Deserialize(decrypted);

        return SIZE_HEADER_LENGTH + messageSize;
    }

    private void Close()
    {
        if (channel.Connected) channel.Close();
    }
}

public class Acceptor
    : IDisposable
{
    public Acceptor(AcceptorConfig cfg, MessageDispatcher dispatcher)
    {
        this.cfg = cfg;
        this.dispatcher = dispatcher;
        this.handshakeDispatcher = cfg.SecureChannel
            ? new HandshakeDispatcher(dispatcher)
            : null;

        listener = new TcpListener(IPAddress.Any, cfg.Port);
    }

    public void Start(int backlog = 100)
    {
        Debug.WriteLine($"server is listening on port : {cfg.Port}");
        listener.Start(backlog);
        listener.BeginAcceptTcpClient(AcceptCallback_listener, null);
    }

    ~Acceptor()
    {
        Dispose();
    }

    public event Action<Session> Connected = delegate { };
    public event Action<Session> Disconnected = delegate { };

    private void AcceptCallback_listener(IAsyncResult ar)
    {
        TcpClient client;
        try
        {
            client = listener.EndAcceptTcpClient(ar);
            var session = new Session(client, dispatcher);
            Debug.WriteLine($"a session accepted : {session.Guid}");
            session.Closed += (s, e) =>
            {
                lock (handshaking) handshaking.Remove(s);
                lock (sessions) sessions.Remove(s);
                Debug.WriteLine($"a session closed by {e?.Message ?? "peer"}: {s.Guid}");
            };
            session.Initialized += (s) =>
            {
                lock (handshaking) handshaking.Remove(s);
                lock (sessions) sessions.Add(s);
                // client 에게 즉시 완료 알리기
                session.HandshakeSend(new Handshake
                {
                    PublicKey = ""
                });
            };

            lock (handshaking) handshaking.Add(session);
            // begin to handshake
            if (handshakeDispatcher == null)
            { // no handshaking(insecure channel)
                _ = session.Begin();
            }
            else
            {
                handshakeDispatcher.HandshakeBegin(session);
            }
        }
        catch (Exception e) when (
            e is ObjectDisposedException
            )
        {
            Debug.WriteLine(e);
            return;
        }

        // keep accepting
        listener.BeginAcceptTcpClient(AcceptCallback_listener, null);
    }

    private readonly AcceptorConfig cfg;
    private readonly TcpListener listener;
    private readonly MessageDispatcher dispatcher;
    private readonly HandshakeDispatcher? handshakeDispatcher;
    private readonly List<Session> handshaking = [];
    private readonly List<Session> sessions = [];

    public void Dispose()
    {
        listener.Stop();
        listener.Dispose();
    }
}
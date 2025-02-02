using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Google.Protobuf;
using tcrp;

namespace scpm;

public class MessageHDispatcher
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
{
    public string Name { get; set; } = string.Empty;
    public Session(TcpClient channel, MessageHDispatcher dispatcher, int readBufferSize = 20480)
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

    public event Action<Session> ClosedByPeer = delegate { };

    public void Send(IMessage message)
    {
        ProtoSerializer.Serialize(message, channel.GetStream());
    }

    private readonly TcpClient channel;
    private readonly MessageHDispatcher dispatcher;
    private readonly byte[] readBuffer;
    private int readBufferSize = 0;

    private void ReadCallback_channel(IAsyncResult ar)
    {
        try
        {
            var readSize = channel.GetStream().EndRead(ar);
            if (readSize < 1)
            {
                ClosedByPeer(this);
                return;
            }
            readBufferSize += readSize;
            var buffer = new MemoryStream(readBuffer, 0, readBufferSize);

            var totalProcessedSize = 0;
            var processedSize = 0;
            for (var message = ProtoSerializer.Deserialize(buffer, out processedSize);
                message != null;
                message = ProtoSerializer.Deserialize(buffer, out processedSize))
            {
                totalProcessedSize += processedSize;
                dispatcher.Invoke(this, message);
            }
            var rest = readBufferSize - totalProcessedSize;
            if (rest >= readBuffer.Length) // overflow
            {
                throw new BufferOverflowException($"read buffer reached maximum capacity : {readSize}/{readBuffer.Length}");
            }
            readBufferSize = rest;
            if (rest > 0 && totalProcessedSize > 0)
            {
                // buffer 앞으로 당겨서 다음에 오는 데이터로 overflow 되지 않도록 한다.
                Array.Copy(readBuffer, totalProcessedSize, readBuffer, 0, rest);
            }
            // keep receiving
            channel.GetStream().BeginRead(readBuffer, rest, readBuffer.Length - rest, ReadCallback_channel, null);
        }
        catch (Exception e) when (
                e is InvalidIOException // socket 이 끊어진 경우
                || e is BufferOverflowException // buffer 가 모자라는 경우
                                                // || e is InvalidProtocolException // 알맞은 protocol(protobuf)을 사용하지 않은 경우
                || e is ObjectDisposedException // NetworkStream 이 닫힌 경우(The TcpClient has been closed.)
                || e is InvalidOperationException // NetworkStream 이 닫힌 경우(The TcpClient is not connected to a remote host.)
                )
        {
            ClosedByPeer(this);
            return;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return;
        }
    }
}

public class Acceptor
    : IDisposable
{
    public Acceptor(int port, MessageHDispatcher dispatcher)
    {
        listener = new TcpListener(IPAddress.Any, port);
        this.dispatcher = dispatcher;
    }

    public void Start(int backlog = 100)
    {
        listener.Start(backlog);
        listener.BeginAcceptTcpClient(AcceptCallback_listener, null);
    }

    ~Acceptor()
    {
        Dispose();
    }

    private void AcceptCallback_listener(IAsyncResult ar)
    {
        Debug.WriteLine($"accepting : {ar.CompletedSynchronously}, {ar.IsCompleted}");
        TcpClient client;
        try
        {
            client = listener.EndAcceptTcpClient(ar);
            var session = new Session(client, dispatcher);
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

    private readonly TcpListener listener;
    private readonly MessageHDispatcher dispatcher;
    private readonly List<Session> sessions = [];

    public void Dispose()
    {
        listener.Stop();
        listener.Dispose();
    }
}
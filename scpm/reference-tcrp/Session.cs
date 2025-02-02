#nullable disable

using System.Net.Sockets;
using System;

namespace tcrp;

public abstract class SerialNumbered
{
    private static int serial = 0;
    public int Serial { get; private set; }
    public SerialNumbered()
    {
        Serial = Interlocked.Increment(ref serial);
    }
}

public class Session : SerialNumbered
{
    public const int INITIAL_READ_BUFFER_SIZE = 2048;
    public const int MAXIMUM_READ_BUFFER_SIZE = 1048576; // 1MB

    public string RemoteAddress { get; private set; }

    public delegate void CloseHandler(Session session, bool closedByPeer);
    public delegate void MessageHandler(Session session, object message);

    public event CloseHandler Closed; // io thread 에서 불린다.
    public event MessageHandler MessageArrived; // io thread 에서 불린다.

    public Session()
    {
        MessageDispatcher = new MessageDispatcher();
    }

    public Session(TcpClient channel)
        : this()
    {
        BindConnectedChannel(channel);
    }

    public object UserData { get; set; } // 여기에 서비스에 사용하는 데이터들을 넣어서 서로 참조하기 쉽도록. generic 하게하면 코드가 너무...

    public void Send(object message) // packet 은 protobuf attribute 가진 것. IExtensible 로 parameter 받기엔.. 좀...
    {
        if (channel == null || channel.Connected == false) return;

        var writeBuffer = new MemoryStream(); // stack 을 사용할 수 있으면 부담이 적을 텐데..
                                              // hash 를 사용(큰 값)하므로 PrefixStyle.Base128 보다 Fixed32 크기가 더 작다.
                                              // 하지만 WithLengthPrefix 함수에 type tag 를 사용하기 위해서는 반드시 PrefixStyle.Base128 만 사용할 수 있다.
                                              // https://github.com/mgravell/protobuf-net/blob/master/protobuf-net/Meta/TypeModel.cs#L322
        var messageId = ProtobufMessage.TypeToId(message.GetType());
        ProtoBuf.Serializer.NonGeneric.SerializeWithLengthPrefix(writeBuffer, message, ProtoBuf.PrefixStyle.Base128, messageId);
        lock (sendingBuffers)
        {
            bool starting = sendingBuffers.Count == 0; // 이미 buffer 에 데이터가 있는 경우, callback 에서 비워질때까지 send 할 것이다
            sendingBuffers.Enqueue(writeBuffer);
            if (starting) Channel_SendCallback(null);
        }
    }

    public MessageDispatcher MessageDispatcher { get; private set; }

    public void Close()
    {
        if (channel == null) return;

        bool hadConnected = channel.Connected;
        if (hadConnected) channel.Close();
        if (Closed != null)
        {
            Closed(this, !hadConnected);
        }
        channel = null; // 두번 이상 호출 방지.
    }

    public void BeginRead()
    {
        if (channel == null) throw new InvalidOperationException("channel should not be null");
        if (channel.Connected == false) throw new InvalidOperationException("channel should be connected before BeginRead");
        channel.GetStream().BeginRead(readBuffer, 0, readBuffer.Length, Channel_ReadCallback, null); // 받기 시작
    }

    protected void BindConnectedChannel(TcpClient channel)
    {
        if (channel.Connected == false) throw new ArgumentException("should be connected", "channel");
        this.channel = channel;
        RemoteAddress = channel.Client.RemoteEndPoint.ToString();
    }

    private void Channel_ReadCallback(IAsyncResult ar)
    {
        if (channel == null) return;
        try
        {
            var stream = channel.GetStream();
            var appendedBytes = stream.EndRead(ar);
            if (appendedBytes < 1)
            {
                Close(); // closed by peer
                return;
            }

            readBytes += appendedBytes;

            int processed = (int)RaiseAllCallback(readBuffer, readBytes, this);

            readBytes -= processed; // 처리하고 남은 데이터가 있으면

            if (readBytes == readBuffer.Length) // overflow - 더이상 받을 공간이 없다.
            {
                if (readBuffer.Length >= MAXIMUM_READ_BUFFER_SIZE) // 그래도 너무 커지면 문제
                {
                    throw new BufferOverflowException("read buffer reached maximum capacity : " + readBuffer.Length.ToString());
                }
                var old = readBuffer;
                readBuffer = new byte[readBuffer.Length + INITIAL_READ_BUFFER_SIZE]; // INITIAL_READ_BUFFER_SIZE 만큼씩 증가
                Array.Copy(old, readBuffer, old.Length);
            }
            else
            {
                Array.Copy(readBuffer, processed, readBuffer, 0, readBytes); // buffer 앞으로 당겨서 다음에 오는 데이터로 overflow 되지 않도록 한다.
            }

            stream.BeginRead(readBuffer, readBytes, readBuffer.Length - readBytes, Channel_ReadCallback, null); // keep receiving
        }
        catch (Exception ex)
        {
            // C# 6 이라면 exception filter 사용하면 좀 더 나을 것 같은데..
            if (ex is InvalidIOException // socket 이 끊어진 경우
                || ex is BufferOverflowException // buffer 가 모자라는 경우
                || ex is InvalidProtocolException // 알맞은 protocol(protobuf)을 사용하지 않은 경우
                || ex is ObjectDisposedException // NetworkStream 이 닫힌 경우(The TcpClient has been closed.)
                || ex is InvalidOperationException // NetworkStream 이 닫힌 경우(The TcpClient is not connected to a remote host.)
                )
            {
                // TODO: 추가 로그
                Close();
            }
        }
    }

    private void Channel_SendCallback(IAsyncResult ar)
    {
        var stream = channel.GetStream();
        if (ar != null) // Send 함수로부터의 호출이 아닌 경우. 비직관적일 수 있지만, 코드가 더 간결하다.
            stream.EndWrite(ar);
        lock (sendingBuffers)
        {
            if (sendingBuffers.Count == 0) return;
            var msg = sendingBuffers.Dequeue();
            try
            {
                stream.BeginWrite(msg.GetBuffer(), 0, (int)msg.Length, Channel_SendCallback, null);
            }
            catch (ObjectDisposedException) // NetworkStream 이 닫힌 경우
            {
                Close();
            }
            catch (IOException) // 네트워크 오류 또는 socket 오류(inner SocketException)
            {
                Close();
            }
        }
    }

    private TcpClient channel;
    private byte[] readBuffer = new byte[INITIAL_READ_BUFFER_SIZE];
    private int readBytes;
    private Queue<MemoryStream> sendingBuffers = new Queue<MemoryStream>();

    #region static members

    // returns processed bytes
    // throws ProtocolNotFoundException : 해당 메시지로 등록된 service 가 없는경우
    private static long RaiseCallback(Stream stream, Session session)
    {
        var message = ProtobufMessage.Deserialize(stream);
        if (message == null) return 0;
        if (session.MessageArrived != null) session.MessageArrived(session, message);
        return stream.Position;
    }

    private static long RaiseAllCallback(byte[] buffer, int bufferEndPoision, Session session)
    {
        using (var partialStream = new MemoryStream(buffer, 0, bufferEndPoision))
        {
            while (RaiseCallback(partialStream, session) > 0) ;
            return partialStream.Position;
        }
    }
    #endregion
}
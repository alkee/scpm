#nullable disable
using System.Net.Sockets;
using System.Net;

namespace tcrp;

public class Acceptor : IDisposable
{
    public Acceptor()
    {
    }

    ~Acceptor()
    {
        Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    public void Start(int listeningPort)
    {
        Start(listeningPort, Environment.ProcessorCount);
    }

    public bool Running { get; private set; }

    public void Start(int listeningPort, int numberOfThread)
    {
        // TODO: Start 이후에 초기화되는 변수들의 참조 오류(Stop 이후 혹은 Start 전에 호출되는 경우)들 개선
        listener = new TcpListener(IPAddress.Any, listeningPort);
        messageHandler = new MessageHandler(numberOfThread);
        groups = new Dictionary<Session, Group>();
        sessions = new HashSet<Session>();

        Running = true;
        listener.Start(); // TODO: backlog 셋팅

        listener.BeginAcceptSocket(listener_AcceptCallback, listener);
    }

    public void Stop()
    {
        Running = false;
        if (listener != null) listener.Stop();
        if (messageHandler != null) messageHandler.Dispose();
    }

    public bool ShouldPollManually { get { return messageHandler.NumberOfWorkerThread == 0; } }
    // returns 처리하고 남은 queue 개수
    public int Poll()
    {
        if (messageHandler == null) throw new InvalidOperationException("should be called after start");
        return messageHandler.Dequeue();
    }

    public event Action<Session> Connected; // 정확히는 Accepted 가 맞지만, 조금 더 쉬운 접근을 위해 Connected 라는 이름 사용
    public event Action<Session> Disconnected;

    private void listener_AcceptCallback(IAsyncResult ar)
    {
        var listener = ar.AsyncState as TcpListener;
        if (Running == false) // listener 가 stop 될때도 callback 이 발생하고 
            return; // EndAcceptTcpClient 에서 이 ObjectDisposedException 이 발생한다.

        var client = listener.EndAcceptTcpClient(ar);
        var session = new Session(client);
        lock (sessions) sessions.Add(session);
        listener.BeginAcceptSocket(listener_AcceptCallback, listener); // keep accepting
        if (Connected != null)
        {
            session.Closed += Session_Closed;
            session.MessageArrived += Session_MessageArrived;
            Connected(session);
        }
        session.BeginRead();
    }

    private void Session_Closed(Session session, bool closedByPeer)
    {
        lock (sessions) sessions.Remove(session);
        if (Disconnected != null) Disconnected(session);
    }

    private void Session_MessageArrived(Session session, object message)
    {
#if DEBUG || UNITY_EDITOR
            if (session.MessageDispatcher.HasDispatcher(message.GetType()) == false)
                throw new ProtocolNotFoundException(message.GetType().Name + " has no dispatcher of services");
#endif

        session.MessageDispatcher.InvokeUnsafe(session, message); // UnsafeMessageDispatcher 는 io thread 에서 바로 불리도록
        Group group = null;
        lock (groups) groups.TryGetValue(session, out group);
        var key = GetDistributionKey(session, group);
        messageHandler.Enqueue(session, message, key); // 그렇지 않으면 순차적으로 호출되도록 하기위해 enqueue
    }

    private static int GetDistributionKey(Session session, Group group)
    {
        return group == null ? session.Serial : group.Serial;
    }

    private TcpListener listener;
    private MessageHandler messageHandler;

    #region group managable

    public void RemoveGroup(Session session)
    {
        var groupBefore = InternalRemoveGroup(session);
        if (groupBefore != null) GroupChanged(session, groupBefore, null);
    }

    public void SetGroup(Session member, Session nonmember = null)
    {
        if (member == null) throw new ArgumentNullException("member", "member should not be null");
        Group group = null;
        if (HasGroup(member) == false) group = CreateGroup(member);
        else { lock (groups) group = groups[member]; }
        if (nonmember != null)
        {
            var before = InternalRemoveGroup(nonmember); // 중복 포함된 group 이 발생하는 것을 막기위함
            group.Add(nonmember);
            lock (groups) { groups[nonmember] = group; }
            GroupChanged(nonmember, before, group);
        }
    }

    public bool HasGroup(Session session)
    {
        lock (groups) return groups.ContainsKey(session);
    }

    public void Broadcast(Session member, object message)
    {
        Group g = null;
        lock (groups)
        {
            groups.TryGetValue(member, out g);
        }
        if (g != null) g.Broadcast(message);
    }

    public void BroadcastToAll(object message)
    {
        lock (sessions) foreach (var s in sessions) s.Send(message);
    }

    public List<Session> SelectMembers(Session member)
    {
        if (groups == null) throw new InvalidOperationException("group is not initialized");
        Group g = null;
        lock (groups)
        {
            groups.TryGetValue(member, out g);
        }
        if (g == null) return null;
        return g.SelectMembers();
    }

    // returns 기존에 가입된 group
    private Group InternalRemoveGroup(Session session)
    {
        if (session == null) throw new ArgumentNullException("session", "invalid session"); ;
        if (groups == null) throw new InvalidOperationException("group is not initialized");
        Group group = null;
        lock (groups)
        {
            if (groups.TryGetValue(session, out group))
            {
                group.Remove(session);
                groups.Remove(session);
            }
        }
        return group;
    }

    // returns 새로 가입된 group
    private Group CreateGroup(Session session)
    {
        if (session == null) throw new ArgumentNullException("session", "invalid session");
        if (groups == null) throw new InvalidOperationException("group is not initialized");
        var before = InternalRemoveGroup(session); // making sure. 의도하지 않는 동작(두곳 이상의 group 에 존재)을 사전에 막기 위해
        var group = new Group();
        group.Add(session);
        lock (groups)
        {
            groups[session] = group;
        }
        GroupChanged(session, before, group);
        return group;
    }

    private void GroupChanged(Session session, Group before, Group after)
    {
        var keyBefore = GetDistributionKey(session, before);
        var keyAfter = GetDistributionKey(session, after);
        messageHandler.RearrangeQueue(session, keyBefore, keyAfter);
    }


    private Dictionary<Session, Group> groups;
    private HashSet<Session> sessions;
    #endregion
}

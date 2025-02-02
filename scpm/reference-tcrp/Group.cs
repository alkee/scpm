#nullable disable

namespace tcrp;

public interface IGroupManagable
{
    void RemoveGroup(Session session);
    void SetGroup(Session member, Session nonmember = null);
    bool HasGroup(Session session);
    void Broadcast(Session member, object message);
    void BroadcastToAll(object message);
    List<Session> SelectMembers(Session member);
}

internal class Group : SerialNumbered
{
    private List<Session> members = new List<Session>();
    public int Add(Session session)
    {
        lock (members)
        {
            members.Add(session);
            return members.Count;
        }
    }

    public int Remove(Session session)
    {
        lock (members)
        {
            members.Remove(session);
            return members.Count;
        }
    }

    public void Broadcast(object message)
    {
        lock (members)
        {
            foreach (var s in members) s.Send(message);
        }
    }

    // thread safe 한 clone 된 list 제공
    public List<Session> SelectMembers()
    {
        lock (members) return new List<Session>(members);
    }
}

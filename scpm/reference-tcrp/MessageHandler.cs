#nullable disable

namespace tcrp;

internal class MessageHandler : IDisposable
{
    public MessageHandler(int numberOfThread)
    {
        if (numberOfThread > 0)
        {
            worker = new BackgroundWorker<ReservedMessage>(numberOfThread);
            worker.Dequeued += WorkerCallback;
        }
        else mq = new Queue<ReservedMessage>(); // background thread 가 없는 경우
    }

    public void Dispose()
    {
        if (worker != null) worker.Dispose();
    }

    ~MessageHandler()
    {
        Dispose();
    }

    public int NumberOfWorkerThread { get { return worker == null ? 0 : worker.WorkerCount; } }

    public void Enqueue(Session session, object message, int distributionKey)
    {
        var rm = new ReservedMessage { Session = session, Message = message };
        if (worker != null)
        {
            worker.Enqueue(SelectWorkerIndex(distributionKey), rm);
        }
        else
        {
            lock (mq) mq.Enqueue(rm);
        }
    }

    public void RearrangeQueue(Session session, int distributionKeyBefore, int distributionKeyAfter)
    {
        if (worker == null) return;
        var indexBefore = SelectWorkerIndex(distributionKeyBefore);
        int indexAfter = SelectWorkerIndex(distributionKeyAfter);
        if (indexBefore == indexAfter) return; // 바꿔도 같은 queue

        worker.MoveElements(indexBefore, indexAfter, (m) => m.Session == session);
    }

    public int Dequeue() // makes invoke
    {
        if (worker != null) throw new InvalidOperationException("Dequeue only works in 0 io thread mode");
        ReservedMessage rm;
        int count = 0;
        lock (mq)
        {
            if (mq.Count == 0) return 0;
            rm = mq.Dequeue();
            count = mq.Count;
        }
        rm.Session.MessageDispatcher.Invoke(rm.Session, rm.Message);
        return count;
    }

    private void WorkerCallback(int threadIndex, ReservedMessage msg)
    {
        msg.Session.MessageDispatcher.Invoke(msg.Session, msg.Message);
    }

    private int SelectWorkerIndex(int key)
    {
        return key % worker.WorkerCount; // TODO: 좀 더 좋은 분산 방법?
    }

    private class ReservedMessage
    {
        public Session Session { get; set; }
        public object Message { get; set; }
    }

    private BackgroundWorker<ReservedMessage> worker;
    private Queue<ReservedMessage> mq;
}


public class Swappable<T> where T : class
{
    public Swappable(T reference) { r = reference; }
    public T GetRef() { lock (this) return r; }
    public virtual T Swap(T newReference)
    {
        lock (this)
        {
            var old = r;
            r = newReference;
            return old;
        }
    }
    private T r;
}

// AutoResetEvent 성능이슈(http://blog.teamleadnet.com/2012/02/why-autoresetevent-is-slow-and-how-to.html)로
// 개선된 ResetEvent http://www.liranchen.com/2010/08/reducing-autoresetevents.html
// 특히나 BackgroundWorker 의 경우 중복실행되는 Set(by Enqueue) call 이 많을 것이기 때문에 효과적
public class EconomicResetEvent
{
    private volatile int eventState;
    private AutoResetEvent waitHandle;

    private const int EVENT_SET = 1;
    private const int EVENT_NOT_SET = 2;
    private const int EVENT_ON_WAIT = 3;

    public EconomicResetEvent(bool initialState)
    {
        waitHandle = new AutoResetEvent(initialState);
        eventState = initialState ? EVENT_SET : EVENT_NOT_SET;
    }

    public void WaitOne()
    {
        if (eventState == EVENT_SET && Interlocked.CompareExchange(
            ref eventState, EVENT_NOT_SET, EVENT_SET) == EVENT_SET)
        {
            return;
        }

        if (eventState == EVENT_NOT_SET && Interlocked.CompareExchange(
            ref eventState, EVENT_ON_WAIT, EVENT_NOT_SET) == EVENT_NOT_SET)
        {
            waitHandle.WaitOne();
        }
    }

    public void Set()
    {
        if (eventState == EVENT_NOT_SET && Interlocked.CompareExchange(
            ref eventState, EVENT_SET, EVENT_NOT_SET) == EVENT_NOT_SET)
        {
            return;
        }

        if (eventState == EVENT_ON_WAIT && Interlocked.CompareExchange(
            ref eventState, EVENT_NOT_SET, EVENT_ON_WAIT) == EVENT_ON_WAIT)
        {
            waitHandle.Set();
        }
    }
}


public class BackgroundWorker<T> : IDisposable where T : class
{
    public BackgroundWorker(int numberOfThread)
    {
        contexts = new List<Context>();
        for (var i = 0; i < numberOfThread; ++i)
        {
            var context = new Context { Worker = new Thread(ThreadMain), Index = i };
            context.Worker.Start(context);
            contexts.Add(context);
        }
    }

    public event Action<int/*threadIndex*/, T> Dequeued;

    public int WorkerCount { get { return contexts.Count; } }

    public void Enqueue(int index, T workMessage)
    {
        if (index >= contexts.Count) throw new ArgumentOutOfRangeException("index");
        var c = contexts[index];
        if (c.ShouldTerminate) return; // or throw ? 어차피 queue 에 넣어도 동작할 thread 가 없다.
        var rq = c.ReplacableQueue;
        lock (rq) rq.GetRef().Enqueue(workMessage);
        c.WaitHandle.Set();
    }

    // *주의* 비용이 너무 커 보인다.
    public void MoveElements(int indexBefore, int indexAfter, Predicate<T> condition)
    {
        if (condition == null) throw new ArgumentNullException("condition");
        var before = contexts[indexBefore];
        var removedQueue = new Queue<T>(); // condition 에 맞는 데이터가 제거된 before queue

        lock (before.ReplacableQueue)
        {
            var q = before.ReplacableQueue.GetRef();
            while (q.Count > 0)
            {
                var e = q.Dequeue();
                if (condition(e))
                {
                    Enqueue(indexAfter, e);
                }
                else
                {
                    removedQueue.Enqueue(e);
                }
            }
            before.ReplacableQueue.Swap(removedQueue);
        }
    }

    public void ThreadMain(object conextObj)
    {
        var context = conextObj as Context;
        while (context.ShouldTerminate == false)
        {
            var q = context.ReplacableQueue.Swap(new Queue<T>());
            if (q.Count == 0)
            {
                context.WaitHandle.WaitOne();
            }
            else
            {
                while (q.Count > 0)
                {
                    var w = q.Dequeue();
                    if (Dequeued != null) Dequeued(context.Index, w);
                }
            }
        }
    }

    public void Dispose()
    {
        // 강제로 terminate 하면 남아있는 queue 를 처리할 수 없으므로, 모두 처리하고 종료하도록 signal 전달
        foreach (var c in contexts)
        {
            c.ShouldTerminate = true;
            c.WaitHandle.Set();
        }

        // 하나의 foreach 안에서 signal 전달 및 대기도 할 수 있지만, 미리 signal 을 주면 동시에 thread 가 종료될 수 있으므로
        // 좀 더 빠른 종료를 위해 분리
        foreach (var c in contexts) if (c.Worker.IsAlive) c.Worker.Join();

    }

    private class Context
    {
        public Swappable<Queue<T>> ReplacableQueue = new Swappable<Queue<T>>(new Queue<T>());
        public Thread Worker;
        public int Index;
        public EconomicResetEvent WaitHandle = new EconomicResetEvent(false); // http://www.liranchen.com/2010/08/reducing-autoresetevents.html
        public bool ShouldTerminate = false;
    }

    private List<Context> contexts;
}

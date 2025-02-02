#nullable disable

using System.Reflection;
namespace tcrp;

public class MessageDispatcher
{
    public int AddContainer(object instance)
    {
        if (instance == null) throw new ArgumentNullException("instance");
        var methods = CreateMessageDispatcherList(instance);
        var count = 0;
        foreach (var m in methods)
        {
            var invokable = CreateInvokable(m, instance);
            AddToDispatchers(GetMessageType(m), invokable); // 항상 성공
            AddIndex(instance, invokable);
            ++count;
        }
        return count;
    }

    public int RemoveContainer(object instance)
    {
        if (instance == null) throw new ArgumentNullException("instance");
        List<IInvokable> invokables;
        lock (index)
        {
            if (index.TryGetValue(instance, out invokables) == false) return 0; // nothing to find
            var count = 0;
            foreach (var d in invokables)
            {
                if (RemoveFromDispatchers(GetMessageType(d.Method), d)) ++count;
            }
            return count;
        }
    }

    public bool HasDispatcher(Type messageType)
    {
        List<IInvokable> tmp;
        lock (dispatchers) if (dispatchers.TryGetValue(messageType, out tmp) == true && tmp.Count > 0) return true;
        lock (unsafeDispatchers) if (unsafeDispatchers.TryGetValue(messageType, out tmp) == true && tmp.Count > 0) return true;
        return false;
    }

    public int Invoke(Session session, object message)
    {
        return Invoke(session, message, false);
    }

    public int InvokeUnsafe(Session session, object message)
    {
        return Invoke(session, message, true);
    }

    // returns 실제 실행된 함수 개수
    private int Invoke(Session session, object message, bool targetUnsafe)
    {
        if (session == null) throw new ArgumentNullException("session");
        if (message == null) throw new ArgumentNullException("message");
        var target = targetUnsafe ? unsafeDispatchers : dispatchers;

        var type = message.GetType();
        List<IInvokable> methods;
        lock (target)
        {
            if (target.TryGetValue(type, out methods) == false || methods.Count == 0)
            {
                return 0;
            }
        }

        // TODO: 잠재적인 문제 - 해당 type 을 실행중에 같은 type 에 대한 methods 목록(현재 실행중인 cascade 목록)이 바뀐 경우 이를 처리하지 못한다.
        //   즉, 한번 message 처리 중간에 Add/Remove 되더라도 다음 같은 message 에서 반영이 된다.
        foreach (var d in methods)
        {
            d.Invoke(session, message);
        }
        return methods.Count;
    }

    private void AddToDispatchers(Type type, IInvokable d)
    {
        // thread safety 를 위해 dispatchers 가 변경될 때에는 항상 새로운 element 를 만들어
        // 기존에 참조하고있던 element 를 최대한 유지시켜 lock 이후에 Invoke 시에 문제 없도록
        var target = IsUnsafeDispatcher(d.Method) ? unsafeDispatchers : dispatchers;
        lock (target)
        {
            List<IInvokable> tmp;
            if (target.TryGetValue(type, out tmp) == false)
            {
                tmp = new List<IInvokable>();
                tmp.Add(d);
                target.Add(type, tmp);
                return;
            }
            var after = new List<IInvokable>(tmp);
            after.Add(d);
            target[type] = after;
        }
    }

    private bool RemoveFromDispatchers(Type type, IInvokable d)
    {
        // thread safety 를 위해 dispatchers 가 변경될 때에는 항상 새로운 element 를 만들어
        // 기존에 참조하고있던 element 를 최대한 유지시켜 lock 이후에 Invoke 시에 문제 없도록
        var target = IsUnsafeDispatcher(d.Method) ? unsafeDispatchers : dispatchers;
        lock (target)
        {
            List<IInvokable> tmp;
            if (target.TryGetValue(type, out tmp) == false)
            {
                return false;
            }
            var after = new List<IInvokable>(tmp);
            if (after.Remove(d) == false) return false;
            if (after.Count == 0)
            {
                target.Remove(type);
            }
            else
            {
                target[type] = after;
            }
            return true;
        }
    }

    private void AddIndex(object instance, IInvokable d)
    {
        List<IInvokable> members;
        lock (index)
        {
            if (index.TryGetValue(instance, out members) == false)
            {
                members = new List<IInvokable>();
                index.Add(instance, members);
            }
            members.Add(d);
        }
    }

    private void RemoveIndex(object instance, IInvokable d)
    {
        List<IInvokable> members;
        lock (index)
        {
            if (index.TryGetValue(instance, out members) == false)
            {
                return;
            }
            members.Remove(d);
            if (members.Count == 0) index.Remove(instance);
        }
    }

    private Dictionary<Type, List<IInvokable>> dispatchers = new Dictionary<Type, List<IInvokable>>();
    private Dictionary<Type, List<IInvokable>> unsafeDispatchers = new Dictionary<Type, List<IInvokable>>();
    private Dictionary<object/*instance*/, List<IInvokable>> index = new Dictionary<object, List<IInvokable>>(); // 제거를 위한 정보

    private interface IInvokable
    {
        void Invoke(Session owner, object message);
        MethodInfo Method { get; }
    }

    private class MessageDelegate<T> : IInvokable
    {
        public MessageDelegate(Delegate source)
        {
            original = source;
        }

        public void Invoke(Session owner, object message)
        {
            ((Action<Session, T>)original)(owner, (T)message);
        }

        public MethodInfo Method { get { return original.Method; } }

        private MessageHandler source { get; set; }
        private Delegate original;
    }


    #region internal helpers
    private static IInvokable CreateInvokable(MethodInfo source, object target)
    { // http://stackoverflow.com/questions/940675/getting-a-delegate-from-methodinfo
        Func<Type[], Type> getType;
        bool isAction = source.ReturnType.Equals((typeof(void)));
        var types = source.GetParameters().Select(p => p.ParameterType);

        if (isAction)
        {
            getType = System.Linq.Expressions.Expression.GetActionType;
        }
        else
        {
            getType = System.Linq.Expressions.Expression.GetFuncType;
            types = types.Concat(new[] { source.ReturnType });
        }

        var del = source.IsStatic
            ? Delegate.CreateDelegate(getType(types.ToArray()), source)
            : Delegate.CreateDelegate(getType(types.ToArray()), target, source.Name);

        return CreateInvokable(del, types.ElementAt(1));
    }

    private static IInvokable CreateInvokable(Delegate source, Type messageType)
    {
        var d1 = typeof(MessageDelegate<>);
        Type[] typeArgs = { messageType };
        var constructed = d1.MakeGenericType(typeArgs);
        var instance = Activator.CreateInstance(constructed, source);
        return (IInvokable)instance;
    }

    private static IEnumerable<MethodInfo> CreateMessageDispatcherList(object instance)
    {
        var targetMehtods = from method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            where method.GetCustomAttributes(typeof(MessageDispatcherAttribute), true).Length > 0
                            select method;
        return targetMehtods;
    }

    private static bool IsUnsafeDispatcher(MethodInfo method)
    {
        return method.GetCustomAttributes(typeof(UnsafeMessageDispatcherAttribute), true).Length > 0;
    }

    private static Type GetMessageType(MethodInfo method)
    {
        // public 의 void return 하고 Session, protobuf Message 두개를 parameter 로 갖는 methods 만을 대상으로
        // 이 validation 이 MessageDispatcherAttribute 안으로 들어가면 참 깔끔할 것 같은데..
        if (method.ReturnType != typeof(void))
            throw new InvalidMessageDispatcherException(method.Name + " return type must be void");
        var parameters = method.GetParameters();
        if (parameters.Length != 2)
            throw new InvalidMessageDispatcherException(method.Name + " 2 arguments(Session and Message) required.");
        var sessionParameter = parameters[0];
        var messageParameter = parameters[1];
        if (sessionParameter.ParameterType != typeof(Session))
            throw new InvalidMessageDispatcherException(method.Name + " the first parametrer must be Session type");
        if (messageParameter.ParameterType.GetCustomAttributes(typeof(global::ProtoBuf.ProtoContractAttribute), true).Length == 0)
            throw new InvalidMessageDispatcherException(method.Name + " the second parameter must be protobuf message");
        return messageParameter.ParameterType;
    }
    #endregion
}

// *주의* 같은 group 내에서는 thread-safe 하지만, 서로다른 그룹에 포함된 경우 thread 에 신경써야만 한다.
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public class MessageDispatcherAttribute : Attribute
{
    // attribute 는 단순한 marker/tag 이기떄문에 붙어있는 대상의 정보를 알거나 인터랙션 할 수 없다.
    // 여기서 parameter 등을 validation 할 수 있다면 참 좋을텐데...
}

// IO thread 에서 직접 message 를 받는  method 를 지정. thread safety 를 보장받을 수 없지만, 상당히 가볍고 빠르게 처리된다.
// *주의* TCP 메시지에 의해 호훌되는 함수는 임의의 thread 에서 불려질 수 있다. thread safety 주의해서 구현해야 한다.
public class UnsafeMessageDispatcherAttribute : MessageDispatcherAttribute
{
}

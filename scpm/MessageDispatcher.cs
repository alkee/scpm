using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Google.Protobuf;

namespace scpm;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class MessageHandlerAttribute : Attribute { }

public class MessageDispatcher<T_Sender>
{
    private readonly Dictionary<Type, List<Handler>> handlers = [];

    private class Handler
    {
        public object? Instance; // null if the Method is `static`.
        public required MethodInfo Method;

        public override bool Equals(object? obj)
        {
            if (obj is Handler handler)
            {
                return handler.Instance == Instance && handler.Method == Method;
            }
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return (Instance?.GetHashCode() ?? 0)
                ^ Method.GetHashCode();
        }
    }

    public MessageDispatcher()
    {
    }

    public MessageDispatcher(object container)
    {
        var count = AddContainer(container);
        if (count == 0)
            throw new ApplicationException($"container has no handler : {container.GetType()}");
    }

    public int CountHandlers<T>() where T : IMessage
    {
        lock (handlers)
        {
            if (handlers.TryGetValue(typeof(T), out var list) == false)
            {
                return 0;
            }
            return list.Count;
        }
    }

    public int CountAll()
    {
        lock (handlers)
            return handlers.Values.Sum(x => x.Count);
    }

    public void Add<T>(Action<T_Sender, T> handler) where T : IMessage
    {
        Add(handler.Target, handler.Method);
    }

    public bool Remove<T>(Action<T_Sender, T> handler) where T : IMessage
    {
        return Remove(handler.Target, handler.Method);
    }

    public bool Remove<T>() where T : IMessage
    {
        var t = typeof(T);
        lock (handlers)
        {
            return handlers.Remove(t);
        }
    }

    public void Clear()
    {
        lock (handlers)
            handlers.Clear();
    }

    /// <summary>
    ///     Add handler methods which is MessageHandlerAttribute decorated in given container.
    /// </summary>
    /// <param name="handlerContainer">
    ///     A instance of the container which has MessageHandlerAttribute decorated methods.
    /// </param>
    /// <returns>
    ///     Number of handlers in the container.
    /// </returns>
    public int AddContainer<T>(T handlerContainer) where T : class
    { // returns 등록된 handler 개수
        var t = handlerContainer?.GetType() ?? typeof(T);
        var methodBinding =
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic;
        var handlerMethods = t.GetMethods(methodBinding)
            .Where(m => m.IsDefined(typeof(MessageHandlerAttribute)));
        var count = handlerMethods.Count();
        foreach (var method in handlerMethods)
        {
            T? instance = method.IsStatic
                ? null
                : handlerContainer;
            Add(instance, method);
        }
        return handlerMethods.Count();
    }

    public int RemoveContainer<T>(T handlerContainer)
    { // returns 제거된 handler 개수
        var t = typeof(T);
        var methodBinding =
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic;
        var handlerMethods = t.GetMethods(methodBinding)
            .Where(m => m.IsDefined(typeof(MessageHandlerAttribute)));
        var count = 0;
        foreach (var method in handlerMethods)
        {
            object? instance = method.IsStatic
                ? null
                : handlerContainer;
            var removed = Remove(instance, method);
            count += removed ? 1 : 0;
        }
        return count;
    }

    public int Dispatch(T_Sender sender, IMessage message)
    {
        var t = message.GetType();
        List<Handler> found;
        lock (handlers)
        {
            if (handlers.TryGetValue(t, out var list) == false)
            {
                Debug.WriteLine($"No registered handler for: {t.FullName}");
                return 0;
            }
            found = [.. list];
        }
        foreach (var h in found)
            h.Method.Invoke(h.Instance, [sender, message]);
        return found.Count;
    }

    private void Add<T>(T? instance, MethodInfo method)
    {
        if (method.IsStatic && instance != null)
            throw new ArgumentException($"Static method should not be with instance. Method: {instance.GetType().FullName}.{method.Name}");

        var parameters = method.GetParameters();
        if (parameters.Length != 2
            || parameters[0].ParameterType != typeof(T_Sender)
            || parameters[1].ParameterType.IsAssignableFrom(typeof(IMessage))
        )
            throw new ArgumentException($"Method has invalid parameter. method: ", nameof(method));
        var messageType = parameters[1].ParameterType;
        lock (handlers)
        {
            if (handlers.TryGetValue(messageType, out var list) == false)
            {
                list = [];
                handlers.Add(messageType, list);
            }
            var handler = new Handler
            {
                Instance = instance,
                Method = method,
            };
            list.Add(handler);
        }
        Debug.WriteLine($"Message handler added. method: {messageType.FullName}.{method.Name}");
    }

    private bool Remove(object? instance, MethodInfo method)
    {
        if (method.IsStatic && instance != null)
            throw new ArgumentException($"Static method should not be with instance. Method: {instance.GetType().FullName}.{method.Name}");
        var parameters = method.GetParameters();
        if (parameters.Length != 2
            || parameters[0].ParameterType != typeof(T_Sender)
            || parameters[1].ParameterType.IsAssignableFrom(typeof(IMessage))
        )
            throw new ArgumentException($"Method has invalid parameter. method: ", nameof(method));
        var messageType = parameters[1].ParameterType;
        lock (handlers)
        {
            if (handlers.TryGetValue(messageType, out var list) == false)
            {
                Debug.WriteLine($"Message handler removed. method: {messageType.FullName}.{method.Name}");
                return false;
            }
            var prev = list.Find(h => h.Instance == instance && h.Method == method);
            if (prev == null)
            {
                Debug.WriteLine($"Unable to find the handler to remove. method: {messageType.FullName}.{method.Name}");
                return false;
            }
            Debug.WriteLine($"Removing Message handler. method: {messageType.FullName}.{method.Name}");
            return list.Remove(prev);
        }
    }
}

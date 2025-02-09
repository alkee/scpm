// https://stu.dev/defer-with-csharp8/

namespace scpm;

internal static class Defer
{
    public static DeferDisposable Create(Action action) => new(action);
    public static DeferDisposable<T> Create<T>(Action<T> action, T param1) => new(action, param1);
}

internal readonly struct DeferDisposable : IDisposable
{
    readonly Action _action;
    public DeferDisposable(Action action) => _action = action;
    public void Dispose() => _action.Invoke();
}

internal readonly struct DeferDisposable<T1> : IDisposable
{
    readonly Action<T1> _action;
    readonly T1 _param1;
    public DeferDisposable(Action<T1> action, T1 param1) => (_action, _param1) = (action, param1);
    public void Dispose() => _action.Invoke(_param1);
}

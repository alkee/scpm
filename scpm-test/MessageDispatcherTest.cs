using System.Diagnostics;
using scpm;

namespace scpm_test;

using TestDispatcher = MessageDispatcher<object>;

public class MessageDispatcherTest
{
    private class TestHandler
    {
        internal Type lastMessageType { get; private set; } = typeof(object);

        [MessageHandler]
        public void Handle(object sender, TestMessage1 message)
        {
            lastMessageType = message.GetType();
        }

        [MessageHandler]
        private void Handle(object sender, TestMessage2 message)
        {
            lastMessageType = message.GetType();
        }

        internal void NoAttribute(object sender, TestMessage1 message)
        {
            lastMessageType = message.GetType();
        }

        [MessageHandler]
        private static void StaticHandle(object sender, TestMessage2 message)
        {
            Debug.WriteLine($"{nameof(StaticHandle)} called");
        }
    }

    [Fact]
    public void Test()
    {
        var testDispatcher = new TestDispatcher(new TestHandler());
        Assert.Equal(3, testDispatcher.CountAll());
        Assert.Equal(1, testDispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(2, testDispatcher.CountHandlers<TestMessage2>());
    }

    [Fact]
    public void TestAddRemove()
    {
        var handler = new TestHandler();
        var dispatcher = new TestDispatcher();
        dispatcher.Add<TestMessage1>(handler.NoAttribute);
        Assert.Equal(1, dispatcher.CountHandlers<TestMessage1>());
        dispatcher.Add<TestMessage1>(handler.Handle);
        Assert.Equal(2, dispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(2, dispatcher.CountAll());
        dispatcher.Remove<TestMessage1>(handler.NoAttribute);
        Assert.Equal(1, dispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(1, dispatcher.CountAll());

        dispatcher.Clear();
        Assert.Equal(0, dispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(0, dispatcher.CountAll());
    }

    [Fact]
    public void TestAddRemoveContainer()
    {
        var handler = new TestHandler();
        var dispatcher = new TestDispatcher();
        var added = dispatcher.AddContainer(handler);
        Assert.Equal(3, added);
        Assert.Equal(3, dispatcher.CountAll());
        Assert.Equal(1, dispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(2, dispatcher.CountHandlers<TestMessage2>());

        dispatcher.RemoveContainer(handler);
        Assert.Equal(0, dispatcher.CountAll());
    }

    [Fact]
    public void TestContainerConstructor()
    {
        var handler = new TestHandler();
        var dispatcher = new TestDispatcher(handler);

        Assert.Equal(3, dispatcher.CountAll());
        Assert.Equal(1, dispatcher.CountHandlers<TestMessage1>());
        Assert.Equal(2, dispatcher.CountHandlers<TestMessage2>());
    }

    [Fact]
    public void TestDispatch()
    {
        var handler = new TestHandler();
        var dispatcher = new TestDispatcher(handler);
        dispatcher.Dispatch(this, new TestMessage1
        {
            Message1 = "msg1",
            Message2 = "msg2"
        });
        Assert.Equal(typeof(TestMessage1), handler.lastMessageType);
    }
}

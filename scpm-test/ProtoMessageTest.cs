using System.Net.Sockets;
using scpm;

namespace scpm_test;

public class ProtoMessageTest
{
    [Fact]
    public void Test1()
    {

        Console.WriteLine("-----------------");
    }

    private class TestHandler
    {
        public void TestMessage1(Session session, TestMessage message)
        {
            Console.WriteLine($"{session.Name} : TestMessage1 received");
        }

        public void TestMessage2(Session session, TestMessage2 message)
        {
            Console.WriteLine($"{session.Name} : TestMessage2 received");
            session.Send(new TestMessage
            {
                Message = "test",
                Message2 = "test2",
            });
        }
    }

    [Fact]
    public async Task Test2()
    {
        var testHandler = new TestHandler();
        var handler = new MessageHDispatcher();
        handler.AddHandler<TestMessage>(testHandler.TestMessage1);
        handler.AddHandler<TestMessage2>(testHandler.TestMessage2);

        var acceptor = new Acceptor(1234, handler);
        acceptor.Start();

        var client = new TcpClient();
        client.Connect("localhost", 1234);
        var session = new Session(client, handler);
        session.Name = "client";
        session.Send(new TestMessage2
        {
            Message3 = "msg3",
            Message4 = "msg4",
        });


        await Task.Delay(2000);

        Console.WriteLine("-----------------");
    }
}

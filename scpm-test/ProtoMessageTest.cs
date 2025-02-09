using System.Diagnostics;
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
        public void TestMessage1(Session session, TestMessage1 message)
        {
            Debug.WriteLine($"TestMessage1 received");
        }

        public void TestMessage2(Session session, TestMessage2 message)
        {
            Debug.WriteLine($"TestMessage2 received");
            session.Send(new TestMessage1
            {
                Message1 = "test",
                Message2 = "test2",
            });
        }
    }

    [Fact]
    public async Task InsecureNetworkMessageTest()
    {
        var testHandler = new TestHandler();
        var handler = new MessageDispatcher();
        handler.AddHandler<TestMessage1>(testHandler.TestMessage1);
        handler.AddHandler<TestMessage2>(testHandler.TestMessage2);

        var cfg = new AcceptorConfig
        {
            Port = 1234,
            SecureChannel = false,
        };
        var acceptor = new Acceptor(cfg, handler);
        acceptor.Start();

        var client = new TcpClient();
        client.Connect("localhost", 1234);
        var session = new Session(client, handler);
        session.Initialized += (s) =>
        {
            session.Send(new TestMessage2
            {
                Message3 = "msg3",
                Message4 = "msg4",
            });
        };
        // Assert.Throws<InvalidOperationException>(() =>
        // {
        //     session.Send(new TestMessage2
        //     {
        //         Message3 = "msg3",
        //         Message4 = "msg4",
        //     });
        // });

        await Task.Delay(2000);

        Debug.WriteLine("-----------------");
    }
}

using Google.Protobuf;
using scpm;

namespace scpm_test;

public class ProtobufTest
{
    [Fact]
    public void Test1()
    {
        var m = new TestMessage1
        {
            Message1 = "1",
            Message2 = "2"
        };
        var desc = TestMessage1.Descriptor;

        Console.WriteLine($"index = {desc.Index} / {desc.Name} / {desc.Name.GetHashCode()}");
        var desc2 = TestMessage2.Descriptor;
        Console.WriteLine($"index = {desc2.Index} / {desc2.Name} / {desc2.Name.GetHashCode()}");

        Console.WriteLine("-----------------");
    }
}

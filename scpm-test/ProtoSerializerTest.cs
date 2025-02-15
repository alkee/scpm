using Google.Protobuf;
using scpm;

namespace scpm_test;


public class ProtoSerializerTest
{
    [Fact]
    public void TestEmptyMessage()
    {
        var emptyMessage1 = new TestMessage1 { };
        var emptyMessage1Bytes = ProtoSerializer.Serialize(emptyMessage1);
        Assert.True(emptyMessage1Bytes.Length == sizeof(Int32));
        var emptyMessage1rec = ProtoSerializer.Deserialize(emptyMessage1Bytes);
        Assert.Equal(emptyMessage1, emptyMessage1rec);
    }

    public static readonly TheoryData<IMessage> TestMessages = [
        new TestMessage1 {},
        new TestMessage1 { Message1 = "1", Message2 = "2" }
    ];

    [Theory]
    [MemberData(nameof(TestMessages))]
    public void TestMessage(IMessage message)
    {
        var bytes = ProtoSerializer.Serialize(message);
        var recovered = ProtoSerializer.Deserialize(bytes);
        Assert.Equal(message, recovered);
    }
}
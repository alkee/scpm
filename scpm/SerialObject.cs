namespace scpm;

public interface IIdentifiable
{
    long ID { get; }
}

internal abstract class SerialObject
    : IIdentifiable
{
    private static long lastIssuedSerial = 0;
    private readonly long serialID;

    public SerialObject()
    {
        serialID = Interlocked.Increment(ref lastIssuedSerial);
    }

    public long ID => serialID;
}

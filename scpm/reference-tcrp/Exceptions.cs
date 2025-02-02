#nullable disable

namespace tcrp;

public class InvalidIOException : ApplicationException
{
    public InvalidIOException(string message) : base(message) { }
}

public class InvalidProtocolException : ApplicationException
{
    public InvalidProtocolException(string message) : base(message) { }
}

public class ProtocolNotFoundException : ApplicationException
{
    public ProtocolNotFoundException(string message) : base(message) { }
}

public class BufferOverflowException : ApplicationException // OverflowException 은 SystemException(throw by CLR) 이므로 별도 지정
{
    public BufferOverflowException(string message) : base(message) { }
}

public class InvalidMessageDispatcherException : ApplicationException
{
    public InvalidMessageDispatcherException(string message) : base(message) { }
}

public class InvalidMessageException : ApplicationException
{
    public InvalidMessageException(string message) : base(message) { }
}
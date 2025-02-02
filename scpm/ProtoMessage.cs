using System.Diagnostics;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace scpm;


public static class ProtoSerializer
{
    /// -returns: written size
    public static int Serialize(IMessage source, Stream stream)
    {
        var messageSize = source.CalculateSize();
        var header = new MessageHeader
        {
            MessageSize = messageSize,
            TypeId = MessageHeader.GetTypeId(source.GetType())
        };

        var headerSize = header.Write(stream);
        source.WriteTo(stream);
        return headerSize + messageSize;
    }

    /// -returns: deserialized message or null(when failed)
    /// -exception: ApplicationException(when unknown message type id)
    public static IMessage? Deserialize(Stream source, out int readSize)
    {
        var header = MessageHeader.Read(source, out var headerSize);
        readSize = headerSize;
        if (header.IsEmpty) return null;

        var buffer = new byte[header.MessageSize];
        var messageSize = source.Read(buffer);
        readSize += messageSize;
        if (messageSize != header.MessageSize)
        {
            return null;
        }
        if (protoMessages.TryGetValue(header.TypeId, out MessageDescriptor? descriptor) == false)
        {
            throw new ApplicationException($"unknown message type id: {header.TypeId}");
        }
        return descriptor.Parser.ParseFrom(buffer);
    }



    private static readonly Dictionary<int /*type id*/, MessageDescriptor> protoMessages;
    static ProtoSerializer()
    {
        protoMessages = [];
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsSealed && t.GetInterfaces().Contains(typeof(IMessage)));
            foreach (var t in types)
            {
                var id = MessageHeader.GetTypeId(t);
                var field = t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is not MessageDescriptor descriptor)
                {
                    Debug.WriteLine($"descriptor is null. {t.FullName}");
                    continue;
                }
                if (protoMessages.TryGetValue(id, out MessageDescriptor? prev))
                {
                    throw new ApplicationException($"duplicated id: {id}, type = {t.FullName}, prev = {prev.FullName}");
                }
                protoMessages[id] = descriptor;
                Debug.WriteLine($"type descriptor added. {id} : {t.FullName}");
            }
        }
    }

    private struct MessageHeader
    {
        public int MessageSize;
        public int TypeId;

        public readonly bool IsEmpty => MessageSize == 0 && TypeId == 0;

        public static MessageHeader Read(Stream stream, out int readSize)
        {
            // [ThreadStatic] 을 사용하고자 했었으나, static 사용시 한번만 초기화
            //   되기 때문에 별도 property wrapper 등을 이용해 비교/초기화가 필요
            //   https://stackoverflow.com/a/18086509
            var buffer = new byte[HEADER_SIZE];

            Debug.Assert(buffer is not null);
            readSize = stream.Read(buffer);
            if (readSize != HEADER_SIZE)
            {
                return Empty;
            }
            return new MessageHeader
            {
                MessageSize = BitConverter.ToInt32(buffer),
                TypeId = BitConverter.ToInt32(buffer, sizeof(int))
            };
        }

        public readonly int Write(Stream stream)
        {
            var size = BitConverter.GetBytes(MessageSize);
            stream.Write(size);
            var typeId = BitConverter.GetBytes(TypeId);
            stream.Write(typeId);
            return size.Length + typeId.Length;
        }

        public static MessageHeader Empty => new() { MessageSize = 0, TypeId = 0 };


        private const int HEADER_SIZE = sizeof(int) * 2;

        public static int GetTypeId(Type type)
        {
            return (type.FullName ?? "invalid").GetHashCode();
        }

    }
}

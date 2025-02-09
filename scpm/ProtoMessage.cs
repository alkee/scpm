using System;
using System.Diagnostics;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace scpm;


public static class ProtoSerializer
{
    public static int GetTypeId(IMessage message)
    {
        return (message.GetType().FullName ?? "invalid").GetHashCode();
    }

    public static byte[] Serialize(IMessage source)
    {
        var messageSize = source.CalculateSize();
        var typeId = GetTypeId(source);
        var typeIdBytes = BitConverter.GetBytes(typeId);
        var buffer = new byte[sizeof(int) + messageSize];
        Array.Copy(typeIdBytes, buffer, typeIdBytes.Length);
        source.WriteTo(buffer.AsSpan(sizeof(int)));
        return buffer;
    }

    /// <returns>
    ///     deserialized message or null(when failed)
    /// </returns>
    /// <exception cref="ApplicationException">
    ///     when unknown message type id
    /// </exception>
    public static IMessage? Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length <= sizeof(Int32)) // invalid type information
            return null;
        var typeId = BitConverter.ToInt32(source);
        if (protoMessages.TryGetValue(typeId, out MessageDescriptor? descriptor) == false)
        {
            throw new ApplicationException($"unknown message type id: {typeId}");
        }
        return descriptor.Parser.ParseFrom(source.Slice(sizeof(Int32)));
    }



    // /// -returns: written size
    // public static int Serialize(IMessage source, Stream stream)
    // {
    //     var buffer = Serialize(source);
    //     stream.Write(buffer, 0, buffer.Length);
    //     return buffer.Length;
    // }

    // public static byte[] Serialize(IMessage source)
    // {
    //     var messageSize = source.CalculateSize();
    //     var header = new MessageHeader
    //     {
    //         MessageSize = messageSize,
    //         TypeId = MessageHeader.GetTypeId(source.GetType())
    //     };
    //     var headerBytes = header.GetBytes();
    //     var buffer = new byte[headerBytes.Length + messageSize];
    //     var messageBuffer = buffer.AsSpan(headerBytes.Length);
    //     Array.Copy(headerBytes, buffer, headerBytes.Length);
    //     source.WriteTo(messageBuffer);
    //     return buffer;
    // }

    // /// -returns: deserialized message or null(when failed)
    // /// -exception: ApplicationException(when unknown message type id)
    // public static IMessage? Deserialize(Stream source, out int readSize)
    // {
    //     var header = MessageHeader.ReadFrom(source, out var headerSize);
    //     readSize = headerSize;
    //     if (header.IsEmpty) return null;

    //     var buffer = new byte[header.MessageSize];
    //     var messageSize = source.Read(buffer);
    //     readSize += messageSize;
    //     if (messageSize != header.MessageSize)
    //     {
    //         return null;
    //     }
    //     if (protoMessages.TryGetValue(header.TypeId, out MessageDescriptor? descriptor) == false)
    //     {
    //         throw new ApplicationException($"unknown message type id: {header.TypeId}");
    //     }
    //     return descriptor.Parser.ParseFrom(buffer);
    // }

    // public static IMessage? Deserialize(Span<byte> buffer, out int readSize)
    // {
    //     readSize = 0;
    //     var header = MessageHeader.ReadFrom(buffer);
    //     if (header.IsEmpty) return null;
    //     var requiredSize = header.MessageSize + MessageHeader.HEADER_SIZE;
    //     if (buffer.Length < requiredSize)
    //         return null;

    //     if (protoMessages.TryGetValue(header.TypeId, out MessageDescriptor? descriptor) == false)
    //     {
    //         throw new ApplicationException($"unknown message type id: {header.TypeId}");
    //     }
    //     readSize = requiredSize;
    //     return descriptor.Parser.ParseFrom(buffer);
    // }


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

        public static MessageHeader ReadFrom(Stream stream, out int readSize)
        {
            // MessageHeader? 를 return 하는 경우 null 검사 이후 자동 unwrapping
            //   이 되지 않아 코드가 복잡해져 .Empty 사용

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
            return ReadFrom(buffer, 0);
        }

        public static MessageHeader ReadFrom(byte[] buffer, int offset)
        {
            return ReadFrom(buffer.AsSpan(offset));
        }

        public static MessageHeader ReadFrom(Span<byte> buffer)
        {
            if (buffer.Length < HEADER_SIZE)
            {
                return Empty;
            }
            return new MessageHeader
            {
                MessageSize = BitConverter.ToInt32(buffer),
                TypeId = BitConverter.ToInt32(buffer[sizeof(int)..])
            };
        }


        public readonly int WriteTo(Stream stream)
        {
            var header = GetBytes();
            stream.Write(header, 0, header.Length);
            return header.Length;
        }

        public readonly Byte[] GetBytes()
        {
            var size = BitConverter.GetBytes(MessageSize);
            var typeId = BitConverter.GetBytes(TypeId);
            return size.Concat(typeId).ToArray();
        }

        public static MessageHeader Empty => new() { MessageSize = 0, TypeId = 0 };


        public const int HEADER_SIZE = sizeof(int) * 2;

        public static int GetTypeId(Type type)
        {
            return (type.FullName ?? "invalid").GetHashCode();
        }
    }
}

static class TestExt
{
    static List<int> Destroy(this List<int> list)
    {
        return list;
    }
}
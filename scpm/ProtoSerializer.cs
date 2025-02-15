using System.Diagnostics;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace scpm;

internal static class ProtoSerializer
{
    const int TYPE_ID_SIZE = sizeof(Int32);

    /// <returns>
    ///     Serialized message bytes
    /// </returns>
    public static byte[] Serialize(IMessage source)
    {
        var messageSize = source.CalculateSize();
        var typeId = GetTypeId(source);
        var typeIdBytes = BitConverter.GetBytes(typeId);
        var buffer = new byte[TYPE_ID_SIZE + messageSize];
        Array.Copy(typeIdBytes, buffer, typeIdBytes.Length);
        source.WriteTo(buffer.AsSpan(TYPE_ID_SIZE));
        return buffer;
    }

    /// <returns>
    ///     Deserialized message
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     Insufficient source size
    /// </exception>
    /// <exception cref="ApplicationException">
    ///     When unknown message type id
    /// </exception>
    public static IMessage Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(Int32)) // invalid type information
            throw new ArgumentException($"Not enough size to deserialize", nameof(source));
        var typeId = BitConverter.ToInt32(source);
        if (protoMessages.TryGetValue(typeId, out MessageDescriptor? descriptor) == false)
        {
            throw new ApplicationException($"unknown message type id: {typeId}");
        }
        return descriptor.Parser.ParseFrom(source.Slice(sizeof(Int32)));
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
                var id = GetTypeId(t);
                var field = t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is not MessageDescriptor descriptor)
                {
                    Debug.WriteLine($"Descriptor is null. {t.FullName}");
                    continue;
                }
                if (protoMessages.TryGetValue(id, out MessageDescriptor? prev))
                {
                    throw new ApplicationException($"Duplicated id: {id}, type: {t.FullName}, prev: {prev.FullName}");
                }
                protoMessages[id] = descriptor;
                Debug.WriteLine($"Type descriptor added. {t.FullName} as {id}");
            }
        }
    }

    private static int GetTypeId(IMessage message)
    {
        return GetTypeId(message.GetType());
    }

    private static int GetTypeId(Type type)
    {
        return (type.FullName ?? "invalid").GetHashCode();
    }
}

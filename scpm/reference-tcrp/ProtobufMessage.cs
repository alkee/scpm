#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace tcrp;
internal static class ProtobufMessage
{
    static ProtobufMessage()
    {
        messageTypes = new Dictionary<int, Type>();
        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        // 모든 message(ProtoContract attribute 가진) 타입들을 검색해 types 구성. http://stackoverflow.com/questions/607178/how-enumerate-all-classes-with-custom-class-attribute
        {
            if (type.GetCustomAttributes(typeof(global::ProtoBuf.ProtoContractAttribute), true).Length > 0)
            {
                var messageId = TypeToId(type);
                if (messageTypes.ContainsKey(messageId))
                {
                    var prev = messageTypes[messageId];
                    // 아래 throw 에 대해 [CA1065](https://msdn.microsoft.com/library/bb386039.aspx) 분석결과가 나오지만, 현재로써는 달리 처리할 방법이 없다.
                    throw new InvalidMessageException("message type hash confilct. " + prev.FullName + " with " + type.FullName);
                }
                messageTypes[messageId] = type;
            }
        }
    }

    public static int TypeToId(Type type)
    {
        // protobuf 의 tag number 제약 때문인지 전체크기(int32)의 hash 를 사용하는 경우 이상한 값으로 전달이 된다... why ???
        // 그리고 음수인 경우 serialize 할 때 length 가 1인 stream 만 반환된다(unity 에서만확인)
        return (ushort)type.FullName.GetHashCode();
    }

    public static Type IdToType(int id)
    {
        if (messageTypes.ContainsKey(id) == false) return null;
        return messageTypes[id];
    }

    // throws InvalidProtocolException : protobuf 형식의 데이터가 아닌 경우
    public static object Deserialize(Stream source)
    {
        var startPos = source.Position;
        int fieldHeader;
        int length;
        if (ProtoBuf.Serializer.TryReadLengthPrefix(source, ProtoBuf.PrefixStyle.Base128, out fieldHeader) == false
            || ProtoBuf.Serializer.TryReadLengthPrefix(source, ProtoBuf.PrefixStyle.Base128, out length) == false)
        {
            source.Position = startPos; // 처리되지 않은 데이터는 다음번 deserialize 에 그대로 사용할 수 있도록 position 조정
            return null;
        }

        source.Position = startPos; // 첫 부분부터(fieldheader, length 포함) 읽어야 한다.
        object message;
        if (ProtoBuf.Serializer.NonGeneric.TryDeserializeWithLengthPrefix(source, ProtoBuf.PrefixStyle.Base128, ProtobufMessage.IdToType, out message) == false)
            throw new InvalidProtocolException("unknown protocol - deserializing failed");
        return message;
    }

    private static Dictionary<int, Type> messageTypes;
}

# SCPM

Single Connection Protobuf Messaging : https://github.com/alkee/scpm

## IDEA and Brainstorming

기본적으로 message 는 [type 에 대한 정보를 serialize, deserialize 하지 않음](https://stackoverflow.com/a/9125119). 

[Protobuf-net](https://github.com/protobuf-net/protobuf-net)을 사용하는 [tcrp](https://bitbucket.org/alkee/tcrp) 에서는 string 의 [HashCode 를 이용하는 방식](https://bitbucket.org/alkee/tcrp/src/f8706040b3f73f799a34df030e1c564f77957571/tcrp/ProtobufMessage.cs#lines-30:35)을 사용함

trpc 에서 사용하는 protobuf-net 은 별도의 type (contract)을 생성해야하므로, 
grpc.tools 를 이용해 자동생성된 .proto 관련 message class 를 그대로 이용하도록

```cs
var desc = TestMessage.Descriptor;
Console.WriteLine($"index = {desc.Index} / {desc.Name} / {desc.Name.GetHashCode()}");
```

## history

### dotnet project 구성

```
$ cd scpm/
$ dotnet new classlib
$ cd ../scpm-test/
$ dotnet new xunit
$ dotnet add reference ../scpm/
$ cd ../
$ dotnet new solution
$ dotnet sln add scpm-test/
```

### vscode 의 C# devkit intelisense 문제

C# devkit 은 solution 에 등록된 project 들에서만 intelisense 동작 하는 듯.
test project 가 library project 를 참조하고있기 때문에 solution 에 test
project 만 추가했기 때문에 문제. solution 에 library project 도 추가해 해결.

> `dotnet sln add scpm/`

### C# genric type 추론을 할 수 없음
```cs
public void AddHandler<T>(Action<Session, T> handler) where T : IMessage

...

handler.AddHandler(testHandler.TestMessage1); // error CS0411: The type arguments for method 'MessageHDispatcher.AddHandler<T>(Action<Session, T>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
handler.AddHandler<TestMessage>(testHandler.TestMessage1); // OK
```

와 같이 type 추론이 자동으로 되지 않음.. 왜??? 일단 type 을 직접 지정해 동작하도록..


### NetworkStream 을 그대로 serialize/deserialize 에 사용할 수 없음

C# TCP 동작은 BeginRead 를 통해 buffer 를 제공해 read(NetworkStream) 대기
하고 EndRead 로 모든 데이터를 소비하게되므로, 데이터 일부만 전송되는 경우를
위해 buffer 를 직접 관리해주고, networkstream 으로부터 직접 읽어 사용할 수
없어 MemoryStream 을 사용함. (networkstream 에는 이전에 쌓인 buffer 데이터가 없음)

### stream 을 parameter 로 받고 return 으로 넘겨줄때 Dispose 복잡

> `private static Stream Encrypt(Stream source, byte[] key, byte[] iv)`

와 같은 함수라면 사용하는 쪽에서 using 을 해주어야 하는데, 원본을 그대로
돌려주는 plain encrptyion 같은 경우 원본이 즉시 close 되어 문제가 있을 수 있다.

```cs
    using encStream = Encrypt(networkStream, key, iv);
```

따라서..

> `private static byte[] Encrypt(Span<byte> buffer, byte[] key, byte[] iv)`

와 같이 bytes 정보를 이용하고, 사용하는 쪽에서 필요한 경우 MemoryStream 등을
이용하는것이 낫겠음.

```cs
    using encStream = new MemoryStream(Encrypt(buffer, key, iv));
```

### async/await 방식으로 변경

```cs
var whoareyou = await ReadMessage<WhoAreYou>(stream);
```
과 같은 형태로 handshake 하면 좀 더 직관적일 듯.


### CryptographicException: Padding is invalid and cannot be removed.

```cs
var aes = new AESCryptor();
var handshake = await Channel.ReadMessageAsync<Handshake>(stream, buffer, aes, ct);
```

와 같은 상황에서 발생.

Encepytion 시에 `CryptoStream` 를 사용하는데,

```cs
        using var stream = new CryptoStream(buffer, encryptor, CryptoStreamMode.Write);
        stream.Write(bytes);
        stream.Flush();        
```

이 때 `.Flush()` 를 사용하면 aes padding 을 사용하지 않는 듯 해
`.FlushFinalBlock()` 를 호출하도록 변경해 동작 확인.


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


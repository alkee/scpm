# Histroy

주요 개발 및 문제해결 과정 기록

## IDEA and Brainstorming

기본적으로 message 는 [type 에 대한 정보를 serialize, deserialize 하지 않음](https://stackoverflow.com/a/9125119). 

[Protobuf-net](https://github.com/protobuf-net/protobuf-net)을 사용하는 [tcrp](https://bitbucket.org/alkee/tcrp) 에서는 string 의 [HashCode 를 이용하는 방식](https://bitbucket.org/alkee/tcrp/src/f8706040b3f73f799a34df030e1c564f77957571/tcrp/ProtobufMessage.cs#lines-30:35)을 사용함

trpc 에서 사용하는 protobuf-net 은 별도의 type (contract)을 생성해야하므로, 
grpc.tools 를 이용해 자동생성된 .proto 관련 message class 를 그대로 이용하도록

```cs
var desc = TestMessage.Descriptor;
Console.WriteLine($"index = {desc.Index} / {desc.Name} / {desc.Name.GetHashCode()}");
```

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

### Channel read/write(receive/send) 에 대한 고민

```cs
    public async Task<IMessage> ReadMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20]; // 안정적인 서비스를 위해 가장 큰 메시지 크기로. (가변인 경우 잘못된 데이터에 의해 위험)
        var stream = client.GetStream();
        return await ReadMessageAsync(stream, buffer, cryptor, ct);
    }
```

와 같이 message 를 하나씩 read 하도록 message 도 async-await 으로하고,
exception 이나 null return 으로 closed 를 구현하는 경우.. 
Dispatcher 를 Channel 에서 분리할 수 있고 별도의 event 는 불필요.
server 에서는 별도의 message loop 함수를 connection 마다 생성해
실행하는 방식. Channel 에 HandshakeAsync 를 명시적으로 호출할 수도 있어야..

Server 에서 Channel 을 생성하고 결국 어떤 형태로든 connection 과 연결해 주어야 하니
의존성은 피할 수 없음. 다만 dispatcher 는 의존성을 줄일 수 있을 듯.

다수의 channel event 를 처리해야하는 server 는 어쩔 수 없이 event 방식으로 유지.
Chnnael 및 Client 는 async/await 방식으로 유지.


### message timeout

일정시간동안 client 로부터 message 가 없는 경우 timeout 으로 연결을 끊는 server
기능을 추가. 이전의 cancelation token 과 새로운 timeout cancelation 을 이용하기
위해 [CancellationTokenSource.CreateLinkedTokenSource](https://learn.microsoft.com/ko-kr/dotnet/standard/threading/how-to-listen-for-multiple-cancellation-requests)
사용. `new CancellationTokenSource(TimeSpan.MaxValue)` 를 사용하는 경우 범위초과
exception 이 발생해 기본 생성자를 사용하는 별도 함수 이용.

```cs
    private CancellationTokenSource CreateMessageTimeoutTokenSource()
    {
        return messageTimeout == TimeSpan.MaxValue
            ? new CancellationTokenSource()
            : new CancellationTokenSource(messageTimeout);
    }
```

link 전 timeout `CancelationTokenSource` 을 `TryReset()` 해서는 timeout reset
이 되지 않는 듯 하다.

따라서.. 좀 무식하지만,

```cs
    private async Task KeepReceivingMessage(TcpClient client, CancellationToken ct)
    {
        Channel? channel = null; // try/using scope 밖에서도 정보를 얻기 위해
        try
        {
            using (var messageTimeoutCts = CreateMessageTimeoutTokenSource())
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(messageTimeoutCts.Token, ct);
                var cryptor = await handshaker.HandshakeAsync(client.GetStream(), cts.Token);
                channel = new Channel(client, cryptor);
            }
            var available = client.GetStream().DataAvailable;
            Handshaked(channel);
            while (channel.IsConnected && ct.IsCancellationRequested == false)
            {
                using var messageTimeoutCts = CreateMessageTimeoutTokenSource();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(messageTimeoutCts.Token, ct);
                var message = await channel.ReadMessageAsync(cts.Token);
                MessageReceived(channel, message);
            }
        }
```

와 같은 방식만 적용 가능.


### netstandard2.1 빌드 지원

unity 환경 에서도 사용할 수 있도록, netstandard2.1 빌드를 지원해볼 예정.
`.csproj` 를 

```xml
   <PropertyGroup>
    <TargetFrameworks>net9.0;netstandard2.1</TargetFrameworks>
    <Nullable>enable</Nullable>

    <!-- to support netstandard2.1 -->
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
 ```

와 같이 수정해 보았으나, `~is not available in C# 8.0~`(CS8400 error) 발생.

```xml
    <LangVersion>12.0</LangVersion>
```

으로 수동으로 설정해주어야 함. 여전히 빌드하면 오류..

```
dotnet build /Users/alkee/Documents/git/scpm/scpm.sln /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary /p:Configuration=Debug /p:Platform="Any CPU" 
  Determining projects to restore...
  All projects are up-to-date for restore.
/Users/alkee/Documents/git/scpm/scpm/MessageDispatcher.cs(17,19): error CS0656: Missing compiler required member 'System.Runtime.CompilerServices.RequiredMemberAttribute..ctor' [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
/Users/alkee/Documents/git/scpm/scpm/MessageDispatcher.cs(17,19): error CS0656: Missing compiler required member 'System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute..ctor' [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
  scpm -> /Users/alkee/Documents/git/scpm/scpm/bin/Debug/net9.0/scpm.dll
  scpm-test -> /Users/alkee/Documents/git/scpm/scpm-test/bin/Debug/net9.0/scpm-test.dll
```

 `Runtime.CompilerServices.RequiredMemberAttribute` 가 netstandard2.1 에서는
 지원이 안되는 모양. 호환성 연결을 담당하는 `NetStandard1.2.cs` 파일을 만들어
 여기에 `RequiredMemberAttribute` 생성. 하지만..

 ```
 /Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(37,9): error CS1674: 'TcpListener': type used in a using statement must implement 'System.IDisposable'. [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
/Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(46,45): error CS1501: No overload for method 'AcceptTcpClientAsync' takes 1 arguments [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
/Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(58,22): error CS1061: 'TcpListener' does not contain a definition for 'Dispose' and no accessible extension method 'Dispose' accepting a first argument of type 'TcpListener' could be found (are you missing a using directive or an assembly reference?) [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
/Users/alkee/Documents/git/scpm/scpm/Net/Client.cs(27,22): error CS1501: No overload for method 'ConnectAsync' takes 3 arguments [/Users/alkee/Documents/git/scpm/scpm/scpm.csproj::TargetFramework=netstandard2.1]
  scpm-test -> /Users/alkee/Documents/git/scpm/scpm-test/bin/Debug/net9.0/scpm-test.dll
```

와 같은 추가적인 호환성 관련 오류가 나타남. 왜 한번에 안나오고 하나씩 해결하면
추가로 나타나는 것인지... 직접 `dotnet build` 해보면 메시지가 좀 더 명확해보임.

 ```
 (base) ü/Documents/git/scpm % dotnet build
Restore complete (0.4s)
  scpm net9.0 succeeded (0.3s) → scpm/bin/Debug/net9.0/scpm.dll
  scpm netstandard2.1 failed with 4 error(s) (0.4s)
    /Users/alkee/Documents/git/scpm/scpm/Net/Client.cs(27,22): error CS1501: No overload for method 'ConnectAsync' takes 3 arguments
    /Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(37,9): error CS1674: 'TcpListener': type used in a using statement must implement 'System.IDisposable'.
    /Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(46,45): error CS1501: No overload for method 'AcceptTcpClientAsync' takes 1 arguments
    /Users/alkee/Documents/git/scpm/scpm/Net/Server.cs(58,22): error CS1061: 'TcpListener' does not contain a definition for 'Dispose' and no accessible extension method 'Dispose' accepting a first argument of type 'TcpListener' could be found (are you missing a using directive or an assembly reference?)
  scpm-test succeeded (0.2s) → scpm-test/bin/Debug/net9.0/scpm-test.dll
```

지원하지 않는 함수는 

```cs
        public static async Task ConnectAsync(this TcpClient client, string host, int port, CancellationToken ct)
        {
            await Task.Run(async () => {
                await client.ConnectAsync(host, port);
            }, ct);
        }

        public static async Task<TcpClient> AcceptTcpClientAsync (this TcpListener listener, CancellationToken ct)
        {
            return await Task.Run(async () => {
                return await listener.AcceptTcpClientAsync();
            }, ct);
        }
```
와 같이 extension 으로 구현하고, NetStandard2.1 에 구현되어있지않은 TcpListener.IDisposable
은 `#if` 로 `using` 을 제거하는 방식으로..

```cs
#if NETSTANDARD
        var listener = new TcpListener(IPAddress.Any, tcpPort);
#else
        using var listener = new TcpListener(IPAddress.Any, tcpPort);
#endif
```


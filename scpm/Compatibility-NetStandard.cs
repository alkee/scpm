#if NETSTANDARD

using System.Threading;
using System.Threading.Tasks;

// net9.0 에서는 지원하지만 netstandard2.1 에서는 지원하지 않는 class 나
// 함수듫을 직접 구현해 인터페이스를 맞추도록.

namespace System.Runtime.CompilerServices
{
    public class RequiredMemberAttribute : Attribute { }
    public class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string name) { }
    }
}

namespace System.Net.Sockets
{
    internal static class SocketsExt
    {
        public static async Task ConnectAsync(this TcpClient client, string host, int port, CancellationToken ct)
        {
            await Task.Run(async () =>
            {
                await client.ConnectAsync(host, port);
            }, ct);
        }

        public static async Task<TcpClient> AcceptTcpClientAsync(this TcpListener listener, CancellationToken ct)
        {
            return await Task.Run(async () =>
            {
                return await listener.AcceptTcpClientAsync();
            }, ct);
        }
    }
}

#endif

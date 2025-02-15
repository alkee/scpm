using System.Net.Sockets;

using scpm.handshake; // proto messsages

using scpm.Security;

namespace scpm.Net;

internal abstract class Handshaker
{
    protected const string HANDSHAKE_VERSION = "1.0.0";
    public abstract Task<Cryptor> HandshakeAsync(NetworkStream stream, CancellationToken ct);
}

internal sealed class ServerHandshaker
    : Handshaker
{
    public override async Task<Cryptor> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20];
        var plain = new NullCryptor();
        using var rsa = new RSACryptor();
        await Channel.SendMessageAsync(stream, new WhoAreYou // plain text
        {
            Version = HANDSHAKE_VERSION,
            PublicKey = rsa.PublicKeyBase64,
        }, plain, ct);
        var whoiam = await Channel.ReadMessageAsync<WhoIAm>(stream, buffer, rsa, ct);
        // TODO: whoiam.Version validation
        var aes = new AESCryptor(whoiam.Key, whoiam.Iv);
        await Channel.SendMessageAsync(stream, new Handshake
        {
            PublicKey = rsa.PublicKeyBase64
        }, aes, ct);
        return aes;
    }
}

internal sealed class ClientHandshaker
    : Handshaker
{
    public override async Task<Cryptor> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1_024 * 20];
        var plain = new NullCryptor();
        var whoareyou = await Channel.ReadMessageAsync<WhoAreYou>(stream, buffer, plain, ct);
        // TODO: validate whoareyou.Version
        using var rsa = new RSACryptor(whoareyou.PublicKey);
        var aes = new AESCryptor(); // TODO: whoareyou 의 정보 일부를 이용해 iv 또는 key 설정
        await Channel.SendMessageAsync(stream, new WhoIAm
        {
            Version = HANDSHAKE_VERSION,
            Key = aes.KeyBase64,
            Iv = aes.IVBase64
        }, rsa, ct);
        var handshake = await Channel.ReadMessageAsync<Handshake>(stream, buffer, aes, ct);
        // TODO: validate handshake.PublicKey
        return aes;
    }
}

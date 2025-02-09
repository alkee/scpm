using System.Security.Cryptography;
using scpm.handshake;

namespace scpm;

public class Cryptor
{
    public virtual byte[] Encrypt(byte[] bytes) => bytes;
    public virtual byte[] Decrypt(byte[] bytes) => bytes;
}


public sealed class RSACryptor
    : Cryptor, IDisposable
{
    public RSACryptor(byte[] publicKey)
    {
        rsa.ImportRSAPublicKey(publicKey, out var readBytes);
    }

    public RSACryptor(string publicKeyBass64)
        : this(Convert.FromBase64String(publicKeyBass64))
    {
    }

    public RSACryptor()
    {
    }

    void IDisposable.Dispose()
    {
        rsa.Dispose();
    }

    public byte[] PublicKey => rsa.ExportRSAPublicKey();
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);
    public byte[] PrivateKey => rsa.ExportRSAPrivateKey();
    public string PrivateKeyBase64 => Convert.ToBase64String(PrivateKey);

    public override byte[] Encrypt(byte[] bytes)
    {
        return rsa.Encrypt(bytes, RSAEncryptionPadding.OaepSHA1);
    }

    public override byte[] Decrypt(byte[] bytes)
    {
        return rsa.Decrypt(bytes, RSAEncryptionPadding.OaepSHA1);
    }

    private readonly RSA rsa = RSA.Create();
}

public sealed class AESCryptor
    : Cryptor, IDisposable
{
    public AESCryptor()
    {
        aes.GenerateKey();
        aes.GenerateIV();
    }

    public AESCryptor(byte[] key, byte[] iv)
    {
        aes.Key = key;
        aes.IV = iv;
    }

    public AESCryptor(string base64key, string base64iv)
        : this(
            Convert.FromBase64String(base64key),
            Convert.FromBase64String(base64iv)
            )
    {
    }

    void IDisposable.Dispose()
    {
        aes.Dispose();
    }

    public byte[] Key => aes.Key;
    public string KeyBase64 => Convert.ToBase64String(Key);
    public byte[] IV => aes.IV;
    public string IVBase64 => Convert.ToBase64String(IV);

    public override byte[] Encrypt(byte[] bytes)
    {
        using var encryptor = aes.CreateEncryptor();
        using var buffer = new MemoryStream();
        using var stream = new CryptoStream(buffer, encryptor, CryptoStreamMode.Write);
        stream.Write(bytes);
        stream.Flush();
        return buffer.ToArray();
    }

    public override byte[] Decrypt(byte[] bytes)
    {
        using var decryptor = aes.CreateDecryptor();
        using var buffer = new MemoryStream(bytes);
        using var stream = new CryptoStream(buffer, decryptor, CryptoStreamMode.Read);
        using var destBuffer = new MemoryStream();
        stream.CopyTo(destBuffer);
        return destBuffer.ToArray();
    }

    private readonly Aes aes = Aes.Create();
}



public class HandshakeDispatcher
    : MessageDispatcher
{
    public event Action<Session> Completed = delegate { };
    public HandshakeDispatcher(MessageDispatcher nextDispatcher)
    {
        next = nextDispatcher;
        AddHandler<WhoAreYou>(WhoAreYou);
        AddHandler<WhoIAm>(WhoIAm);
        AddHandler<Handshake>(Handshake);
    }

    #region Server 에서만 사용되는 함수들

    /// <summary>
    ///     Handshake begins from server
    /// </summary>
    /// <param name="session">
    ///     session on server
    /// </param>
    public void HandshakeBegin(Session client)
    {
        // client: session on Server
        var nextCrpytor = new RSACryptor();
        client.HandshakeSend(new WhoAreYou
        {
            Version = PROTOCOL_VERSION,
            PublicKey = Convert.ToBase64String(nextCrpytor.PrivateKey)
        });
        client.Cryptor = nextCrpytor;
    }

    #endregion

    private const string PROTOCOL_VERSION = "1.0";

    /// Plain text message from Server
    private void WhoAreYou(Session server, WhoAreYou message)
    {
        // TODO: supported version varification
        var publicKey = Convert.FromBase64String(message.PublicKey);
        server.Cryptor = new RSACryptor(publicKey);
        var nextCryptor = new AESCryptor();
        server.HandshakeSend(new WhoIAm
        {
            Version = PROTOCOL_VERSION,
            Key = Convert.ToBase64String(nextCryptor.Key),
            Iv = Convert.ToBase64String(nextCryptor.IV),
        });
        server.Cryptor = nextCryptor;
    }

    /// Encoded message by public key from Client
    private void WhoIAm(Session client, WhoIAm message)
    {
        var rsa = client.Cryptor as RSACryptor
            ?? throw new ApplicationException("invalid crpytor");
        var publicKey = rsa.PublicKey;

        var key = Convert.FromBase64String(message.Key);
        var iv = Convert.FromBase64String(message.Iv);
        client.Cryptor = new AESCryptor(key, iv);
        // TODO: validate protocol version
        client.HandshakeSend(new Handshake
        {
            PublicKey = Convert.ToBase64String(publicKey)
        });
        _ = client.Begin();
    }

    // Encoded message by symetric security key from Server
    private void Handshake(Session server, Handshake message)
    {
        // TODO: message.publickey validation
        _ = server.Begin();
    }

    private readonly MessageDispatcher next;

    private static readonly RSA rsa = RSA.Create(); // server only
    private static readonly string PublicKey = GetPublicKey();
    private static string GetPublicKey()
    {
        return Convert.ToBase64String(rsa.ExportRSAPublicKey());
    }
}

using System;
using System.IO;
using System.Security.Cryptography;

namespace scpm.Security;

internal interface IEncodable
{
    byte[] Encode(byte[] bytes);
}

internal interface IDecodable
{
    byte[] Decode(byte[] bytes);
}

internal abstract class Cryptor
        : IEncodable, IDecodable
{
    public abstract byte[] Encode(byte[] bytes);
    public abstract byte[] Decode(byte[] bytes);
}


internal sealed class NullCryptor
    : Cryptor
{
    public override byte[] Encode(byte[] bytes) => bytes;
    public override byte[] Decode(byte[] bytes) => bytes;
}


internal sealed class RSACryptor
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

    public override byte[] Encode(byte[] bytes)
    {
        return rsa.Encrypt(bytes, RSAEncryptionPadding.Pkcs1);
    }

    public override byte[] Decode(byte[] bytes)
    {
        return rsa.Decrypt(bytes, RSAEncryptionPadding.Pkcs1);
    }

    private readonly RSA rsa = RSA.Create();
}

internal sealed class AESCryptor
    : Cryptor, IDisposable
{
    public AESCryptor(byte[]? key = null, byte[]? iv = null)
    {
        if (key is null)
            aes.GenerateKey();
        else
            aes.Key = key;
        if (iv is null)
            aes.GenerateIV();
        else
            aes.IV = iv;
        aes.Padding = PaddingMode.PKCS7;
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

    public override byte[] Encode(byte[] bytes)
    {
        using var encryptor = aes.CreateEncryptor();
        using var buffer = new MemoryStream();
        using var stream = new CryptoStream(buffer, encryptor, CryptoStreamMode.Write);
        stream.Write(bytes);
        stream.FlushFinalBlock();
        return buffer.ToArray();
    }

    public override byte[] Decode(byte[] bytes)
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

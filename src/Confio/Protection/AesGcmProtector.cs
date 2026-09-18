using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Confio.Protection;

internal sealed class AesGcmProtector : IConfigurationProtector, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4 + NonceSize + TagSize;
    private readonly byte[] _key;
    private bool _disposed;

    internal AesGcmProtector(ReadOnlySpan<byte> key)
    {
        _key = key.ToArray();
    }

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AesGcmProtector));
        var payload = new byte[checked(HeaderSize + plaintext.Length)];
        "CFA1"u8.CopyTo(payload);
        var nonce = payload.AsSpan(4, NonceSize);
#if NETFRAMEWORK
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(payload, 4, NonceSize);
#else
        RandomNumberGenerator.Fill(nonce);
#endif
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, payload.AsSpan(HeaderSize), payload.AsSpan(4 + NonceSize, TagSize),
            Encoding.UTF8.GetBytes(purpose));
        return payload;
    }

    public byte[] Unprotect(byte[] ciphertext, string purpose)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AesGcmProtector));
        if (ciphertext.Length < HeaderSize || !ciphertext.AsSpan(0, 4).SequenceEqual("CFA1"u8))
        {
            throw new InvalidDataException("The AES-GCM payload has an unsupported version or invalid shape.");
        }
        var plaintext = new byte[ciphertext.Length - HeaderSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(ciphertext.AsSpan(4, NonceSize), ciphertext.AsSpan(HeaderSize),
                ciphertext.AsSpan(4 + NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(purpose));
            return plaintext;
        }
        catch
        {
            Clear(plaintext);
            throw;
        }
    }

    public Task<byte[]> ProtectAsync(byte[] plaintext, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Protect(plaintext, purpose));
    }

    public Task<byte[]> UnprotectAsync(byte[] ciphertext, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unprotect(ciphertext, purpose));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Clear(_key);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void Clear(byte[] buffer)
    {
#if NETFRAMEWORK
        // Framework 没有 CryptographicOperations；同样禁止优化移除密钥清理。
        Array.Clear(buffer, 0, buffer.Length);
#else
        CryptographicOperations.ZeroMemory(buffer);
#endif
    }
}

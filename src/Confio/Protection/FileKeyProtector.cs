using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Confio.Internal;

namespace Confio.Protection;

// 文件实例的操作锁负责本实例协调；首次建钥复用文件协作锁和原子提交。
internal sealed class FileKeyProtector(string path) : IConfigurationProtector, IDisposable
{
    private const string KeyPurpose = "Confio/AesKey/v1";
    private AesGcmProtector? _aes;

    public byte[] Protect(byte[] plaintext, string purpose) => Get(create: true).Protect(plaintext, purpose);
    public byte[] Unprotect(byte[] ciphertext, string purpose) => Get(create: false).Unprotect(ciphertext, purpose);

    public async Task<byte[]> ProtectAsync(byte[] plaintext, string purpose, CancellationToken cancellationToken = default) =>
        (await GetAsync(create: true, cancellationToken).ConfigureAwait(false)).Protect(plaintext, purpose);

    public async Task<byte[]> UnprotectAsync(byte[] ciphertext, string purpose, CancellationToken cancellationToken = default) =>
        (await GetAsync(create: false, cancellationToken).ConfigureAwait(false)).Unprotect(ciphertext, purpose);

    private AesGcmProtector Get(bool create)
    {
        if (_aes is not null) return _aes;
        var stored = Read();
        if (stored is not null) return Remember(stored);
        if (!create) throw MissingKey();
        CreateDirectory();
        using var keyLock = FileStore.Acquire(path);
        stored = Read();
        if (stored is not null) return Remember(stored);
        stored = NewKey();
        try
        {
            FileStore.Write(path, stored, privateFile: true);
            return Remember(stored);
        }
        finally { Array.Clear(stored, 0, stored.Length); }
    }

    private async Task<AesGcmProtector> GetAsync(bool create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_aes is not null) return _aes;
        var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not null) return Remember(stored);
        if (!create) throw MissingKey();
        CreateDirectory();
        using var keyLock = await FileStore.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
        stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not null) return Remember(stored);
        stored = NewKey();
        try
        {
            await FileStore.WriteAsync(path, stored, cancellationToken, privateFile: true).ConfigureAwait(false);
            return Remember(stored);
        }
        finally { Array.Clear(stored, 0, stored.Length); }
    }

    private byte[]? Read()
    {
        try
        {
            using var stream = OpenRead(asynchronous: false);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var result = buffer.ToArray();
            Array.Clear(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = OpenRead(asynchronous: true);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            var result = buffer.ToArray();
            Array.Clear(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // 原子重命名会短暂持有删除权限，读取必须允许共享删除。
    private FileStream OpenRead(bool asynchronous) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
            asynchronous ? FileOptions.Asynchronous : FileOptions.None);

    private AesGcmProtector Remember(byte[] stored)
    {
        byte[]? key = null;
        try
        {
            key = DefaultPaths.IsWindows ? new DpapiProtector().Unprotect(stored, KeyPurpose) : stored;
            if (key.Length != 32) throw new InvalidDataException("The automatic AES key file must contain a valid 32-byte key.");
            return _aes = new AesGcmProtector(key);
        }
        finally
        {
            if (key is not null) Array.Clear(key, 0, key.Length);
            Array.Clear(stored, 0, stored.Length);
        }
    }

    private static byte[] NewKey()
    {
        var key = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
        if (!DefaultPaths.IsWindows) return key;
        try { return new DpapiProtector().Protect(key, KeyPurpose); }
        finally { Array.Clear(key, 0, key.Length); }
    }

    private void CreateDirectory()
    {
        var directory = Path.GetDirectoryName(path)!;
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
#endif
        Directory.CreateDirectory(directory);
    }

    private FileNotFoundException MissingKey() => new("The automatic AES key is missing. Restore the original key file to decrypt the configuration.", path);
    public void Dispose() => _aes?.Dispose();
}

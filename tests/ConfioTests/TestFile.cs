using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Confio.Protection;

namespace ConfioTests;

internal sealed class TestFile : IDisposable
{
    private readonly string _extension;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ConfioTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    internal TestFile(string extension = ".json")
    {
        _extension = extension;
    }

    internal string FilePath => Path.Combine(_directory, "settings" + _extension);
    internal string DirectoryPath => _directory;
    internal CancellationToken Token => _deadline.Token;
    internal byte[] EncryptionKey { get; } = RandomNumberGenerator.GetBytes(32);

    internal ConfigurationFile Create(IConfigurationProtector? protector = null) =>
        new(TestSettingsContext.Default, FilePath, options: new ConfigurationFileOptions { EncryptionKey = protector is null ? EncryptionKey : null, Protector = protector });

    internal void Write(string text)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(EncryptionKey);
        _deadline.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class ControlledProtector : IConfigurationProtector, IDisposable
{
    private readonly AesGcmProtector _inner;
    internal int ProtectCalls;
    internal int UnprotectCalls;
    internal Action? AfterProtect;
    internal Func<CancellationToken, Task>? BeforeUnprotectAsync;

    internal ControlledProtector(byte[] key)
    {
        _inner = new AesGcmProtector(key);
    }

    public byte[] Protect(byte[] plaintext, string purpose)
    {
        Interlocked.Increment(ref ProtectCalls);
        var result = _inner.Protect(plaintext, purpose);
        AfterProtect?.Invoke();
        return result;
    }

    public byte[] Unprotect(byte[] ciphertext, string purpose)
    {
        Interlocked.Increment(ref UnprotectCalls);
        return _inner.Unprotect(ciphertext, purpose);
    }

    public Task<byte[]> ProtectAsync(byte[] plaintext, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Protect(plaintext, purpose));
    }

    public async Task<byte[]> UnprotectAsync(byte[] ciphertext, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeUnprotectAsync is not null)
        {
            await BeforeUnprotectAsync(cancellationToken);
        }
        return Unprotect(ciphertext, purpose);
    }

    public void Dispose() => _inner.Dispose();
}

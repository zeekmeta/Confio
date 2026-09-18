using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Confio.Protection;

internal sealed class DpapiProtector : IConfigurationProtector
{
    public byte[] Protect(byte[] plaintext, string purpose)
    {
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
#endif
        return ProtectedData.Protect(plaintext, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext, string purpose)
    {
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
#endif
        return ProtectedData.Unprotect(ciphertext, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
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
}

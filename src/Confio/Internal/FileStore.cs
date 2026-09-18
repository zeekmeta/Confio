using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Confio.Formats;

namespace Confio.Internal;

internal static class FileStore
{
    private const int MaximumCommitRetries = 4;
    private const int CommitRetryDelayMilliseconds = 25;

    internal static FileDocument Read(string path, FileFormat format, out bool exists)
    {
        try
        {
            using var stream = OpenRead(path, asynchronous: false);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            exists = true;
            return format.Parse(buffer.ToArray());
        }
        catch (FileNotFoundException)
        {
            exists = false;
            return format.Empty();
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            return format.Empty();
        }
    }

    internal static async Task<(FileDocument Document, bool Exists)> ReadAsync(string path, FileFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = OpenRead(path, asynchronous: true);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            return (format.Parse(buffer.ToArray()), true);
        }
        catch (FileNotFoundException)
        {
            return (format.Empty(), false);
        }
        catch (DirectoryNotFoundException)
        {
            return (format.Empty(), false);
        }
    }

    internal static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            try
            {
                return OpenLock(path);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                Thread.Sleep(25);
            }
        }
    }

    internal static async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OpenLock(path);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static void Write(string path, byte[] bytes, bool privateFile = false)
    {
        var temporary = TemporaryPath(path);
        var created = false;
        try
        {
            using (var stream = OpenWrite(temporary, asynchronous: false, privateFile))
            {
                created = true;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            Commit(temporary, path);
        }
        catch (Exception exception)
        {
            if (created)
            {
                Cleanup(temporary, exception);
            }
            throw;
        }
    }

    internal static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken, bool privateFile = false)
    {
        var temporary = TemporaryPath(path);
        var created = false;
        try
        {
            using (var stream = OpenWrite(temporary, asynchronous: true, privateFile))
            {
                created = true;
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // 磁盘刷新和最终替换均为同步平台调用，取消在提交前检查。
                stream.Flush(flushToDisk: true);
            }
            await CommitAsync(temporary, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (created)
            {
                Cleanup(temporary, exception);
            }
            throw;
        }
    }

    private static FileStream OpenWrite(string path, bool asynchronous, bool privateFile)
    {
#if !NETFRAMEWORK
        if (privateFile && !OperatingSystem.IsWindows())
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
#endif
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            asynchronous ? FileOptions.Asynchronous : FileOptions.None);
    }

    private static FileStream OpenRead(string path, bool asynchronous) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
            asynchronous ? FileOptions.Asynchronous | FileOptions.SequentialScan : FileOptions.SequentialScan);

    private static FileStream OpenLock(string path) =>
        new(path + ".confio.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static bool IsContention(IOException exception)
    {
        var code = exception.HResult & 0xffff;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return code is 32 or 33;
        // EAGAIN / EWOULDBLOCK 在 macOS 是 35，在 Linux 是 11。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return code == 35;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && code == 11;
    }

    private static string TemporaryPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

    private static void Commit(string temporary, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                CommitOnce(temporary, path);
                return;
            }
            catch (IOException exception) when (CanRetryCommit(exception, attempt))
            {
                Thread.Sleep(CommitRetryDelayMilliseconds);
            }
        }
    }

    private static async Task CommitAsync(string temporary, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CommitOnce(temporary, path);
                return;
            }
            catch (IOException exception) when (CanRetryCommit(exception, attempt))
            {
                await Task.Delay(CommitRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Windows 的共享冲突及 1175 保留原文件和替换文件，只重试同一份已准备好的提交。
    // 1176 / 1177 可能已经移动文件，不属于可重试错误；权限等其他失败也直接报告。
    private static bool CanRetryCommit(IOException exception, int attempt) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && attempt < MaximumCommitRetries &&
        (exception.HResult & 0xffff) is 32 or 33 or 1175;

    private static void CommitOnce(string temporary, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static void Cleanup(string path, Exception failure)
    {
        // ReplaceFile 在这两种失败下可能已移动文件；保留剩余替换文件，避免删除唯一可恢复副本。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && failure is IOException && (failure.HResult & 0xffff) is 1176 or 1177)
        {
            failure.Data["Confio.ReplacementFile"] = path;
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // 保留原始失败，并附上清理错误类别；不输出配置正文。
            failure.Data["Confio.TemporaryFileCleanupFailure"] = cleanup.GetType().FullName;
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Confio;
using Xunit;

namespace ConfioTests;

public sealed class PlatformFileTests
{
    [ReplacementFailureTheory]
    [InlineData(".json", false)]
    [InlineData(".json", true)]
    [InlineData(".yaml", false)]
    [InlineData(".yaml", true)]
    [InlineData(".toml", false)]
    [InlineData(".toml", true)]
    [InlineData(".ini", false)]
    [InlineData(".ini", true)]
    public async Task ReplacementFailureKeepsTheOriginalAndCleansOnlyItsTemporaryFile(string extension, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new FormatSettings { Port = 465, Password = "secret-original" });
        var before = File.ReadAllBytes(fixture.FilePath);
        var foreign = Path.Combine(fixture.DirectoryPath, ".unrelated.tmp");
        File.WriteAllText(foreign, "owned by another writer");
        var calls = 0;
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        void Edit(FormatSettings mail)
        {
            calls++;
            mail.Port = 25;
            mail.Password = "secret-failed";
        }
        using (BlockReplacement(fixture.FilePath))
        {
            var failure = asynchronous
                ? await Record.ExceptionAsync(() => file.UpdateAsync<FormatSettings>(Edit, fixture.Token))
                : Record.Exception(() => file.Update<FormatSettings>(Edit));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(1, calls);
            Assert.Equal(0, notifications);
            Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
            Assert.Equal(465, file.Read<FormatSettings>().Port);
            Assert.Equal("secret-original", file.Read<FormatSettings>().Password);
            Assert.Equal(new[] { foreign }, Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }
        if (asynchronous) await file.UpdateAsync<FormatSettings>(Edit, fixture.Token);
        else file.Update<FormatSettings>(Edit);
        Assert.Equal(2, calls);
        Assert.Equal(1, notifications);
        using var reopened = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Equal(25, reopened.Read<FormatSettings>().Port);
        Assert.Equal("secret-failed", reopened.Read<FormatSettings>().Password);
        Assert.Equal(new[] { foreign }, Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [ReplacementFailureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnwritablePlaintextCannotCompleteTheInitialLoad(bool asynchronous)
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Mail":{"port":465,"credential":"manual-secret"}}""");
        var original = File.ReadAllBytes(fixture.FilePath);
        using var file = fixture.Create();
        using (BlockReplacement(fixture.FilePath))
        {
            var failure = asynchronous
                ? await Record.ExceptionAsync(() => file.ReadAsync<MailSettings>(fixture.Token))
                : Record.Exception(() => file.Read<MailSettings>());
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        fixture.Write("""{"Mail":{"port":25,"credential":"retry-secret"}}""");
        var value = asynchronous ? await file.ReadAsync<MailSettings>(fixture.Token) : file.Read<MailSettings>();
        Assert.Equal(25, value.Port);
        Assert.Equal("retry-secret", value.Password);
        Assert.DoesNotContain("retry-secret", File.ReadAllText(fixture.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadersWithoutDeleteSharingFollowPlatformReplacementSemantics(bool asynchronous)
    {
        using var fixture = new TestFile();
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465 });
        var original = File.ReadAllText(fixture.FilePath);
        using var stream = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        var failure = asynchronous
            ? await Record.ExceptionAsync(() => file.UpdateAsync<MailSettings>(mail => mail.Port = 2525, fixture.Token))
            : Record.Exception(() => file.Update<MailSettings>(mail => mail.Port = 2525));
        if (OperatingSystem.IsWindows()) Assert.IsAssignableFrom<IOException>(failure);
        else Assert.Null(failure);
        Assert.Equal(original, await reader.ReadToEndAsync(fixture.Token));
        using var reopened = fixture.Create();
        var expected = OperatingSystem.IsWindows() ? 465 : 2525;
        Assert.Equal(expected, reopened.Read<MailSettings>().Port);
        Assert.Equal(expected, file.Read<MailSettings>().Port);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    private static IDisposable BlockReplacement(string path) => OperatingSystem.IsWindows()
        ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        : new ImmutableFile(path);

    private sealed class ReplacementFailureTheoryAttribute : TheoryAttribute
    {
        public ReplacementFailureTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                Skip = "Requires Windows file sharing or macOS user-immutable files.";
        }
    }

    // 仅作用于 TestFile 新建的私有文件；父目录仍可写，确保故障发生在替换而非临时文件创建阶段。
    private sealed class ImmutableFile : IDisposable
    {
        private readonly string _path;

        internal ImmutableFile(string path)
        {
            _path = path;
            try { ChangeFlags("uchg"); }
            catch (Exception failure)
            {
                try { ChangeFlags("nouchg"); }
                catch (Exception cleanup) { failure.Data["ImmutableFileCleanupFailure"] = cleanup; }
                throw;
            }
        }

        public void Dispose() => ChangeFlags("nouchg");

        private void ChangeFlags(string flags)
        {
            var start = new ProcessStartInfo("/usr/bin/chflags") { UseShellExecute = false };
            start.ArgumentList.Add(flags);
            start.ArgumentList.Add(_path);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start chflags.");
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                process.WaitForExit(5000);
                throw new TimeoutException("chflags did not complete.");
            }
            if (process.ExitCode != 0) throw new IOException("Cannot change the test file's immutable flag.");
        }
    }
}

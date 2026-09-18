using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConfioTests;

public sealed class ProtectionTests
{
    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task ExplicitKeySurvivesReopenAndProtectsNestedValues(string extension)
    {
        using var fixture = new TestFile(extension);
        var key = RandomNumberGenerator.GetBytes(32);
        var supplied = key.ToArray();
        using (var file = Create(fixture, supplied))
        {
            Array.Clear(supplied);
            Assert.False(Directory.Exists(fixture.DirectoryPath));
            Assert.Equal("", (await file.ReadAsync<MailSettings>(fixture.Token)).Password);
            file.Save(new MailSettings { Password = "aes-test-secret", Endpoint = new() { Token = "nested-secret" } });
            var first = File.ReadAllText(fixture.FilePath);
            Assert.DoesNotContain("aes-test-secret", first);
            Assert.DoesNotContain("nested-secret", first);
            await file.UpdateAsync<MailSettings>(mail => mail.Port = 465, fixture.Token);
            Assert.Equal("aes-test-secret", file.Read<MailSettings>().Password);
        }

        using var reopened = Create(fixture, key);
        var settings = await reopened.ReadAsync<MailSettings>(fixture.Token);
        Assert.Equal(465, settings.Port);
        Assert.Equal("aes-test-secret", settings.Password);
        Assert.Equal("nested-secret", settings.Endpoint!.Token);
        reopened.Update<MailSettings>(mail => mail.Password = "changed-secret");
        await reopened.ReloadAsync(fixture.Token);
        Assert.Equal("changed-secret", reopened.Read<MailSettings>().Password);
    }

    [Fact]
    public void PayloadInteroperatesWithStandardAesGcmAndUsesFreshNonces()
    {
        using var fixture = new TestFile();
        var key = RandomNumberGenerator.GetBytes(32);
        using var file = Create(fixture, key);
        file.Save(new MailSettings { Password = "protocol-test-secret" });
        byte[] Payload() => Convert.FromBase64String(JsonNode.Parse(File.ReadAllText(fixture.FilePath))!["Mail"]!["credential"]!.GetValue<string>()["enc:v1:".Length..]);
        var first = Payload();
        Assert.True(first.AsSpan(0, 4).SequenceEqual("CFA1"u8));
        var plaintext = new byte[first.Length - 32];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(first.AsSpan(4, 12), first.AsSpan(32), first.AsSpan(16, 16), plaintext, Encoding.UTF8.GetBytes("Confio/v1\0/Mail/credential"));
        Assert.Equal("protocol-test-secret", JsonNode.Parse(plaintext)!.GetValue<string>());
        file.Save(new MailSettings { Password = "protocol-test-secret" });
        Assert.False(first.AsSpan(4, 12).SequenceEqual(Payload().AsSpan(4, 12)));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("path")]
    [InlineData("tag")]
    [InlineData("ciphertext")]
    [InlineData("version")]
    [InlineData("payload-version")]
    [InlineData("truncated")]
    public async Task InvalidProtectionDoesNotReplaceTheFileOrSnapshot(string change)
    {
        using var fixture = new TestFile();
        var key = RandomNumberGenerator.GetBytes(32);
        using var file = Create(fixture, key);
        file.Save(new MailSettings { Password = "original-secret" });
        var document = JsonNode.Parse(File.ReadAllText(fixture.FilePath))!;
        var envelope = document["Mail"]!["credential"]!;
        if (change == "path")
        {
            document["Mail"]!["endpoint"]!["api/key~1"] = envelope.DeepClone();
        }
        else if (change is "tag" or "ciphertext" or "version" or "payload-version" or "truncated")
        {
            var protectedText = envelope.GetValue<string>();
            var bytes = Convert.FromBase64String(protectedText["enc:v1:".Length..]);
            if (change == "truncated")
                bytes = bytes[..10];
            else if (change == "payload-version")
                bytes[3] = (byte)'2';
            else if (change == "version")
            {
                document["Mail"]!["credential"] = "enc:v2:" + Convert.ToBase64String(bytes);
            }
            else
                bytes[change == "tag" ? 16 : 32] ^= 1;
            if (change != "version")
            {
                document["Mail"]!["credential"] = "enc:v1:" + Convert.ToBase64String(bytes);
            }
        }

        File.WriteAllText(fixture.FilePath, document.ToJsonString());
        var disk = File.ReadAllBytes(fixture.FilePath);
        using var other = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = change == "key" ? RandomNumberGenerator.GetBytes(32) : key });
        var error = await Record.ExceptionAsync(() => other.ReadAsync<MailSettings>(fixture.Token));
        Assert.True(error is CryptographicException or InvalidDataException);
        Assert.DoesNotContain("original-secret", error!.ToString());
        Assert.Equal(disk, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal("original-secret", file.Read<MailSettings>().Password);
        if (change != "key")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => file.ReloadAsync(fixture.Token));
            Assert.Equal("original-secret", file.Read<MailSettings>().Password);
        }
    }

    [Fact]
    public async Task EveryContainerEntryUsesTheSameExplicitKey()
    {
        using var fixture = new TestFile();
        var key = RandomNumberGenerator.GetBytes(32);
        using (var file = Create(fixture, key))
            file.Save(new MailSettings { Password = "container-secret" });
        var supplied = key.ToArray();
        var services = new ServiceCollection().AddConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = supplied });
        Array.Clear(supplied);
        var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();
        Assert.Equal("container-secret", first.GetRequiredService<ISettings<MailSettings>>().Read().Password);
        var other = second.GetRequiredService<ISettings<MailSettings>>();
        Assert.Equal("container-secret", other.Read().Password);
        first.Dispose();
        await other.UpdateAsync(mail => mail.Port = 465, fixture.Token);
        var syncServices = new ServiceCollection();
        using var syncConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        syncServices.AddConfigurationFile(syncConfiguration, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var native = syncServices.BuildServiceProvider())
            Assert.Equal("container-secret", native.GetRequiredService<IConfiguration>()["Mail:credential"]);
        var asyncServices = new ServiceCollection();
        using var asyncConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        await asyncServices.AddConfigurationFileAsync(asyncConfiguration, new[] { TestSettingsContext.Default }, fixture.FilePath, cancellationToken: fixture.Token, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var native = asyncServices.BuildServiceProvider())
            Assert.Equal("container-secret", native.GetRequiredService<IOptions<MailSettings>>().Value.Password);
        var syncBuilder = Host.CreateEmptyApplicationBuilder(null);
        syncBuilder.AddConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var host = syncBuilder.Build())
            Assert.Equal("container-secret", host.Services.GetRequiredService<ISettings<MailSettings>>().Read().Password);
        var asyncBuilder = Host.CreateEmptyApplicationBuilder(null);
        await asyncBuilder.AddConfigurationFileAsync(new[] { TestSettingsContext.Default }, fixture.FilePath, cancellationToken: fixture.Token, options: new ConfigurationFileOptions { EncryptionKey = key });
        using (var host = asyncBuilder.Build())
            Assert.Equal(465, host.Services.GetRequiredService<IOptions<MailSettings>>().Value.Port);
    }

    [Fact]
    public async Task InvalidKeysAndCancelledOperationsDoNotCreateFiles()
    {
        using var fixture = new TestFile();
        foreach (var length in new[]
        {
            0,
            16,
            24,
            31,
            33
        }

        )
        {
            Assert.Throws<ArgumentException>(() => Create(fixture, new byte[length]));
            Assert.Throws<ArgumentException>(() => new ServiceCollection().AddConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = new byte[length] }));
        }

        using var protector = new ControlledProtector(fixture.EncryptionKey);
        Assert.Throws<ArgumentException>(() => new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = new byte[32], Protector = protector }));
        using var file = Create(fixture, RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.SaveAsync(new MailSettings { Password = "cancelled-secret" }, new CancellationToken(true)));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }

    [Fact]
    public void FileAndContainerBorrowAnExternalProtectorWithoutDisposingIt()
    {
        using var fixture = new TestFile();
        var protector = new BorrowedProtector();
        using (var file = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { Protector = protector }))
            file.Read<MailSettings>();
        using (var root = new ServiceCollection().AddConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { Protector = protector }).BuildServiceProvider())
            root.GetRequiredService<ISettings<MailSettings>>().Read();
        Assert.False(protector.Disposed);
    }

    private static ConfigurationFile Create(TestFile fixture, byte[] key) => new(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
    private sealed class BorrowedProtector : IConfigurationProtector, IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
        public byte[] Protect(byte[] plaintext, string purpose) => throw new InvalidOperationException("Empty defaults must not use protection.");
        public byte[] Unprotect(byte[] ciphertext, string purpose) => throw new InvalidOperationException("Empty defaults must not use protection.");
        public Task<byte[]> ProtectAsync(byte[] plaintext, string purpose, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<byte[]> UnprotectAsync(byte[] ciphertext, string purpose, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

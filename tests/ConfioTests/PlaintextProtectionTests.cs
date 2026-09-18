using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConfioTests;

public sealed class PlaintextProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFileProtectsOnlyExistingPlaintextAndKeepsUnknownData(bool asynchronous)
    {
        using var fixture = new TestFile();
        using (var seed = fixture.Create()) seed.Save(new MailSettings { Password = "existing-secret" });
        var encrypted = JsonNode.Parse(File.ReadAllText(fixture.FilePath))!["Mail"]!["credential"]!.GetValue<string>();
        var document = JsonNode.Parse("""
            {"Mail":{"endpoint":{"api/key~1":"manual-secret"},"custom":{"$confio":"data"}},
             "External":{"prefix":"enc:v1:ordinary-data"}}
            """)!;
        document["Mail"]!["credential"] = encrypted;
        fixture.Write(document.ToJsonString());
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = fixture.Create(protector);
        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        var value = asynchronous ? await file.ReadAsync<MailSettings>(fixture.Token) : file.Read<MailSettings>();
        Assert.Equal("existing-secret", value.Password);
        Assert.Equal("manual-secret", value.Endpoint!.Token);
        Assert.Equal(587, value.Port);
        Assert.Equal(0, notifications);
        Assert.Equal(1, protector.ProtectCalls);
        Assert.Equal(1, protector.UnprotectCalls);

        var persisted = JsonNode.Parse(File.ReadAllText(fixture.FilePath))!;
        Assert.Equal(encrypted, persisted["Mail"]!["credential"]!.GetValue<string>());
        Assert.Null(persisted["Mail"]!["port"]);
        Assert.Null(persisted["Mail"]!["endpoint"]!["timeout"]);
        Assert.Equal("data", persisted["Mail"]!["custom"]!["$confio"]!.GetValue<string>());
        Assert.Equal("enc:v1:ordinary-data", persisted["External"]!["prefix"]!.GetValue<string>());
        var bytes = File.ReadAllBytes(fixture.FilePath);
        using var readOnly = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (asynchronous) await file.ReloadAsync(fixture.Token);
        else file.Reload();
        Assert.Equal(1, notifications);
        Assert.Equal(1, protector.ProtectCalls);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [Theory]
    [InlineData(".json", "enc:plain")]
    [InlineData(".yaml", "enc:v1:literal-password")]
    [InlineData(".json", "enc:v2:AAAA")]
    [InlineData(".yaml", "enc:v1:")]
    [InlineData(".json", "enc:v1:AAAA")]
    public async Task ReservedFilePrefixesFailWithoutPublishingOrProtectingOtherPlaintext(string extension, string input)
    {
        using var fixture = new TestFile(extension);
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = fixture.Create(protector);
        Assert.Equal(587, file.Read<MailSettings>().Port);
        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        var document = JsonNode.Parse("""{"Mail":{"port":465,"endpoint":{"api/key~1":"manual-secret"}}}""")!;
        document["Mail"]!["credential"] = input;
        fixture.Write(document.ToJsonString());
        var before = File.ReadAllBytes(fixture.FilePath);

        var failure = await Record.ExceptionAsync(() => file.ReloadAsync(fixture.Token));
        Assert.True(failure is InvalidDataException or CryptographicException);
        Assert.DoesNotContain(input, failure!.ToString());
        using var firstRead = fixture.Create();
        Assert.ThrowsAny<Exception>(() => firstRead.Read<MailSettings>());
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal(0, notifications);
        Assert.Equal(0, protector.ProtectCalls);
    }

    [Theory]
    [InlineData(".json", "save")]
    [InlineData(".yaml", "update")]
    [InlineData(".json", "reset")]
    public async Task DirectWritesIncludePlaintextProtectionInTheirSingleCommit(string extension, string operation)
    {
        using var fixture = new TestFile(extension);
        fixture.Write("""{"Mail":{"credential":"manual-secret","custom":42}}""");
        var original = File.ReadAllText(fixture.FilePath);
        using var protector = new ControlledProtector(fixture.EncryptionKey)
        {
            AfterProtect = () => Assert.Equal(original, File.ReadAllText(fixture.FilePath))
        };
        using var file = fixture.Create(protector);
        var notifications = 0;
        var updates = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        if (operation == "save") file.Save(new CounterSettings { Value = 2 });
        else if (operation == "update")
            await file.UpdateAsync<CounterSettings>(counter => { updates++; counter.Value = 2; }, fixture.Token);
        else await file.ResetAsync<CounterSettings>(fixture.Token);
        Assert.Equal(operation == "update" ? 1 : 0, updates);
        Assert.Equal(1, notifications);
        Assert.Equal(1, protector.ProtectCalls);
        Assert.Equal("manual-secret", file.Read<MailSettings>().Password);
        Assert.Equal(operation == "reset" ? 0 : 2, file.Read<CounterSettings>().Value);
        Assert.DoesNotContain("manual-secret", File.ReadAllText(fixture.FilePath));
        Assert.Contains("custom", File.ReadAllText(fixture.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAutomaticProtectionKeepsThePreviousSnapshot(bool cancel)
    {
        using var fixture = new TestFile();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = fixture.Create(protector);
        file.Read<MailSettings>();
        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        fixture.Write("""{"Mail":{"port":465,"credential":"manual-secret"}}""");
        var original = File.ReadAllBytes(fixture.FilePath);
        protector.AfterProtect = () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new CryptographicException("Unavailable protection.");
        };
        var failure = await Record.ExceptionAsync(() => file.ReloadAsync(cancellation.Token));
        Assert.True(cancel ? failure is OperationCanceledException : failure is CryptographicException);
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task AutomaticProtectionRereadsAfterWaitingForTheFileLock()
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Mail":{"port":465,"credential":"old-secret"}}""");
        using var file = fixture.Create();
        Task<MailSettings> reading;
        using (var fileLock = new FileStream(fixture.FilePath + ".confio.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            reading = file.ReadAsync<MailSettings>(fixture.Token);
            Assert.False(reading.IsCompleted);
            // 模拟协调写入者完整替换文件，避免截断写入与首次异步读取竞争。
            var replacement = fixture.FilePath + ".replacement";
            File.WriteAllText(replacement, """{"Mail":{"port":25,"credential":"new-secret"},"External":42}""");
            File.Move(replacement, fixture.FilePath, overwrite: true);
        }
        var result = await reading;
        Assert.Equal(25, result.Port);
        Assert.Equal("new-secret", result.Password);
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(fixture.FilePath))!["External"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationAndProjectionFailuresPrecedeAutomaticProtection(bool native)
    {
        using var fixture = new TestFile();
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        fixture.Write(native
            ? """{"Mail":{"credential":"manual-secret"},"External":{"a:b":1,"a":{"b":2}}}"""
            : """{"Checked":{"Attempts":-1,"Secret":"valid-secret"}}""");
        var before = File.ReadAllBytes(fixture.FilePath);
        if (native)
        {
            using var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
            await Assert.ThrowsAsync<InvalidDataException>(() => new ServiceCollection().AddConfigurationFileAsync(configuration, TestSettingsContext.Default, fixture.FilePath, cancellationToken: fixture.Token, options: new ConfigurationFileOptions { Protector = protector }));
        }
        else
        {
            using var file = new ConfigurationFile(BehaviorSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { Protector = protector });
            await Assert.ThrowsAsync<ValidationException>(() => file.ReadAsync<CheckedSettings>(fixture.Token));
        }
        Assert.Equal(0, protector.ProtectCalls);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task WholeObjectsStopAtTheirProtectionBoundaryAndMissingDefaultsAreNotPersisted(string extension)
    {
        using var fixture = new TestFile(extension);
        fixture.Write("""{"Whole":{"Account":{"api/key~1":"enc:v1:literal-password"}}}""");
        using var file = new ConfigurationFile(WholeProtectedContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var value = await file.ReadAsync<WholeProtectedSettings>(fixture.Token);
        Assert.Equal("enc:v1:default", value.Password);
        Assert.Equal("enc:v1:literal-password", value.Account!.Token);
        Assert.DoesNotContain("Password", File.ReadAllText(fixture.FilePath));
        Assert.DoesNotContain("literal-password", File.ReadAllText(fixture.FilePath));
        await file.ReloadAsync(fixture.Token);
        Assert.Equal(value.Account.Token, file.Read<WholeProtectedSettings>().Account!.Token);
    }
}

[SettingsSection("Whole")]
public sealed class WholeProtectedSettings
{
    [Protected]
    public string Password { get; set; } = "enc:v1:default";
    [Protected]
    public EndpointSettings? Account { get; set; }
}

[JsonSerializable(typeof(WholeProtectedSettings))]
public partial class WholeProtectedContext : JsonSerializerContext { }

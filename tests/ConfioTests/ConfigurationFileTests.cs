using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConfioTests;

public sealed class ConfigurationFileTests
{
    [Theory]
    [InlineData(".json", false)]
    [InlineData(".json", true)]
    [InlineData(".yaml", false)]
    [InlineData(".yaml", true)]
    public async Task InitialPlaintextIsProtectedBeforeReturning(string extension, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        fixture.Write(extension == ".json"
            ? "{\"Mail\":{\"credential\":\"literal-password\"}}"
            : "Mail:\n  credential: 'literal-password'\n");
        var key = RandomNumberGenerator.GetBytes(32);
        using var file = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });

        var value = asynchronous
            ? await file.ReadAsync<MailSettings>(fixture.Token)
            : file.Read<MailSettings>();
        Assert.Equal("literal-password", value.Password);
        var persisted = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("literal-password", persisted);
        Assert.Contains("enc:v1:", persisted);

        using var reopened = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        Assert.Equal("literal-password", reopened.Read<MailSettings>().Password);
    }

    [Fact]
    public void SavingModelValueWithEncryptionPrefixTreatsItAsPlaintext()
    {
        using var fixture = new TestFile();
        var key = RandomNumberGenerator.GetBytes(32);
        using (var file = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key }))
        {
            file.Save(new MailSettings { Password = "enc:v1:literal-password" });
        }

        using var reopened = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        Assert.Equal("enc:v1:literal-password", reopened.Read<MailSettings>().Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonKeepsUnicodeReadableAndRoundTripsEscapedCharacters(bool asynchronous)
    {
        using var fixture = new TestFile();
        fixture.Write("""{"外部配置":{"标题":"保留中文"}}""");
        using var file = fixture.Create();
        const string label = "中文 café 日本語\n引号\"反斜杠\\制表\t空字符\0 <>& 😀";
        var value = new MailSettings { Label = label, Password = "测试保护内容" };
        if (asynchronous) await file.SaveAsync(value, fixture.Token);
        else file.Save(value);

        var text = File.ReadAllText(fixture.FilePath);
        Assert.Contains("中文 café 日本語", text);
        Assert.Contains("外部配置", text);
        Assert.Contains("保留中文", text);
        Assert.DoesNotContain("测试保护内容", text);
        Assert.Equal(label, JsonNode.Parse(text)!["Mail"]!["label"]!.GetValue<string>());
        using var reopened = fixture.Create();
        var copy = await reopened.ReadAsync<MailSettings>(fixture.Token);
        Assert.Equal(label, copy.Label);
        Assert.Equal(value.Password, copy.Password);
    }

    [Fact]
    public void WriterRejectsDocumentsDeeperThanTheReaderAccepts()
    {
        using var fixture = new TestFile();
        var context = new TestSettingsContext(new JsonSerializerOptions { MaxDepth = 128 });
        using var file = new ConfigurationFile(context, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new DepthSettings());
        var before = File.ReadAllBytes(fixture.FilePath);
        var value = new DepthSettings();
        var current = value;
        for (var index = 0; index < 70; index++)
        {
            current.Child = new DepthSettings();
            current = current.Child;
        }
        Assert.Throws<InvalidDataException>(() => file.Save(value));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Null(file.Read<DepthSettings>().Child);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".yml")]
    public async Task EmptyDefaultsAndMissingResetDoNotUseFilesOrKeys(string extension)
    {
        using var fixture = new TestFile(extension);
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = fixture.Create(protector);
        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal("", (await file.ReadAsync<MailSettings>(fixture.Token)).Password);
        file.Reset<MailSettings>();
        await file.ResetAsync<CounterSettings>(fixture.Token);
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        Assert.Equal(0, protector.ProtectCalls + protector.UnprotectCalls);
        file.Save(new MailSettings { Port = 0, Label = null });
        Assert.Equal(0, file.Read<MailSettings>().Port);
        Assert.Null(file.Read<MailSettings>().Label);
        Assert.Equal(0, protector.ProtectCalls + protector.UnprotectCalls);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task NestedCollectionsNullableValuesAndExplicitNamesRemainProtected(string extension)
    {
        using var fixture = new TestFile(extension);
        using (var file = fixture.Create())
        {
            var mail = new MailSettings { Value = new SecretValue { Secret = "secret-value" } };
            mail.Endpoint!.Token = "secret-endpoint";
            mail.Servers["a/b~c"] = new EndpointSettings { Token = "secret-dictionary" };
            mail.Items.Add(new EndpointSettings { Token = "secret-list" });
            await file.SaveAsync(mail, fixture.Token);
        }
        Assert.DoesNotContain("secret-", File.ReadAllText(fixture.FilePath));
        using var reopened = fixture.Create();
        var copy = reopened.Read<MailSettings>();
        Assert.Equal("secret-value", copy.Value!.Value.Secret);
        Assert.Equal("secret-endpoint", copy.Endpoint!.Token);
        Assert.Equal("secret-dictionary", copy.Servers["a/b~c"].Token);
        Assert.Equal("secret-list", copy.Items[0].Token);
        copy.Items[0].Token = "changed";
        Assert.Equal("secret-list", reopened.Read<MailSettings>().Items[0].Token);
    }

    [Fact]
    public void MissingValuesUseDefaultsWhileNullAndContainersReplaceThem()
    {
        using var fixture = new TestFile();
        fixture.Write("""
            { // 配置允许注释与尾逗号
              "Mail": { "PORT": 0, "label": null, "endpoint": { "name": "partial" }, "items": [], },
            }
            """);
        using var file = fixture.Create();
        var mail = file.Read<MailSettings>();
        Assert.Equal(0, mail.Port);
        Assert.Null(mail.Label);
        Assert.Equal(30, mail.Endpoint!.Timeout);
        Assert.Equal("partial", mail.Endpoint.Name);
        Assert.Empty(mail.Items);
        fixture.Write("{\"Mail\":{\"endpoint\":null}}");
        file.Reload();
        Assert.Null(file.Read<MailSettings>().Endpoint);
        Assert.Equal(587, file.Read<MailSettings>().Port);
    }

    [Theory]
    [InlineData("{\"Mail\":null}")]
    [InlineData("{\"Mail\":{\"port\":\"sensitive-input\"}}")]
    [InlineData("{\"Mail\":{\"port\":1,\"PORT\":2}}")]
    [InlineData("{\"Mail\":{\"port\":1,\"port\":2}}")]
    [InlineData("{\"Mail\":{\"credential\":{\"unexpected\":\"sensitive-input\"}}}")]
    [InlineData("{\"Mail\":{\"port\": sensitive-input}}")]
    [InlineData("[]")]
    public async Task InvalidReloadKeepsOldSnapshotAndSanitizesInput(string invalid)
    {
        using var fixture = new TestFile();
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465 });
        fixture.Write(invalid);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => file.ReloadAsync(fixture.Token));
        Assert.DoesNotContain("sensitive-input", failure.ToString());
        Assert.Equal(465, file.Read<MailSettings>().Port);
        Assert.Equal(invalid, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public async Task ExplicitSaveAndResetCanReplaceABrokenOwnedSection()
    {
        using var fixture = new TestFile();
        fixture.Write("{\"Mail\":{\"port\":\"bad\",\"credential\":\"plaintext\"},\"External\":42}");
        using var file = fixture.Create();
        await file.SaveAsync(new MailSettings { Port = 465 }, fixture.Token);
        Assert.Equal(465, file.Read<MailSettings>().Port);
        fixture.Write("{\"Mail\":null,\"External\":42}");
        await file.ResetAsync<MailSettings>(fixture.Token);
        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(fixture.FilePath))!["External"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public void UpdateFailureKeepsTheFileAndPublishedState(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465, Password = "secret-original" });
        var before = File.ReadAllBytes(fixture.FilePath);
        var calls = 0;
        Assert.Throws<ArithmeticException>(() => file.Update<MailSettings>(mail =>
        {
            calls++;
            mail.Port = 25;
            throw new ArithmeticException("business failure");
        }));
        Assert.Equal(1, calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(465, file.Read<MailSettings>().Port);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".toml")]
    [InlineData(".ini")]
    public async Task CancellationAfterProtectionDoesNotCommitOrPublish(string extension)
    {
        using var fixture = new TestFile(extension);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { Protector = protector });
        file.Save(new FormatSettings { Port = 465, Password = "secret-original" });
        var before = File.ReadAllBytes(fixture.FilePath);
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        protector.AfterProtect = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            file.SaveAsync(new FormatSettings { Port = 25, Password = "secret-cancelled" }, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(465, file.Read<FormatSettings>().Port);
        Assert.Equal("secret-original", file.Read<FormatSettings>().Password);
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".toml")]
    [InlineData(".ini")]
    public async Task CancellationWhileWaitingForFileLockLeavesOldData(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new FormatSettings { Port = 465, Password = "secret-original" });
        var before = File.ReadAllBytes(fixture.FilePath);
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        using var fileLock = new FileStream(fixture.FilePath + ".confio.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var saving = file.SaveAsync(new FormatSettings { Port = 25 }, cancellation.Token);
        Assert.False(saving.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(465, file.Read<FormatSettings>().Port);
        Assert.Equal("secret-original", file.Read<FormatSettings>().Password);
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadersAllowingReplacementKeepTheirDocumentWhileNewReadersSeeTheCommit(bool asynchronous)
    {
        using var fixture = new TestFile();
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465 });
        var original = File.ReadAllText(fixture.FilePath);
        using var stream = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        if (asynchronous) await file.UpdateAsync<MailSettings>(mail => mail.Port = 2525, fixture.Token);
        else file.Update<MailSettings>(mail => mail.Port = 2525);

        Assert.Equal(original, await reader.ReadToEndAsync(fixture.Token));
        using var reopened = fixture.Create();
        Assert.Equal(2525, reopened.Read<MailSettings>().Port);
        Assert.Equal(2525, file.Read<MailSettings>().Port);
    }

    [Fact]
    public async Task FirstLoadsAndQueuedSaveShareOneCoordinationBoundary()
    {
        using var fixture = new TestFile();
        using (var seed = fixture.Create())
        {
            seed.Save(new MailSettings { Port = 465, Password = "secret-original" });
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var protector = new ControlledProtector(fixture.EncryptionKey)
        {
            BeforeUnprotectAsync = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        using var file = fixture.Create(protector);
        var first = file.ReadAsync<MailSettings>(fixture.Token);
        await entered.Task.WaitAsync(fixture.Token);
        var second = file.ReadAsync<MailSettings>(fixture.Token);
        var save = file.SaveAsync(new MailSettings { Port = 2525 }, fixture.Token);
        release.SetResult();
        await Task.WhenAll(first, second, save).WaitAsync(fixture.Token);
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Equal(2525, file.Read<MailSettings>().Port);
    }

    [Fact]
    public async Task CancelledFirstLoadCanBeRetriedWithoutPublishingPartialState()
    {
        using var fixture = new TestFile();
        using (var seed = fixture.Create())
        {
            seed.Save(new MailSettings { Port = 465, Password = "secret-original" });
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var protector = new ControlledProtector(fixture.EncryptionKey)
        {
            BeforeUnprotectAsync = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        using var file = fixture.Create(protector);
        var reading = file.ReadAsync<MailSettings>(cancellation.Token);
        await entered.Task.WaitAsync(fixture.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        protector.BeforeUnprotectAsync = null;
        Assert.Equal(465, (await file.ReadAsync<MailSettings>(fixture.Token)).Port);
    }

    [Fact]
    public async Task ConcurrentUpdatesReadLatestCommittedValues()
    {
        using var fixture = new TestFile();
        using var first = fixture.Create();
        using var second = fixture.Create();
        var calls = 0;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            (index % 2 == 0 ? first : second).UpdateAsync<CounterSettings>(counter =>
            {
                Interlocked.Increment(ref calls);
                counter.Value++;
            }, fixture.Token))).WaitAsync(fixture.Token);
        first.Reload();
        Assert.Equal(24, first.Read<CounterSettings>().Value);
        Assert.Equal(24, calls);
    }

    [Fact]
    public async Task UpdateWaitsForTheLockThenUsesTheLatestFileExactlyOnce()
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Counters":{"Primary":{"value":4}},"External":42}""");
        using var file = fixture.Create();
        Assert.Equal(4, file.Read<CounterSettings>().Value);
        var calls = 0;
        var notifications = 0;
        using var subscription = file.OnChange<CounterSettings>(_ => notifications++);
        Task updating;
        using (var held = new FileStream(fixture.FilePath + ".confio.lock", FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            updating = file.UpdateAsync<CounterSettings>(value =>
            {
                calls++;
                value.Value++;
            }, fixture.Token);
            Assert.False(updating.IsCompleted);
            Assert.Equal(0, calls);
            fixture.Write("""{"Counters":{"Primary":{"value":40}},"External":43}""");
        }
        await updating.WaitAsync(fixture.Token);
        Assert.Equal(1, calls);
        Assert.Equal(1, notifications);
        Assert.Equal(41, file.Read<CounterSettings>().Value);
        Assert.Equal(43, JsonNode.Parse(File.ReadAllText(fixture.FilePath))!["External"]!.GetValue<int>());
        Assert.True(File.Exists(fixture.FilePath + ".confio.lock"));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLockPathFailsWithoutRunningTheUpdateOrPublishing(bool asynchronous)
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Mail":{"port":465}}""");
        using var file = fixture.Create();
        Assert.Equal(465, file.Read<MailSettings>().Port);
        Directory.CreateDirectory(fixture.FilePath + ".confio.lock");
        var before = File.ReadAllBytes(fixture.FilePath);
        var calls = 0;
        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        void Edit(MailSettings mail) { calls++; mail.Port = 25; }
        var failure = asynchronous
            ? await Record.ExceptionAsync(() => file.UpdateAsync<MailSettings>(Edit, fixture.Token))
            : Record.Exception(() => file.Update<MailSettings>(Edit));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(0, calls);
        Assert.Equal(0, notifications);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(465, file.Read<MailSettings>().Port);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("ciphertext")]
    public void AuthenticatedPayloadsRejectWrongPurposeOrCorruption(string change)
    {
        using var fixture = new TestFile();
        using (var seed = fixture.Create())
        {
            seed.Save(new MailSettings { Password = "secret-authenticated" });
        }
        var document = JsonNode.Parse(File.ReadAllText(fixture.FilePath))!;
        if (change == "path")
        {
            document["Mail"]!["endpoint"]!["api/key~1"] = document["Mail"]!["credential"]!.DeepClone();
            document["Mail"]!["credential"] = "";
        }
        else if (change == "ciphertext")
        {
            var protectedText = document["Mail"]!["credential"]!.GetValue<string>();
            var bytes = Convert.FromBase64String(protectedText["enc:v1:".Length..]);
            bytes[bytes.Length / 2] ^= 0xff;
            document["Mail"]!["credential"] = "enc:v1:" + Convert.ToBase64String(bytes);
        }
        fixture.Write(document.ToJsonString());
        using var file = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var failure = Assert.ThrowsAny<CryptographicException>(() => file.Read<MailSettings>());
        Assert.DoesNotContain("secret-authenticated", failure.ToString());
    }

    [Fact]
    public void RegistrationFreezesSelectionsAndRejectsInvalidMetadataBeforeIo()
    {
        using var fixture = new TestFile();
        var contexts = new ISettingsContext[] { TestSettingsContext.Default };
        var services = new ServiceCollection().AddConfigurationFile(contexts, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        contexts[0] = null!;
        using var provider = services.BuildServiceProvider();
        var file = provider.GetRequiredService<ConfigurationFile>();
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        Assert.Throws<ArgumentException>(() => new ConfigurationFile(new[] { TestSettingsContext.Default, TestSettingsContext.Default }, fixture.FilePath));
        var invalid = new TestSettingsContext(new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(invalid, fixture.FilePath));
        file.Dispose();
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => file.Read<MailSettings>());
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConfioTests;

public sealed class ConfigurationOptionsTests
{
    [Theory]
    [InlineData(ConfigurationFormat.Json)]
    [InlineData(ConfigurationFormat.Yaml)]
    [InlineData(ConfigurationFormat.Toml)]
    [InlineData(ConfigurationFormat.Ini)]
    public async Task RootModelAndExplicitFormatSupportCustomFileNames(ConfigurationFormat format)
    {
        using var fixture = new TestFile(".conf");
        var options = new ConfigurationFileOptions { Format = format, EncryptionKey = fixture.EncryptionKey };
        using var file = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        Assert.Equal(587, file.Read<RootSettings>().Port);
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        await file.SaveAsync(new RootSettings { Port = 0, SecretNumber = 42 }, fixture.Token);
        using var reopened = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        Assert.Equal(0, reopened.Read<RootSettings>().Port);
        Assert.Equal(42, reopened.Read<RootSettings>().SecretNumber);
        Assert.Contains("enc:v1:", File.ReadAllText(fixture.FilePath));
        await reopened.ResetAsync<RootSettings>(fixture.Token);
        Assert.Equal(587, reopened.Read<RootSettings>().Port);
        using var reset = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        Assert.Equal(587, reset.Read<RootSettings>().Port);
    }

    [Fact]
    public void DefaultPathsAndProtectionNeedNoApplicationIdentifierOrIo()
    {
        using var file = new ConfigurationFile(RootContext.Default);
        Assert.True(Path.IsPathRooted(file.Path));
        Assert.Equal("appsettings.json", Path.GetFileName(file.Path));
        Assert.Equal(OperatingSystem.IsWindows(), file.KeyFilePath is null);
        using var toml = new ConfigurationFile(RootContext.Default, options: new ConfigurationFileOptions { Format = ConfigurationFormat.Toml });
        Assert.Equal("appsettings.toml", Path.GetFileName(toml.Path));
        using var aes = new ConfigurationFile(RootContext.Default, options: new ConfigurationFileOptions { Protection = ConfigurationProtection.AesGcm });
        Assert.True(Path.IsPathRooted(aes.KeyFilePath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticKeyIsLazyStableAndMissingKeysNeverOverwriteCiphertext(bool asynchronous)
    {
        using var fixture = new TestFile();
        var keyPath = Path.Combine(fixture.DirectoryPath, "private", "automatic.key");
        var options = new ConfigurationFileOptions { KeyFilePath = keyPath };
        using (var file = new ConfigurationFile(RootContext.Default, fixture.FilePath, options))
        {
            Assert.Equal(keyPath, file.KeyFilePath);
            Assert.Equal(587, file.Read<RootSettings>().Port);
            Assert.False(Directory.Exists(fixture.DirectoryPath));
            if (asynchronous) await file.SaveAsync(new RootSettings { SecretNumber = 123 }, fixture.Token);
            else file.Save(new RootSettings { SecretNumber = 123 });
        }
        var keyBytes = File.ReadAllBytes(keyPath);
        using (var file = new ConfigurationFile(RootContext.Default, fixture.FilePath, options))
        {
            var model = asynchronous ? await file.ReadAsync<RootSettings>(fixture.Token) : file.Read<RootSettings>();
            Assert.Equal(123, model.SecretNumber);
            file.Save(model);
        }
        Assert.Equal(keyBytes, File.ReadAllBytes(keyPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
        var ciphertext = File.ReadAllBytes(fixture.FilePath);
        File.Delete(keyPath);
        using var missing = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        if (asynchronous) await Assert.ThrowsAsync<FileNotFoundException>(() => missing.ReadAsync<RootSettings>(fixture.Token));
        else Assert.Throws<FileNotFoundException>(() => missing.Read<RootSettings>());
        Assert.False(File.Exists(keyPath));
        Assert.Equal(ciphertext, File.ReadAllBytes(fixture.FilePath));
    }

    [Fact]
    public async Task SimultaneousFilesUseOnePersistedAutomaticKey()
    {
        using var fixture = new TestFile();
        var options = new ConfigurationFileOptions { KeyFilePath = Path.Combine(fixture.DirectoryPath, "private", "shared.key") };
        using var first = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        using var second = new ConfigurationFile(RootContext.Default, Path.Combine(fixture.DirectoryPath, "second.json"), options);
        await Task.WhenAll(first.SaveAsync(new RootSettings { SecretNumber = 17 }, fixture.Token),
            second.SaveAsync(new RootSettings { SecretNumber = 23 }, fixture.Token));
        using var reopenedFirst = new ConfigurationFile(RootContext.Default, first.Path, options);
        using var reopenedSecond = new ConfigurationFile(RootContext.Default, second.Path, options);
        Assert.Equal(17, reopenedFirst.Read<RootSettings>().SecretNumber);
        Assert.Equal(23, reopenedSecond.Read<RootSettings>().SecretNumber);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticKeyReadAllowsAnOutstandingDeleteHandle(bool asynchronous)
    {
        using var fixture = new TestFile();
        var keyPath = Path.Combine(fixture.DirectoryPath, "private", "shared.key");
        var options = new ConfigurationFileOptions { KeyFilePath = keyPath };
        using (var original = new ConfigurationFile(RootContext.Default, fixture.FilePath, options))
            original.Save(new RootSettings { SecretNumber = 42 });
        var ciphertext = File.ReadAllBytes(fixture.FilePath);

        // Windows 重命名持有删除权限；用测试密钥的 DeleteOnClose 句柄稳定覆盖相同共享契约。
        using var keyReader = new FileStream(keyPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        using var reopened = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        var model = asynchronous ? await reopened.ReadAsync<RootSettings>(fixture.Token) : reopened.Read<RootSettings>();
        Assert.Equal(42, model.SecretNumber);
        Assert.Equal(ciphertext, File.ReadAllBytes(fixture.FilePath));
    }

    [Fact]
    public async Task ReadOnlyPlaintextLoadingDefersProtectionUntilExplicitSave()
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Port":465,"SecretNumber":42}""");
        var before = File.ReadAllBytes(fixture.FilePath);
        var options = new ConfigurationFileOptions
        {
            KeyFilePath = Path.Combine(fixture.DirectoryPath, "private", "key"),
            ProtectPlaintextOnLoad = false
        };
        using var file = new ConfigurationFile(RootContext.Default, fixture.FilePath, options);
        var value = await file.ReadAsync<RootSettings>(fixture.Token);
        await file.ReloadAsync(fixture.Token);
        Assert.Equal(42, value.SecretNumber);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.False(File.Exists(options.KeyFilePath));
        await file.SaveAsync(value, fixture.Token);
        Assert.True(File.Exists(options.KeyFilePath));
        Assert.Contains("enc:v1:", File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void CorruptKeyAndConflictingOptionsFailWithoutReplacingFiles()
    {
        using var fixture = new TestFile();
        fixture.Write("{}");
        var keyPath = Path.Combine(fixture.DirectoryPath, "broken.key");
        File.WriteAllBytes(keyPath, new byte[3]);
        using var broken = new ConfigurationFile(RootContext.Default, fixture.FilePath, new ConfigurationFileOptions { KeyFilePath = keyPath });
        var error = Record.Exception(() => broken.Save(new RootSettings { SecretNumber = 42 }));
        Assert.True(error is CryptographicException or InvalidDataException);
        Assert.Equal("{}", File.ReadAllText(fixture.FilePath));
        Assert.Equal(new byte[3], File.ReadAllBytes(keyPath));
        Assert.Throws<ArgumentException>(() => new ConfigurationFile(RootContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey, KeyFilePath = keyPath }));
        Assert.Throws<ArgumentException>(() => new ConfigurationFile(RootContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Protection = ConfigurationProtection.Dpapi, KeyFilePath = keyPath }));
        Assert.Throws<ArgumentException>(() => new ConfigurationFile(RootContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { KeyFilePath = fixture.FilePath }));
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => new ConfigurationFile(RootContext.Default, fixture.FilePath,
                new ConfigurationFileOptions { Protection = ConfigurationProtection.Dpapi }));
    }

    [Theory]
    [InlineData(ConfigurationFormat.Json, false)]
    [InlineData(ConfigurationFormat.Json, true)]
    [InlineData(ConfigurationFormat.Yaml, false)]
    [InlineData(ConfigurationFormat.Yaml, true)]
    [InlineData(ConfigurationFormat.Toml, false)]
    [InlineData(ConfigurationFormat.Toml, true)]
    public void DictionaryKeysRetainCaseAndTheirProtectedLocations(ConfigurationFormat format, bool caseInsensitive)
    {
        using var fixture = new TestFile(".conf");
        var options = new ConfigurationFileOptions { Format = format, EncryptionKey = fixture.EncryptionKey };
        var context = new CaseContext(new JsonSerializerOptions { PropertyNameCaseInsensitive = caseInsensitive });
        using var file = new ConfigurationFile(context, fixture.FilePath, options);
        file.Save(new CaseSettings
        {
            Entries = new Dictionary<string, CaseEntry>
            {
                ["A"] = new CaseEntry { Secret = "upper" }, ["a"] = new CaseEntry { Secret = "lower" }
            }
        });
        using var reopened = new ConfigurationFile(context, fixture.FilePath, options);
        var model = reopened.Read<CaseSettings>();
        Assert.Equal("upper", model.Entries["A"].Secret);
        Assert.Equal("lower", model.Entries["a"].Secret);
        using var configuration = new ConfigurationManager();
        Assert.Throws<InvalidDataException>(() => new ServiceCollection().AddConfigurationFile(configuration, reopened));
        Assert.Equal("upper", reopened.Read<CaseSettings>().Entries["A"].Secret);
    }

    [Fact]
    public void JsonContextControlsPropertyMatchingAndErrorsLocateTheMember()
    {
        using var fixture = new TestFile();
        fixture.Write("""{"port":465}""");
        using var strict = new ConfigurationFile(RootContext.Default, fixture.FilePath);
        Assert.Equal(587, strict.Read<RootSettings>().Port);
        using var insensitive = new ConfigurationFile(new RootContext(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }), fixture.FilePath);
        Assert.Equal(465, insensitive.Read<RootSettings>().Port);
        fixture.Write("""{"Port":"not-an-integer"}""");
        var error = Assert.Throws<InvalidDataException>(() => strict.Reload());
        Assert.Contains("Port", error.Message);
        Assert.DoesNotContain("not-an-integer", error.Message);
        Assert.Equal(587, strict.Read<RootSettings>().Port);
    }

    [Theory]
    [InlineData(ConfigurationFormat.Json, "{\"secretnumber\":42}")]
    [InlineData(ConfigurationFormat.Yaml, "secretnumber: 42\n")]
    [InlineData(ConfigurationFormat.Toml, "secretnumber = 42\n")]
    [InlineData(ConfigurationFormat.Ini, "secretnumber=42\n")]
    public async Task CaseInsensitivePlaintextProtectionReplacesTheOriginalProperty(ConfigurationFormat format, string content)
    {
        using var fixture = new TestFile(".conf");
        fixture.Write(content);
        var options = new ConfigurationFileOptions { Format = format, EncryptionKey = fixture.EncryptionKey };
        var context = new RootContext(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        using var file = new ConfigurationFile(context, fixture.FilePath, options);
        Assert.Equal(42, (await file.ReadAsync<RootSettings>(fixture.Token)).SecretNumber);
        var encrypted = File.ReadAllText(fixture.FilePath);
        Assert.Contains("secretnumber", encrypted);
        Assert.DoesNotContain("SecretNumber", encrypted);
        Assert.Equal(1, encrypted.Split("enc:v1:").Length - 1);
        using var reopened = new ConfigurationFile(context, fixture.FilePath, options);
        Assert.Equal(42, reopened.Read<RootSettings>().SecretNumber);
        Assert.Equal(encrypted, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void IniUsesCaseInsensitivePathsAndRejectsCaseCollisionsBeforeSaving()
    {
        using var fixture = new TestFile(".ini");
        fixture.Write("[cases]\nENTRIES:a:SECRET=first\nentries:b:secret=second\n");
        var options = new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey };
        using var file = new ConfigurationFile(CaseContext.Default, fixture.FilePath, options);
        var value = file.Read<CaseSettings>();
        Assert.Equal("first", value.Entries["a"].Secret);
        Assert.Equal("second", value.Entries["b"].Secret);
        var before = File.ReadAllBytes(fixture.FilePath);
        Assert.DoesNotContain("first", File.ReadAllText(fixture.FilePath));
        using var reopened = new ConfigurationFile(CaseContext.Default, fixture.FilePath, options);
        Assert.Equal("second", reopened.Read<CaseSettings>().Entries["b"].Secret);
        value.Entries.Add("A", new CaseEntry { Secret = "collision" });
        Assert.Throws<ConfigurationValueException>(() => file.Save(value));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(2, file.Read<CaseSettings>().Entries.Count);
    }

    [Fact]
    public void ContainerBindsDifferentModelsToTheirOwnFilesAndFreezesOptions()
    {
        using var fixture = new TestFile();
        var options = new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey.ToArray() };
        var services = new ServiceCollection();
        services.AddConfigurationFile(RootContext.Default, fixture.FilePath, options);
        services.AddConfigurationFile(CaseContext.Default, Path.Combine(fixture.DirectoryPath, "cases.json"), options);
        Array.Clear(options.EncryptionKey!, 0, options.EncryptionKey!.Length);
        options.KeyFilePath = "invalid-after-registration";
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISettings<RootSettings>>().Save(new RootSettings { SecretNumber = 29 });
        provider.GetRequiredService<ISettings<CaseSettings>>().Save(new CaseSettings { Name = "separate" });
        using var reopened = new ConfigurationFile(RootContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Equal(29, reopened.Read<RootSettings>().SecretNumber);
        Assert.DoesNotContain("separate", File.ReadAllText(fixture.FilePath));
        Assert.Equal(2, provider.GetServices<ConfigurationFile>().Count());
        Assert.Throws<InvalidOperationException>(() => services.AddConfigurationFile(RootContext.Default, fixture.FilePath));
    }
}

[SettingsSection]
public sealed class RootSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Port { get; set; } = 587;

    [Protected]
    public int SecretNumber { get; set; }
}

[JsonSerializable(typeof(RootSettings))]
public partial class RootContext : JsonSerializerContext;

[SettingsSection("Cases")]
public sealed class CaseSettings
{
    public string Name { get; set; } = "default";
    public Dictionary<string, CaseEntry> Entries { get; set; } = new();
}

public sealed class CaseEntry
{
    [Protected]
    public string Secret { get; set; } = "";
}

[JsonSerializable(typeof(CaseSettings))]
public partial class CaseContext : JsonSerializerContext;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConfioTests;

public sealed class EncodingTests
{
    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".toml")]
    [InlineData(".ini")]
    public void DefaultWritesUtf8WithoutBom(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new FormatSettings { Code = "中文配置" });
        var bytes = File.ReadAllBytes(fixture.FilePath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("中文配置", new UTF8Encoding(false, true).GetString(bytes));
    }

    [Theory]
    [InlineData(".json", "utf8-bom", false)]
    [InlineData(".yaml", "utf8-bom", true)]
    [InlineData(".toml", "utf8-bom", false)]
    [InlineData(".ini", "utf8-bom", true)]
    [InlineData(".json", "utf16", true)]
    [InlineData(".yaml", "utf16", false)]
    [InlineData(".json", "utf16-no-bom", false)]
    [InlineData(".yaml", "utf16be", true)]
    [InlineData(".ini", "utf16be", false)]
    [InlineData(".json", "utf32", false)]
    [InlineData(".yaml", "utf32be", true)]
    [InlineData(".ini", "utf32", true)]
    [InlineData(".json", "gbk", true)]
    [InlineData(".ini", "gbk", false)]
    [InlineData(".ini", "default", true)]
    public async Task ConfiguredEncodingCoversReadSaveUpdateResetAndReload(string extension, string name, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        var encoding = SelectEncoding(name);
        var options = new ConfigurationFileOptions { Encoding = encoding, EncryptionKey = fixture.EncryptionKey };
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options);
        Assert.Equal("00123", file.Read<FormatSettings>().Code);
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        var model = new FormatSettings { Code = "中文配置 café", Password = "保护中文😀" };
        if (asynchronous) await file.SaveAsync(model, fixture.Token);
        else file.Save(model);

        var bytes = File.ReadAllBytes(fixture.FilePath);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Contains("中文配置 café", File.ReadAllText(fixture.FilePath, encoding));
        Assert.DoesNotContain(model.Password, File.ReadAllText(fixture.FilePath, encoding));
        using var external = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options);
        var read = asynchronous ? await external.ReadAsync<FormatSettings>(fixture.Token) : external.Read<FormatSettings>();
        Assert.Equal(model.Code, read.Code);
        Assert.Equal(model.Password, read.Password);
        if (asynchronous)
        {
            await external.UpdateAsync<FormatSettings>(value => value.Code = "外部更新", fixture.Token);
            await external.SaveAsync(new FormatCounter { Value = 9 }, fixture.Token);
            await external.ResetAsync<FormatCounter>(fixture.Token);
            await file.ReloadAsync(fixture.Token);
        }
        else
        {
            external.Update<FormatSettings>(value => value.Code = "外部更新");
            external.Save(new FormatCounter { Value = 9 });
            external.Reset<FormatCounter>();
            file.Reload();
        }
        Assert.Equal("外部更新", file.Read<FormatSettings>().Code);
        Assert.Equal(model.Password, file.Read<FormatSettings>().Password);
        Assert.Equal(3, file.Read<FormatCounter>().Value);
        Assert.True(File.ReadAllBytes(fixture.FilePath).AsSpan().StartsWith(encoding.GetPreamble()));
    }

    [Theory]
    [InlineData(".json", "utf8-bom")]
    [InlineData(".json", "utf16")]
    [InlineData(".yaml", "utf16be")]
    [InlineData(".ini", "utf32")]
    [InlineData(".yaml", "utf32be")]
    [InlineData(".toml", "utf8-bom")]
    public async Task BomDeterminesInputWhileSavingUsesTheConfiguredEncoding(string extension, string name)
    {
        using var fixture = new TestFile(extension);
        Directory.CreateDirectory(fixture.DirectoryPath);
        var source = extension switch
        {
            ".json" => "{\"Mail\":{\"Code\":\"手写中文\"}}",
            ".yaml" => "Mail:\n  Code: 手写中文\n",
            ".toml" => "[Mail]\nCode = \"手写中文\"\n",
            _ => "[Mail]\nCode=手写中文\n"
        };
        File.WriteAllText(fixture.FilePath, source, SelectEncoding(name));
        var original = File.ReadAllBytes(fixture.FilePath);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Equal("手写中文", (await file.ReadAsync<FormatSettings>(fixture.Token)).Code);
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
        await file.UpdateAsync<FormatSettings>(model => model.Code = "更新中文", fixture.Token);
        var saved = File.ReadAllBytes(fixture.FilePath);
        Assert.False(saved.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("更新中文", new UTF8Encoding(false, true).GetString(saved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticProtectionRetainsInputOnEncodingFailureThenWritesTheSelectedEncoding(bool asynchronous)
    {
        using var fixture = new TestFile(".ini");
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, "[Mail]\nCode=😀\nPassword=手写秘密\n", Encoding.UTF8);
        var original = File.ReadAllBytes(fixture.FilePath);
        var encoding = SelectEncoding("gbk");
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Encoding = encoding, EncryptionKey = fixture.EncryptionKey });
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        var error = asynchronous
            ? await Assert.ThrowsAsync<InvalidDataException>(() => file.ReadAsync<FormatSettings>(fixture.Token))
            : Assert.Throws<InvalidDataException>(() => file.Read<FormatSettings>());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("手写秘密", error.ToString());
        Assert.DoesNotContain("😀", error.ToString());
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(0, notifications);

        File.WriteAllText(fixture.FilePath, "[Mail]\nCode=手写中文\nPassword=手写秘密\n", Encoding.UTF8);
        var model = asynchronous ? await file.ReadAsync<FormatSettings>(fixture.Token) : file.Read<FormatSettings>();
        Assert.Equal("手写中文", model.Code);
        Assert.Equal("手写秘密", model.Password);
        Assert.False(File.ReadAllBytes(fixture.FilePath).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var text = encoding.GetString(File.ReadAllBytes(fixture.FilePath));
        Assert.Contains("手写中文", text);
        Assert.Contains("enc:v1:", text);
        Assert.DoesNotContain("手写秘密", text);
        Assert.DoesNotContain("Port", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LossyOutputFailsWithoutChangingFilesSnapshotsOrNotifications(bool asynchronous)
    {
        using var fixture = new TestFile(".ini");
        var supplied = (Encoding)SelectEncoding("gbk").Clone();
        supplied.EncoderFallback = new EncoderReplacementFallback("replacement");
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Encoding = supplied, EncryptionKey = fixture.EncryptionKey });
        Assert.IsType<EncoderReplacementFallback>(supplied.EncoderFallback);
        file.Save(new FormatSettings { Code = "原始中文" });
        var original = File.ReadAllBytes(fixture.FilePath);
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        if (asynchronous)
            await Assert.ThrowsAsync<InvalidDataException>(() => file.UpdateAsync<FormatSettings>(value => value.Code = "😀", fixture.Token));
        else
            Assert.Throws<InvalidDataException>(() => file.Save(new FormatSettings { Code = "😀" }));
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal("原始中文", file.Read<FormatSettings>().Code);
        Assert.Equal(0, notifications);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(".json", "utf8", "FF", false)]
    [InlineData(".yaml", "utf8", "FFFE4100FF", true)]
    [InlineData(".ini", "gbk", "81", true)]
    [InlineData(".ini", "utf8", "FFFE000000001100", false)]
    [InlineData(".toml", "utf8", "C328", true)]
    public async Task InvalidEncodedInputKeepsTheLastSnapshot(string extension, string name, string hex, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Encoding = SelectEncoding(name), EncryptionKey = fixture.EncryptionKey });
        file.Save(new FormatSettings { Code = "原始中文" });
        var malformed = Convert.FromHexString(hex);
        File.WriteAllBytes(fixture.FilePath, malformed);
        if (asynchronous) await Assert.ThrowsAsync<InvalidDataException>(() => file.ReloadAsync(fixture.Token));
        else Assert.Throws<InvalidDataException>(file.Reload);
        Assert.Equal("原始中文", file.Read<FormatSettings>().Code);
        Assert.Equal(malformed, File.ReadAllBytes(fixture.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerAndNativeViewsUseEncodingFixedAtRegistration(bool native)
    {
        using var fixture = new TestFile(".ini");
        var encoding = SelectEncoding("gbk");
        var options = new ConfigurationFileOptions { Encoding = encoding, EncryptionKey = fixture.EncryptionKey };
        var services = new ServiceCollection();
        using var configuration = new ConfigurationManager();
        if (native)
            await services.AddConfigurationFileAsync(configuration, FormatSettingsContext.Default, fixture.FilePath,
                options: options, cancellationToken: fixture.Token);
        else
            services.AddConfigurationFile(FormatSettingsContext.Default, fixture.FilePath, options);
        options.Encoding = Encoding.Unicode;
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettings<FormatSettings>>();
        await settings.SaveAsync(new FormatSettings { Code = "容器中文", Password = "加密中文😀" }, fixture.Token);
        Assert.Contains("容器中文", encoding.GetString(File.ReadAllBytes(fixture.FilePath)));
        if (native)
        {
            Assert.Equal("容器中文", configuration["Mail:Code"]);
            Assert.Equal("加密中文😀", provider.GetRequiredService<IOptionsMonitor<FormatSettings>>().CurrentValue.Password);
        }
        using var reopened = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Encoding = encoding, EncryptionKey = fixture.EncryptionKey });
        Assert.Equal("容器中文", reopened.Read<FormatSettings>().Code);
    }

    [Theory]
    [InlineData(".toml", "utf16")]
    [InlineData(".toml", "gbk")]
    [InlineData(".yaml", "gbk")]
    public void UnsupportedFormatEncodingIsRejectedBeforeIo(string extension, string name)
    {
        using var fixture = new TestFile(extension);
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { Encoding = SelectEncoding(name) }));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }

    [Fact]
    public async Task TomlRejectsAUnicodeBomThatIsNotUtf8()
    {
        using var fixture = new TestFile(".toml");
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, "[Mail]\nCode=\"中文\"", Encoding.Unicode);
        var original = File.ReadAllBytes(fixture.FilePath);
        using var file = new ConfigurationFile(FormatSettingsContext.Default, fixture.FilePath);
        await Assert.ThrowsAsync<NotSupportedException>(() => file.ReadAsync<FormatSettings>(fixture.Token));
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));
    }

    private static Encoding SelectEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16" => Encoding.Unicode,
        "utf16-no-bom" => new UnicodeEncoding(false, false, true),
        "utf16be" => Encoding.BigEndianUnicode,
        "utf32" => Encoding.UTF32,
        "utf32be" => new UTF32Encoding(true, true, true),
        "gbk" => CodePagesEncodingProvider.Instance.GetEncoding("gbk")!,
        "default" => Encoding.Default,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}

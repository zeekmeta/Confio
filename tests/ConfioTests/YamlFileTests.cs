using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using Xunit;

namespace ConfioTests;

public sealed class YamlFileTests
{
    [Theory]
    [InlineData(".yaml")]
    [InlineData(".YML")]
    public async Task SaveAndResetPreserveOtherSectionDataAndCiphertext(string extension)
    {
        using var fixture = new TestFile(extension);
        fixture.Write("\uFEFF" + """
            %YAML 1.2
            ---
            # 支持手写配置与 Core 标量
            Mail:
              PORT: 0o721
              label: ~
              endpoint:
                name: |-
                  第一行
                  第二行
              items: []
            External:
              Large: 900719925474099312345678901234567890
              Decimal: +000.123456789012345678901234567890123450
              Exponent: 001.2300e+2000
              Hex: 0x10000000000000001
              Leading: '00123'
              BooleanText: 'true'
              NullText: 'null'
              Explicit: !!str 00123
              Date: 2026-09-15
              LegacyBoolean: yes
              Zero: -0.0
              Sequence: [null, false, 'false', 0, '0']
              Empty: {}
            """);
        using var file = fixture.Create();
        var mail = await file.ReadAsync<MailSettings>(fixture.Token);
        Assert.Equal(465, mail.Port);
        Assert.Null(mail.Label);
        Assert.Equal("第一行\n第二行", mail.Endpoint!.Name);
        Assert.Equal(30, mail.Endpoint.Timeout);
        Assert.Empty(mail.Items);
        mail.Password = "yaml-protected-secret";
        file.Save(mail);
        var ciphertext = Ciphertexts(fixture.FilePath);
        Assert.Single(ciphertext);
        Assert.DoesNotContain("yaml-protected-secret", File.ReadAllText(fixture.FilePath));
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, file);
        using var provider = nativeServices.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var before = configuration.GetSection("External").AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value);
        await file.SaveAsync(new CounterSettings { Value = 2 }, fixture.Token);
        Assert.Equal(ciphertext, Ciphertexts(fixture.FilePath));
        await file.ReloadAsync(fixture.Token);
        Assert.Equal("900719925474099312345678901234567890", configuration["External:Large"]);
        Assert.Equal("0.123456789012345678901234567890123450", configuration["External:Decimal"]);
        Assert.Equal("1.2300e+2000", configuration["External:Exponent"]);
        Assert.Equal("18446744073709551617", configuration["External:Hex"]);
        Assert.Equal("00123", configuration["External:Leading"]);
        Assert.Equal("true", configuration["External:BooleanText"]);
        Assert.Equal("null", configuration["External:NullText"]);
        Assert.Equal("00123", configuration["External:Explicit"]);
        Assert.Equal("2026-09-15", configuration["External:Date"]);
        Assert.Equal("yes", configuration["External:LegacyBoolean"]);
        Assert.Equal("-0.0", configuration["External:Zero"]);
        Assert.Equal("False", configuration["External:Sequence:1"]);
        Assert.Equal("false", configuration["External:Sequence:2"]);
        Assert.Equal("yaml-protected-secret", file.Read<MailSettings>().Password);
        file.Reset<MailSettings>();
        await file.ResetAsync<CounterSettings>(fixture.Token);
        file.Reload();
        Assert.Equal(before, configuration.GetSection("External").AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Equal(587, file.Read<MailSettings>().Port);
    }

    [Theory]
    [InlineData("+000123", "123")]
    [InlineData("-000123", "-123")]
    [InlineData(".5", "0.5")]
    [InlineData("1.", "1.0")]
    [InlineData("01.e+003", "1.0e+003")]
    [InlineData("!!float '12'", "12.0")]
    [InlineData("!!int '0o17'", "15")]
    [InlineData("!!bool 'TRUE'", "true")]
    [InlineData("!!null ''", "null")]
    [InlineData("! 00123", "\"00123\"")]
    [InlineData("0b10", "\"0b10\"")]
    [InlineData("1_000", "\"1_000\"")]
    [InlineData("+0x10", "\"+0x10\"")]
    [InlineData("tRuE", "\"tRuE\"")]
    [InlineData("ON", "\"ON\"")]
    [InlineData("2026-09-15", "\"2026-09-15\"")]
    public void CoreScalarSemanticsSurviveWriting(string yaml, string expectedJson)
    {
        using var fixture = new TestFile(".yaml");
        fixture.Write("Scalar: {Value: " + yaml + "}");
        using var file = new ConfigurationFile(YamlScalarContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var value = file.Read<YamlScalarSettings>();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), value.Value));
        file.Save(value);
        file.Reload();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), file.Read<YamlScalarSettings>().Value));
    }

    [Fact]
    public void CompletedAliasesExpandWithoutSharingMutableSections()
    {
        using var fixture = new TestFile(".yaml");
        fixture.Write("""
            Template: &mail {port: 465, label: original}
            Mail: *mail
            Alias: *mail
            Old: &same old
            First: *same
            New: &same new
            Last: *same
            Empty: &empty null
            NullAlias: *empty
            Merge: {<<: {port: 25}}
            """);
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        using var provider = nativeServices.BuildServiceProvider();
        var file = provider.GetRequiredService<ConfigurationFile>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        Assert.Equal(465, file.Read<MailSettings>().Port);
        file.Update<MailSettings>(mail => mail.Port = 2525);
        file.Reload();
        Assert.Equal("465", configuration["Template:port"]);
        Assert.Equal("465", configuration["Alias:port"]);
        Assert.Equal("2525", configuration["Mail:port"]);
        Assert.Equal("old", configuration["First"]);
        Assert.Equal("new", configuration["Last"]);
        Assert.Null(configuration["NullAlias"]);
        Assert.Equal("25", configuration["Merge:<<:port"]);
    }

    [Fact]
    public void StringEscapingAndNumericOutputDoNotDependOnTheCurrentCulture()
    {
        using var fixture = new TestFile(".yaml");
        using var file = new ConfigurationFile(YamlScalarContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var strings = new[]
            {
                "",
                " ",
                "true",
                "null",
                "00123",
                ".nan",
                "# value",
                "a: b",
                "a\nb",
                "a\r\nb",
                "a\u0085b",
                "a\u2028b",
                "中文 🔐",
                "\0\t\"\\"
            };
            var node = new JsonObject();
            foreach (var value in strings)
            {
                node.Add(value, JsonValue.Create(value));
            }

            node.Add("number", JsonValue.Create(0.1234567890123456789012345678m));
            file.Save(new YamlScalarSettings { Value = node });
            Assert.DoesNotContain("\r", File.ReadAllText(fixture.FilePath));
            file.Reload();
            Assert.True(JsonNode.DeepEquals(node, file.Read<YamlScalarSettings>().Value));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void InvalidUtf8AndExcessiveOutputDepthDoNotReplaceTheSnapshotOrFile()
    {
        using var fixture = new TestFile(".yaml");
        var context = new TestSettingsContext(new System.Text.Json.JsonSerializerOptions { MaxDepth = 128 });
        using var file = new ConfigurationFile(context, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new DepthSettings());
        var before = File.ReadAllBytes(fixture.FilePath);
        File.WriteAllBytes(fixture.FilePath, [0xff, 0xfe]);
        Assert.Throws<InvalidDataException>(file.Reload);
        File.WriteAllBytes(fixture.FilePath, before);
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
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("Mail: null")]
    [InlineData("Mail: {port: 'sensitive-input'}")]
    [InlineData("Mail: {port: 1, PORT: 2}")]
    [InlineData("Mail: {port: 1, port: 2}")]
    [InlineData("Mail: [sensitive-input")]
    [InlineData("External: {.nan: sensitive-input}")]
    [InlineData("External: {1: sensitive-input}")]
    [InlineData("External: {true: sensitive-input}")]
    [InlineData("External: {[a, b]: sensitive-input}")]
    [InlineData("External: !!timestamp 2026-09-15")]
    [InlineData("External: !sensitive-input {}")]
    [InlineData("External: !!str {}")]
    [InlineData("External: !!bool sensitive-input")]
    [InlineData("External: .inf")]
    [InlineData("External: -.INF")]
    [InlineData("External: .NaN")]
    [InlineData("External: *sensitive-input")]
    [InlineData("External: &cycle [*cycle]")]
    [InlineData("External: {}\n---\nMail: {}")]
    [InlineData("%YAML 1.1\n---\nMail: {}")]
    public async Task InvalidInputKeepsTheFileAndOldSnapshotWithoutLeakingValues(string invalid)
    {
        using var fixture = new TestFile(".yaml");
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465 });
        fixture.Write(invalid);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => file.ReloadAsync(fixture.Token));
        Assert.DoesNotContain("sensitive-input", failure.ToString());
        Assert.Equal(465, file.Read<MailSettings>().Port);
        Assert.Equal(invalid, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void AliasExpansionAndActualDepthAreBoundedBeforeCopying()
    {
        using var fixture = new TestFile(".yaml");
        using var file = fixture.Create();
        file.Save(new MailSettings { Port = 465 });
        var lines = new List<string>
        {
            "a: &a [x, x, x, x, x, x, x, x, x, x]"
        };
        for (var index = 1; index < 7; index++)
        {
            var previous = (char)('a' + index - 1);
            var current = (char)('a' + index);
            lines.Add(current + ": &" + current + " [" + string.Join(", ", Enumerable.Repeat("*" + previous, 10)) + "]");
        }

        fixture.Write(string.Join("\n", lines));
        Assert.Throws<InvalidDataException>(file.Reload);
        fixture.Write("a: &a [x]\nb: " + new string ('[', 63) + "*a" + new string (']', 63));
        Assert.Throws<InvalidDataException>(file.Reload);
        fixture.Write("a: " + new string ('[', 64) + "x" + new string (']', 64));
        Assert.Throws<InvalidDataException>(file.Reload);
        Assert.Equal(465, file.Read<MailSettings>().Port);
    }

    private static string[] Ciphertexts(string path)
    {
        using var text = File.OpenText(path);
        var parser = new Parser(text);
        var values = new List<string>();
        while (parser.MoveNext())
        {
            if (parser.Current is Scalar { Value: var value } && value.StartsWith("enc:v1:", StringComparison.Ordinal))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }
}

[SettingsSection("Scalar")]
public sealed class YamlScalarSettings
{
    public JsonNode? Value { get; set; }
}

[JsonSerializable(typeof(YamlScalarSettings))]
public partial class YamlScalarContext : JsonSerializerContext
{
}

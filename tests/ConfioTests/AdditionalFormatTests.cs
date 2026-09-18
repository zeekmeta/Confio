using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace ConfioTests;

public sealed class AdditionalFormatTests
{
    private static ConfigurationFile Create(TestFile fixture, byte[]? key = null) => new(FormatSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key ?? fixture.EncryptionKey });
    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".TOML")]
    [InlineData(".INI")]
    public async Task SameModelsSupportSyncAsyncProtectionAndNativeViews(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        var value = file.Read<FormatSettings>();
        Assert.Equal(587, value.Port);
        Assert.False(File.Exists(fixture.FilePath));
        value.Password = "enc:v1:the-entire-text-is-the-password";
        value.Port = 465;
        await file.SaveAsync(value, fixture.Token);
        var saved = File.ReadAllText(fixture.FilePath);
        Assert.Contains("enc:v1:", saved);
        Assert.DoesNotContain("the-entire-text-is-the-password", saved);
        Assert.DoesNotContain("endpoint-secret", saved);
        Assert.DoesNotContain("whole-object-secret", saved);
        using var fresh = Create(fixture);
        var loaded = await fresh.ReadAsync<FormatSettings>(fixture.Token);
        Assert.Equal(value.Password, loaded.Password);
        Assert.Equal("00123", loaded.Code);
        Assert.Equal("true", loaded.BooleanText);
        Assert.True(loaded.UseTls);
        Assert.Equal(1.25m, loaded.Ratio);
        Assert.Equal(FormatMode.Second, loaded.Mode);
        Assert.Equal(FormatMode.Second, loaded.NamedMode);
        Assert.Equal(value.Date, loaded.Date);
        Assert.Equal(value.Time, loaded.Time);
        Assert.Equal(value.Local, loaded.Local);
        Assert.Equal(value.Local.Kind, loaded.Local.Kind);
        Assert.Equal(value.Timestamp, loaded.Timestamp);
        Assert.Equal(value.Timestamp.Offset, loaded.Timestamp.Offset);
        Assert.Equal(value.Names, loaded.Names);
        Assert.Equal("endpoint-secret", loaded.Endpoints[0].Token);
        Assert.Equal("endpoint-secret", loaded.Servers["a/b~c"].Token);
        Assert.Null(loaded.Credentials!.Optional);
        Assert.Empty(loaded.Credentials.Empty);
        Assert.Equal(value.Credentials!.Exact, loaded.Credentials.Exact);
        loaded.Port = 2525;
        Assert.Equal(465, fresh.Read<FormatSettings>().Port);
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, file);
        using var provider = nativeServices.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<FormatSettings>>();
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        await fresh.UpdateAsync<FormatSettings>(mail => mail.Port = 4025, fixture.Token);
        file.Update<FormatSettings>(mail => mail.Port++);
        Assert.Equal(4026, file.Read<FormatSettings>().Port);
        Assert.Equal("4026", configuration["Mail:Port"]);
        Assert.Equal(4026, monitor.CurrentValue.Port);
        Assert.Equal(1, notifications);
        fresh.Reload();
        Assert.Equal(4026, fresh.Read<FormatSettings>().Port);
        await fresh.ReloadAsync(fixture.Token);
        file.Save(new FormatCounter { Value = 7 });
        await file.UpdateAsync<FormatCounter>(counter => counter with { Value = counter.Value + 1 }, fixture.Token);
        file.Reset<FormatSettings>();
        Assert.Equal(8, file.Read<FormatCounter>().Value);
        Assert.Equal(587, file.Read<FormatSettings>().Port);
        await file.ResetAsync<FormatCounter>(fixture.Token);
        Assert.Equal(3, file.Read<FormatCounter>().Value);
        fresh.Reload();
        Assert.Equal(587, fresh.Read<FormatSettings>().Port);
    }

    [Theory]
    [InlineData(".json", false)]
    [InlineData(".json", true)]
    [InlineData(".yaml", false)]
    [InlineData(".yaml", true)]
    [InlineData(".toml", false)]
    [InlineData(".toml", true)]
    [InlineData(".ini", false)]
    [InlineData(".ini", true)]
    public async Task ReloadAfterExternalDeletionPublishesDefaultsWithoutRecreatingFile(string extension, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        file.Save(new FormatSettings { Port = 465, Password = "deleted-file-secret" });
        file.Save(new FormatCounter { Value = 7 });
        var original = file.Read<FormatSettings>();
        var notifications = 0;
        FormatSettings? notified = null;
        using var subscription = file.OnChange<FormatSettings>(mail =>
        {
            notifications++;
            notified = mail;
        });
        File.Delete(fixture.FilePath);
        var cached = asynchronous ? await file.ReadAsync<FormatSettings>(fixture.Token) : file.Read<FormatSettings>();
        Assert.Equal(465, cached.Port);
        Assert.Equal("deleted-file-secret", cached.Password);
        Assert.Equal(7, file.Read<FormatCounter>().Value);
        Assert.Equal(0, notifications);
        Assert.False(File.Exists(fixture.FilePath));
        if (asynchronous)
            await file.ReloadAsync(fixture.Token);
        else
            file.Reload();
        var defaults = asynchronous ? await file.ReadAsync<FormatSettings>(fixture.Token) : file.Read<FormatSettings>();
        Assert.Equal(587, defaults.Port);
        Assert.Equal("", defaults.Password);
        Assert.Equal(3, file.Read<FormatCounter>().Value);
        Assert.Equal(1, notifications);
        Assert.NotNull(notified);
        Assert.Equal(587, notified.Port);
        Assert.Equal(465, original.Port);
        Assert.Equal("deleted-file-secret", original.Password);
        Assert.False(File.Exists(fixture.FilePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(".json", false)]
    [InlineData(".json", true)]
    [InlineData(".yaml", false)]
    [InlineData(".yaml", true)]
    [InlineData(".toml", false)]
    [InlineData(".toml", true)]
    [InlineData(".ini", false)]
    [InlineData(".ini", true)]
    public async Task SaveOfOldDraftReplacesTheSectionWhileUpdatePreservesExternalChanges(string extension, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        using var external = Create(fixture);
        file.Save(new FormatSettings { Port = 465, Code = "initial", Password = "initial-secret" });
        file.Save(new FormatCounter { Value = 7 });
        var draft = file.Read<FormatSettings>();
        draft.Port = 2525;
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        external.Update<FormatSettings>(mail =>
        {
            mail.Code = "external-before-save";
            mail.Password = "external-save-secret";
        });
        external.Save(new FormatCounter { Value = 11 });
        Assert.Equal("initial", file.Read<FormatSettings>().Code);
        Assert.Equal(0, notifications);
        if (asynchronous)
            await file.SaveAsync(draft, fixture.Token);
        else
            file.Save(draft);
        Assert.Equal(1, notifications);
        Assert.Equal("initial", file.Read<FormatSettings>().Code);
        Assert.Equal(11, file.Read<FormatCounter>().Value);
        using (var persisted = Create(fixture))
        {
            var saved = persisted.Read<FormatSettings>();
            Assert.Equal(2525, saved.Port);
            Assert.Equal("initial", saved.Code);
            Assert.Equal("initial-secret", saved.Password);
            Assert.Equal(11, persisted.Read<FormatCounter>().Value);
        }

        external.Update<FormatSettings>(mail =>
        {
            mail.Code = "external-before-update";
            mail.Password = "external-update-secret";
        });
        external.Save(new FormatCounter { Value = 13 });
        draft.Port = 4025;
        var calls = 0;
        void ChangePort(FormatSettings mail)
        {
            calls++;
            mail.Port = draft.Port;
        }

        if (asynchronous)
            await file.UpdateAsync<FormatSettings>(ChangePort, fixture.Token);
        else
            file.Update<FormatSettings>(ChangePort);
        Assert.Equal(1, calls);
        Assert.Equal(2, notifications);
        Assert.Equal("external-before-update", file.Read<FormatSettings>().Code);
        Assert.Equal("external-update-secret", file.Read<FormatSettings>().Password);
        Assert.Equal(4025, file.Read<FormatSettings>().Port);
        Assert.Equal(13, file.Read<FormatCounter>().Value);
        Assert.Equal("initial", draft.Code);
        Assert.Equal("initial-secret", draft.Password);
        using var reopened = Create(fixture);
        var updated = reopened.Read<FormatSettings>();
        Assert.Equal(4025, updated.Port);
        Assert.Equal("external-before-update", updated.Code);
        Assert.Equal("external-update-secret", updated.Password);
        Assert.Equal(13, reopened.Read<FormatCounter>().Value);
    }

    [Theory]
    [InlineData(".toml")]
    [InlineData(".ini")]
    public async Task PlaintextLoadingOnlyRewritesProtectionLocations(string extension)
    {
        using var fixture = new TestFile(extension);
        fixture.Write(extension == ".toml" ? """
            [Mail]
            Port = 465
            Password = "handwritten-secret"
            Custom = "kept"
            [[Mail.Endpoints]]
            "api/key~1" = "array-secret"
            [Mail.Servers."a/b~c"]
            "api/key~1" = "dictionary-secret"
            [Mail.Credentials]
            Token = "object-secret"
            [External]
            Date = 2026-09-16
            Text = "2026-09-16"
            Timestamp = 2026-09-16T12:34:56.1234567+08:00
            Time = 12:34:56.1234567
            Large = 9223372036854775807
            Float = 1.2345678901234567
            """ : """
            ; hand-written strings are interpreted using the model
            [Mail]
            Port=0465
            Code=00123
            BooleanText=true
            UseTls=TRUE
            Password=handwritten-secret
            Custom=kept
            Endpoints:0:api/key~1=array-secret
            Servers:a/b~c:api/key~1=dictionary-secret
            Credentials:Token=object-secret
            [External]
            Code=00123
            BooleanText=true
            """);
        using var file = Create(fixture);
        var loaded = await file.ReadAsync<FormatSettings>(fixture.Token);
        Assert.Equal("handwritten-secret", loaded.Password);
        Assert.Equal("object-secret", loaded.Credentials!.Token);
        Assert.Equal("array-secret", loaded.Endpoints[0].Token);
        Assert.Equal("dictionary-secret", loaded.Servers["a/b~c"].Token);
        Assert.Equal(465, loaded.Port);
        Assert.Equal("00123", loaded.Code);
        var protectedText = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("handwritten-secret", protectedText);
        Assert.DoesNotContain("object-secret", protectedText);
        Assert.DoesNotContain("Ratio", protectedText);
        Assert.Contains("Custom", protectedText);
        await file.ReloadAsync(fixture.Token);
        Assert.Equal(protectedText, File.ReadAllText(fixture.FilePath));
        var cipher = ReadCipher(fixture.FilePath);
        file.Save(new FormatCounter { Value = 5 });
        Assert.Equal(cipher, ReadCipher(fixture.FilePath));
        if (extension == ".toml")
        {
            var table = ReadToml(fixture.FilePath);
            var external = (TomlTable)table["External"];
            Assert.IsType<TomlDateTime>(external["Date"]);
            Assert.IsType<TomlDateTime>(external["Timestamp"]);
            Assert.IsType<TomlDateTime>(external["Time"]);
            Assert.Equal("2026-09-16", Assert.IsType<string>(external["Text"]));
            Assert.Equal(long.MaxValue, external["Large"]);
            Assert.Equal(1.2345678901234567, external["Float"]);
            Assert.False(((TomlTable)table["Mail"]).ContainsKey("Code"));
        }

        using var next = Create(fixture);
        Assert.Equal("handwritten-secret", next.Read<FormatSettings>().Password);
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, next);
        using var provider = nativeServices.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfiguration>();
        Assert.Equal(extension == ".toml" ? "2026-09-16" : "00123", configuration[extension == ".toml" ? "External:Text" : "External:Code"]);
    }

    [Theory]
    [InlineData(".toml", "[Mail]\nPort = \"invalid-port-value\"")]
    [InlineData(".ini", "[Mail]\nPort=invalid-port-value")]
    public async Task SaveAndResetCanReplaceAnInvalidTargetSection(string extension, string invalid)
    {
        using var fixture = new TestFile(extension);
        fixture.Write(invalid + "\n[Counter.Primary]\nValue=7\n".Replace("Counter.Primary", extension == ".ini" ? "Counter:Primary" : "Counter.Primary"));
        using var file = Create(fixture);
        await file.SaveAsync(new FormatSettings { Port = 465 }, fixture.Token);
        Assert.Equal(465, file.Read<FormatSettings>().Port);
        Assert.Equal(7, file.Read<FormatCounter>().Value);
        fixture.Write(invalid);
        await file.ResetAsync<FormatSettings>(fixture.Token);
        Assert.Equal(587, file.Read<FormatSettings>().Port);
    }

    [Theory]
    [InlineData(".toml", "null", "/Mail/Password", ConfigurationValueError.Null)]
    [InlineData(".toml", "null-object", "/Mail/Credentials", ConfigurationValueError.Null)]
    [InlineData(".toml", "array-item", "/Mail/Names/1", ConfigurationValueError.Null)]
    [InlineData(".toml", "nested-property", "/Mail/Endpoints/0/api~1key~01", ConfigurationValueError.Null)]
    [InlineData(".toml", "dictionary-item", "/Mail/Servers/a~1b~0c", ConfigurationValueError.Null)]
    [InlineData(".toml", "dictionary-property", "/Mail/Servers/a~1b~0c/api~1key~01", ConfigurationValueError.Null)]
    [InlineData(".toml", "precision", "/Mail/Ratio", ConfigurationValueError.NumericPrecisionLoss)]
    [InlineData(".ini", "null", "/Mail/Password", ConfigurationValueError.Null)]
    [InlineData(".ini", "null-object", "/Mail/Credentials", ConfigurationValueError.Null)]
    [InlineData(".ini", "array-item", "/Mail/Names/1", ConfigurationValueError.Null)]
    [InlineData(".ini", "nested-property", "/Mail/Endpoints/0/api~1key~01", ConfigurationValueError.Null)]
    [InlineData(".ini", "dictionary-item", "/Mail/Servers/a~1b~0c", ConfigurationValueError.Null)]
    [InlineData(".ini", "dictionary-property", "/Mail/Servers/a~1b~0c/api~1key~01", ConfigurationValueError.Null)]
    [InlineData(".ini", "empty-array", "/Mail/Names", ConfigurationValueError.EmptyCollection)]
    [InlineData(".ini", "empty-list", "/Mail/Endpoints", ConfigurationValueError.EmptyCollection)]
    [InlineData(".ini", "empty-object", "/Mail/Servers", ConfigurationValueError.EmptyObject)]
    [InlineData(".ini", "multiline", "/Mail/Code", ConfigurationValueError.MultilineString)]
    [InlineData(".ini", "key", "/Mail/Servers/invalid:key", ConfigurationValueError.UnsupportedKey)]
    [InlineData(".ini", "multiline-key", "/Mail/Servers/line\nbreak", ConfigurationValueError.UnsupportedKey)]
    public async Task UnrepresentableWritesPreserveFileSnapshotAndNotifications(string extension, string problem, string path, ConfigurationValueError reason)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        file.Save(new FormatSettings { Port = 465 });
        var before = File.ReadAllBytes(fixture.FilePath);
        var snapshot = JsonSerializer.Serialize(file.Read<FormatSettings>(), FormatSettingsContext.Default.FormatSettings);
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        var value = file.Read<FormatSettings>();
        switch (problem)
        {
            case "null":
                value.Password = null;
                break;
            case "null-object":
                value.Credentials = null;
                break;
            case "array-item":
                value.Names[1] = null!;
                break;
            case "nested-property":
                value.Endpoints[0].Token = null;
                break;
            case "dictionary-item":
                value.Servers["a/b~c"] = null!;
                break;
            case "dictionary-property":
                value.Servers["a/b~c"].Token = null;
                break;
            case "precision":
                value.Ratio = 0.1234567890123456789012345678m;
                break;
            case "empty-array":
                value.Names = [];
                break;
            case "empty-list":
                value.Endpoints.Clear();
                break;
            case "empty-object":
                value.Servers.Clear();
                break;
            case "multiline":
                value.Code = "sensitive-first-line\nsecond-line";
                break;
            case "key":
                value.Servers["invalid:key"] = new();
                break;
            case "multiline-key":
                value.Servers["line\nbreak"] = new();
                break;
        }

        foreach (var asynchronous in new[]
        {
            false,
            true
        }

        )
        {
            var exception = asynchronous ? await Assert.ThrowsAsync<ConfigurationValueException>(() => file.SaveAsync(value, fixture.Token)) : Assert.Throws<ConfigurationValueException>(() => file.Save(value));
            Assert.Equal(extension.TrimStart('.').ToUpperInvariant(), exception.Format);
            Assert.Equal(path, exception.Path);
            Assert.Equal(reason, exception.Reason);
            Assert.Null(exception.InnerException);
            Assert.DoesNotContain("sensitive-first-line", exception.ToString());
            Assert.DoesNotContain("endpoint-secret", exception.ToString());
            Assert.DoesNotContain("0.1234567890123456789012345678", exception.ToString());
            Assert.DoesNotContain('\n', exception.Message);
            Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
            Assert.Equal(snapshot, JsonSerializer.Serialize(file.Read<FormatSettings>(), FormatSettingsContext.Default.FormatSettings));
            Assert.Equal(0, notifications);
        }

        await Assert.ThrowsAsync<ValidationException>(() => file.UpdateAsync<FormatSettings>(mail => mail.Port = -1, fixture.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.SaveAsync(new FormatSettings(), canceled.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        using var wrongKey = Create(fixture, new byte[32]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongKey.ReadAsync<FormatSettings>(fixture.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
    }

    [Theory]
    [InlineData(".toml", false)]
    [InlineData(".toml", true)]
    [InlineData(".ini", false)]
    [InlineData(".ini", true)]
    public async Task AnUnrepresentableFirstSavePreservesDefaultsAndCanBeCorrected(string extension, bool asynchronous)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        var notifications = 0;
        using var subscription = file.OnChange<FormatSettings>(_ => notifications++);
        var value = file.Read<FormatSettings>();
        value.Port = 465;
        value.Password = null;
        var exception = asynchronous ? await Assert.ThrowsAsync<ConfigurationValueException>(() => file.SaveAsync(value, fixture.Token)) : Assert.Throws<ConfigurationValueException>(() => file.Save(value));
        Assert.Equal("/Mail/Password", exception.Path);
        Assert.Equal(ConfigurationValueError.Null, exception.Reason);
        Assert.False(File.Exists(fixture.FilePath));
        Assert.Equal(587, file.Read<FormatSettings>().Port);
        Assert.Equal("", file.Read<FormatSettings>().Password);
        Assert.Equal(0, notifications);
        Assert.Null(value.Password);
        value.Password = "corrected-secret";
        if (asynchronous)
            await file.SaveAsync(value, fixture.Token);
        else
            file.Save(value);
        using var reopened = Create(fixture);
        Assert.Equal(465, reopened.Read<FormatSettings>().Port);
        Assert.Equal(value.Password, reopened.Read<FormatSettings>().Password);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(".ini", "[Mail]\nNames:1=gap")]
    [InlineData(".ini", "[Mail]\nNames:00=alias")]
    [InlineData(".ini", "[Mail]\nNames:0=first\nNames:2=gap")]
    [InlineData(".ini", "[Mail]\nPort=1\nPORT=secret-duplicate")]
    [InlineData(".ini", "[Mail]\nNames=parent\nNames:0=child")]
    [InlineData(".ini", "[Mail]\nNames:0=child\nNames=parent")]
    [InlineData(".toml", "[Mail]\nPort=465\nPort=2525")]
    [InlineData(".toml", "[External]\nValue=nan")]
    [InlineData(".toml", "[Mail]\nPassword=\"unterminated-secret")]
    [InlineData(".toml", "[Mail]\nPassword=\"secret\\x4G\"")]
    [InlineData(".toml", "[Mail]\nPassword=\"secret\\q\"")]
    [InlineData(".toml", "[External]\nValue={Name=\"secret\",Name=\"duplicate\"}")]
    [InlineData(".toml", "[External]\nTime=25:34")]
    public async Task InvalidFilesDoNotOverwriteTheLastSuccessfulSnapshot(string extension, string text)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        file.Save(new FormatSettings { Port = 465 });
        fixture.Write(text);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => file.ReloadAsync(fixture.Token));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Equal(text, File.ReadAllText(fixture.FilePath));
        Assert.Equal(465, file.Read<FormatSettings>().Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Toml11SyntaxSurvivesProtectionAndSavingAnotherSection(bool asynchronous)
    {
        using var fixture = new TestFile(".toml");
        fixture.Write("""
            [Mail]
            Password = "secret\e\x41"
            Time = 12:34
            Local = 2026-09-17T12:34
            Timestamp = 2026-09-17T12:34Z
            [External]
            Escapes = "\e\x41"
            Inline = {
                "dotted.key" = { Value = 3, },
                Name = "kept",
            }
            Time = 12:34
            Timestamp = 2026-09-17T12:34Z
            """);
        using var file = Create(fixture);
        var settings = asynchronous ? await file.ReadAsync<FormatSettings>(fixture.Token) : file.Read<FormatSettings>();
        Assert.Equal("secret\u001bA", settings.Password);
        Assert.DoesNotContain("secret", File.ReadAllText(fixture.FilePath));
        Assert.Equal(new TimeOnly(12, 34), settings.Time);
        Assert.Equal(new DateTime(2026, 9, 17, 12, 34, 0), settings.Local);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 12, 34, 0, TimeSpan.Zero), settings.Timestamp);
        if (asynchronous)
        {
            await file.SaveAsync(new FormatCounter { Value = 7 }, fixture.Token);
            await file.ReloadAsync(fixture.Token);
        }
        else
        {
            file.Save(new FormatCounter { Value = 7 });
            file.Reload();
        }

        Assert.Equal(settings.Password, file.Read<FormatSettings>().Password);
        Assert.Equal(settings.Time, file.Read<FormatSettings>().Time);
        Assert.Equal(settings.Timestamp, file.Read<FormatSettings>().Timestamp);
        Assert.Equal(7, file.Read<FormatCounter>().Value);
        var external = Assert.IsType<TomlTable>(ReadToml(fixture.FilePath)["External"]);
        Assert.Equal("\u001bA", external["Escapes"]);
        var inline = Assert.IsType<TomlTable>(external["Inline"]);
        Assert.Equal("kept", inline["Name"]);
        Assert.Equal(3L, Assert.IsType<TomlTable>(inline["dotted.key"])["Value"]);
        Assert.Equal(TomlDateTimeKind.LocalTime, Assert.IsType<TomlDateTime>(external["Time"]).Kind);
        Assert.Equal(TomlDateTimeKind.OffsetDateTimeByZ, Assert.IsType<TomlDateTime>(external["Timestamp"]).Kind);
    }

    [Theory]
    [InlineData("1.2345678901234567")]
    [InlineData("0.1")]
    [InlineData("1.0")]
    [InlineData("1e20")]
    [InlineData("1e-20")]
    [InlineData("2.2250738585072014e-308")]
    public void TomlPreservesFloatingPointValuesWhenSavingAnotherSection(string literal)
    {
        using var fixture = new TestFile(".toml");
        fixture.Write("[External]\nValue = " + literal);
        using var file = Create(fixture);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            file.Save(new FormatCounter { Value = 7 });
            file.Reload();
            var external = (TomlTable)ReadToml(fixture.FilePath)["External"];
            Assert.Equal(double.Parse(literal, CultureInfo.InvariantCulture), Assert.IsType<double>(external["Value"]));
            Assert.Equal(7, file.Read<FormatCounter>().Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void IniRoundTripsWhitespaceQuotesAndInvariantExactNumbers()
    {
        using var fixture = new TestFile(".ini");
        using var file = Create(fixture);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var text in new[]
            {
                "",
                " ",
                "\"quoted\"",
                "a=b;#c",
                "\\n",
                "中文 🔐",
                " padded \" ",
                "true",
                "00123"
            }

            )
            {
                file.Save(new FormatSettings { Code = text, Ratio = 0.1234567890123456789012345678m, SpecialNumber = double.NaN });
                file.Reload();
                Assert.Equal(text, file.Read<FormatSettings>().Code);
                Assert.Equal(0.1234567890123456789012345678m, file.Read<FormatSettings>().Ratio);
                Assert.True(double.IsNaN(file.Read<FormatSettings>().SpecialNumber));
                Assert.DoesNotContain('\r', File.ReadAllText(fixture.FilePath));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void TomlWritesDeclaredDatesAsNativeValuesAndRejectsOutOfRangeIntegers()
    {
        using var fixture = new TestFile(".toml");
        using var file = Create(fixture);
        file.Save(new FormatSettings());
        var mail = (TomlTable)ReadToml(fixture.FilePath)["Mail"];
        Assert.Equal(TomlDateTimeKind.LocalDate, ((TomlDateTime)mail["Date"]).Kind);
        Assert.Equal(TomlDateTimeKind.LocalTime, ((TomlDateTime)mail["Time"]).Kind);
        Assert.Equal(TomlDateTimeKind.LocalDateTime, ((TomlDateTime)mail["Local"]).Kind);
        Assert.Equal(TomlDateTimeKind.OffsetDateTimeByNumber, ((TomlDateTime)mail["Timestamp"]).Kind);
        using var raw = new ConfigurationFile(YamlScalarContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var before = File.ReadAllBytes(fixture.FilePath);
        var exception = Assert.Throws<ConfigurationValueException>(() => raw.Save(new YamlScalarSettings { Value = JsonNode.Parse("9223372036854775808") }));
        Assert.Equal(ConfigurationValueError.IntegerOutOfRange, exception.Reason);
        Assert.Equal("TOML", exception.Format);
        Assert.Equal("/Scalar/Value", exception.Path);
        Assert.DoesNotContain("9223372036854775808", exception.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
    }

    [Theory]
    [InlineData(".toml")]
    [InlineData(".ini")]
    public void CompleteProtectedConvertersUseTheSharedJsonPayload(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(WholeValueContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new WholeValueSettings { Value = new TokenValue { Secret = "custom-converted-secret" } });
        Assert.DoesNotContain("custom-converted-secret", File.ReadAllText(fixture.FilePath));
        file.Reload();
        Assert.Equal("custom-converted-secret", file.Read<WholeValueSettings>().Value.Secret);
    }

    [Fact]
    public void IniRejectsAnUndeterminedScalarShapeBeforeOpeningTheFile()
    {
        using var fixture = new TestFile(".ini");
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(YamlScalarContext.Default, fixture.FilePath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }

    private static TomlTable ReadToml(string path) => TomlSerializer.Deserialize(File.ReadAllText(path), TestTomlContext.Default.TomlTable)!;
    private static string ReadCipher(string path)
    {
        if (Path.GetExtension(path) == ".toml")
            return (string)((TomlTable)ReadToml(path)["Mail"])["Password"];
        using var stream = File.OpenRead(path);
        return Microsoft.Extensions.Configuration.Ini.IniStreamConfigurationProvider.Read(stream)["Mail:Password"]!;
    }
}

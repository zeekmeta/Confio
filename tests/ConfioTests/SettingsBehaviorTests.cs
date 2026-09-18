using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ConfioTests;

public sealed class SettingsBehaviorTests
{
    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task ImmutableDefaultsRespectMissingNullAndExplicitNestedValues(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        var defaults = file.Read<CheckedSettings>();
        Assert.Equal(3, defaults.Attempts);
        Assert.Equal(99, defaults.Detail!.Timeout);
        var validations = CheckedSettings.ValidationCalls;
        file.Read<CheckedSettings>();
        await file.ReadAsync<CheckedSettings>(fixture.Token);
        Assert.Equal(validations, CheckedSettings.ValidationCalls);
        // JSON 流式对象也是有效的 YAML 输入；分别经过两个正式文件适配器。
        fixture.Write("""{"Checked":{"Attempts":0,"Label":null,"Detail":{},"Items":[{}],"Lookup":{"primary":{}}},"Optional":{}}""");
        await file.ReloadAsync(fixture.Token);
        var partial = file.Read<CheckedSettings>();
        Assert.Equal(0, partial.Attempts);
        Assert.Equal(10, partial.Limit);
        Assert.Null(partial.Label);
        Assert.Equal(30, partial.Detail!.Timeout);
        Assert.Equal(30, Assert.Single(partial.Items).Timeout);
        Assert.Equal(30, partial.Lookup["primary"].Timeout);
        Assert.Equal(new OptionalSettings(), file.Read<OptionalSettings>());
        file.Save(partial with { Detail = null, Items = [], Lookup = [], Secret = "valid-secret" });
        using var reopened = Create(fixture);
        var saved = reopened.Read<CheckedSettings>();
        Assert.Null(saved.Detail);
        Assert.Empty(saved.Items);
        Assert.Empty(saved.Lookup);
        Assert.Equal("valid-secret", saved.Secret);
        Assert.DoesNotContain("valid-secret", File.ReadAllText(fixture.FilePath));
        await file.ResetAsync<CheckedSettings>(fixture.Token);
        Assert.Equal(99, file.Read<CheckedSettings>().Detail!.Timeout);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task BusinessValidationPrecedesCommitAcrossDirectDiAndNativeViews(string extension)
    {
        using var fixture = new TestFile(extension);
        using var protector = new ControlledProtector(fixture.EncryptionKey);
        using var file = Create(fixture, protector);
        file.Save(new CheckedSettings { Attempts = 2, Limit = 5, Secret = "valid-sensitive" });
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        await nativeServices.AddConfigurationFileAsync(configurationRoot, file, cancellationToken: fixture.Token);
        using var provider = nativeServices.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettings<CheckedSettings>>();
        var mutable = provider.GetRequiredService<ISettings<MutableCheckedSettings>>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CheckedSettings>>();
        Assert.Equal(2, monitor.CurrentValue.Attempts);
        var token = configuration.GetReloadToken();
        var before = File.ReadAllBytes(fixture.FilePath);
        var protectedCalls = protector.ProtectCalls;
        var notifications = 0;
        using var subscription = settings.OnChange(_ => notifications++);
        Assert.Throws<ValidationException>(() => file.Save(new CheckedSettings { Attempts = 6, Limit = 5 }));
        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(new CheckedSettings { Attempts = -1 }, fixture.Token));
        var callbacks = 0;
        Assert.Throws<ValidationException>(() => file.Update<CheckedSettings>(value =>
        {
            callbacks++;
            return value with
            {
                Attempts = 6
            };
        }));
        await Assert.ThrowsAsync<ValidationException>(() => settings.UpdateAsync(value =>
        {
            callbacks++;
            return value with
            {
                Secret = "invalid-sensitive"
            };
        }, fixture.Token));
        Assert.Throws<ValidationException>(() => file.Update<MutableCheckedSettings>(value => value.Value = -1));
        await Assert.ThrowsAsync<ValidationException>(() => mutable.UpdateAsync(value => value.Value = -1, fixture.Token));
        Assert.Equal(2, callbacks);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(protectedCalls, protector.ProtectCalls);
        Assert.Equal(2, settings.Read().Attempts);
        Assert.Equal(2, monitor.CurrentValue.Attempts);
        Assert.Equal("2", configuration["Checked:Attempts"]);
        Assert.Same(token, configuration.GetReloadToken());
        Assert.False(token.HasChanged);
        Assert.Equal(0, notifications);
        settings.Update(value => value with { Attempts = 4 });
        Assert.Equal(4, monitor.CurrentValue.Attempts);
        Assert.Equal(1, notifications);
        Assert.True(token.HasChanged);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task InvalidExternalModelsKeepTheSnapshotAndCanBeExplicitlyReplaced(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        file.Save(new CheckedSettings { Attempts = 2 });
        var notifications = 0;
        using var subscription = file.OnChange<CheckedSettings>(_ => notifications++);
        const string invalid = """{"Checked":{"Attempts":-1},"External":42}""";
        fixture.Write(invalid);
        Assert.Throws<ValidationException>(file.Reload);
        await Assert.ThrowsAsync<ValidationException>(() => file.ReloadAsync(fixture.Token));
        await Assert.ThrowsAsync<ValidationException>(() => file.SaveAsync(new MutableCheckedSettings { Value = 2 }, fixture.Token));
        Assert.Equal(2, file.Read<CheckedSettings>().Attempts);
        Assert.Equal(invalid, File.ReadAllText(fixture.FilePath));
        Assert.Equal(0, notifications);
        using var fresh = Create(fixture);
        Assert.Throws<ValidationException>(() => fresh.Read<CheckedSettings>());
        await Assert.ThrowsAsync<ValidationException>(() => fresh.ReadAsync<CheckedSettings>(fixture.Token));
        await file.SaveAsync(new CheckedSettings { Attempts = 4 }, fixture.Token);
        Assert.Equal(4, fresh.Read<CheckedSettings>().Attempts);
        Assert.Equal(1, notifications);
        Assert.Contains("42", File.ReadAllText(fixture.FilePath));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task InvalidDefaultsRejectFirstLoadAndResetWithoutChangingFiles(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = new ConfigurationFile(InvalidDefaultsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Throws<ValidationException>(() => file.Read<InvalidDefaultsSettings>());
        await Assert.ThrowsAsync<ValidationException>(() => file.ResetAsync<InvalidDefaultsSettings>(fixture.Token));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        file.Save(new InvalidDefaultsSettings { Port = 465 });
        var before = File.ReadAllBytes(fixture.FilePath);
        var notifications = 0;
        using var subscription = file.OnChange<InvalidDefaultsSettings>(_ => notifications++);
        Assert.Throws<ValidationException>(file.Reset<InvalidDefaultsSettings>);
        await Assert.ThrowsAsync<ValidationException>(() => file.ResetAsync<InvalidDefaultsSettings>(fixture.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(465, file.Read<InvalidDefaultsSettings>().Port);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task FunctionalUpdatesUseLatestValuesAndExecuteOnceAcrossInstances(string extension)
    {
        using var fixture = new TestFile(extension);
        using var first = Create(fixture);
        using var second = Create(fixture);
        first.Save(new CheckedSettings { Attempts = 0, Limit = 1000, Secret = "valid-secret" });
        var oldCopy = first.Read<CheckedSettings>();
        second.Update<CheckedSettings>(value => value with { Attempts = 8, Label = "external" });
        first.Update<CheckedSettings>(value => value with { Attempts = value.Attempts + 1 });
        Assert.Equal(9, first.Read<CheckedSettings>().Attempts);
        Assert.Equal("external", first.Read<CheckedSettings>().Label);
        var callbacks = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? first : second).UpdateAsync<CheckedSettings>(value =>
        {
            Interlocked.Increment(ref callbacks);
            return value with
            {
                Attempts = value.Attempts + 1
            };
        }, fixture.Token)));
        first.Reload();
        Assert.Equal(20, callbacks);
        Assert.Equal(29, first.Read<CheckedSettings>().Attempts);
        Assert.Equal("valid-secret", first.Read<CheckedSettings>().Secret);
        Assert.Equal(0, oldCopy.Attempts);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task FunctionalFailureNullAndCancellationDoNotCommitOrRetry(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = Create(fixture);
        file.Save(new CheckedSettings { Secret = "valid-secret" });
        var before = File.ReadAllBytes(fixture.FilePath);
        var notifications = 0;
        using var subscription = file.OnChange<CheckedSettings>(_ => notifications++);
        var callbacks = 0;
        Func<CheckedSettings, CheckedSettings> fail = _ =>
        {
            callbacks++;
            throw new ArithmeticException("business callback failed");
        };
        Assert.Throws<ArithmeticException>(() => file.Update(fail));
        await Assert.ThrowsAsync<ArithmeticException>(() => file.UpdateAsync(fail, fixture.Token));
        Assert.Equal(2, callbacks);
        Assert.Throws<InvalidOperationException>(() => file.Update<CheckedSettings>(_ => null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => file.UpdateAsync<CheckedSettings>(_ => null!, fixture.Token));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.UpdateAsync<CheckedSettings>(value =>
        {
            callbacks++;
            cancellation.Cancel();
            return value with
            {
                Attempts = 4
            };
        }, cancellation.Token));
        Assert.Equal(3, callbacks);
        Assert.Equal(before, File.ReadAllBytes(fixture.FilePath));
        Assert.Equal(3, file.Read<CheckedSettings>().Attempts);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".yaml")]
    public async Task DirectSubscriptionsDoNotLoadAndProvideIndependentCurrentCopies(string extension)
    {
        using var fixture = new TestFile(extension);
        using var file = fixture.Create();
        var values = new List<int>();
        using var subscription = file.OnChange<MailSettings>(mail => values.Add(mail.Port));
        using var editing = file.OnChange<MailSettings>(mail => mail.Port = -1);
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        file.Read<MailSettings>();
        Assert.Empty(values);
        file.Save(new MailSettings { Port = 465 });
        Assert.Equal(465, file.Read<MailSettings>().Port);
        await file.SaveAsync(new CounterSettings { Value = 1 }, fixture.Token);
        file.Reset<MailSettings>();
        using (var external = fixture.Create())
        {
            await external.SaveAsync(new MailSettings { Port = 2525 }, fixture.Token);
        }

        Assert.Equal(new[] { 465, 465, 587 }, values);
        await file.ReloadAsync(fixture.Token);
        Assert.Equal(new[] { 465, 465, 587, 2525 }, values);
        subscription.Dispose();
        await file.ResetAsync<MailSettings>(fixture.Token);
        Assert.Equal(4, values.Count);
        Assert.Equal(587, file.Read<MailSettings>().Port);
    }

    [Fact]
    public async Task DirectNotificationsAllowReentryAndIsolateFailingSubscribers()
    {
        using var fixture = new TestFile();
        using var file = fixture.Create();
        var reentered = false;
        var observed = 0;
        using var working = file.OnChange<MailSettings>(mail =>
        {
            Assert.Equal(465, mail.Port);
            observed++;
            if (!reentered)
            {
                reentered = true;
                file.Update<CounterSettings>(value => value.Value++);
            }
        });
        using var failing = file.OnChange<MailSettings>(_ => throw new InvalidOperationException("sensitive-callback-detail"));
        await Task.Run(() => file.Save(new MailSettings { Port = 465 }), fixture.Token).WaitAsync(fixture.Token);
        Assert.True(reentered);
        Assert.True(observed > 0);
        var previous = observed;
        await file.SaveAsync(new MailSettings { Port = 465 }, fixture.Token);
        Assert.True(observed > previous);
        using var reopened = fixture.Create();
        Assert.Equal(465, reopened.Read<MailSettings>().Port);
        Assert.Equal(1, reopened.Read<CounterSettings>().Value);
    }

    private static ConfigurationFile Create(TestFile fixture, IConfigurationProtector? protector = null) => new(BehaviorSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = protector is null ? fixture.EncryptionKey : null, Protector = protector });
}

[SettingsSection("Checked")]
public sealed record CheckedSettings : IValidatableSettings
{
    public static int ValidationCalls;
    public int Attempts { get; init; } = 3;
    public int Limit { get; init; } = 10;
    public string? Label { get; init; } = "default";
    public DetailSettings? Detail { get; init; } = new()
    {
        Timeout = 99
    };
    public List<DetailSettings> Items { get; init; } = [new()
    {
        Timeout = 88
    }

    ];
    public Dictionary<string, DetailSettings> Lookup { get; init; } = [];

    [Protected]
    public string Secret { get; init; } = "";

    public void Validate()
    {
        Interlocked.Increment(ref ValidationCalls);
        if (Attempts < 0 || Attempts > Limit)
            throw new ValidationException("Attempts must be non-negative and must not exceed the limit.");
        if (Secret.Length > 0 && !Secret.StartsWith("valid-", StringComparison.Ordinal))
            throw new ValidationException("The secret must use the expected format.");
    }
}

public sealed record DetailSettings
{
    public int Timeout { get; init; } = 30;
}

[SettingsSection("Mutable")]
public sealed class MutableCheckedSettings : IValidatableSettings
{
    public int Value { get; set; } = 1;

    public void Validate()
    {
        if (Value < 0)
            throw new ValidationException("The value must be non-negative.");
    }
}

[SettingsSection("Optional")]
public sealed record OptionalSettings(int Count = 7)
{
    public int Delay { get; init; } = 11;
}

[JsonSerializable(typeof(CheckedSettings))]
[JsonSerializable(typeof(MutableCheckedSettings))]
[JsonSerializable(typeof(OptionalSettings))]
public partial class BehaviorSettingsContext : JsonSerializerContext
{
}

[SettingsSection("InvalidDefaults")]
public sealed record InvalidDefaultsSettings : IValidatableSettings
{
    public int Port { get; init; }

    public void Validate()
    {
        if (Port <= 0)
            throw new ValidationException("The port must be positive.");
    }
}

[JsonSerializable(typeof(InvalidDefaultsSettings))]
public partial class InvalidDefaultsContext : JsonSerializerContext
{
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace ConfioTests;

public sealed class NativeIntegrationTests
{
    [Fact]
    public void NativeProjectionUsesDefaultsDecryptionAndNativeKeySemantics()
    {
        using var fixture = new TestFile();
        fixture.Write("""
            {"External":{"Flag":true,"Large":9007199254740993,"Text":"00123","Empty":{},"Array":[],"Null":null}}
            """);
        using (var seed = fixture.Create())
        {
            seed.Save(new MailSettings { Password = "native-test-secret" });
        }

        using var protector = new ControlledProtector(fixture.EncryptionKey);
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { Protector = protector });
        Assert.Equal("native-test-secret", configurationRoot["mail:credential"]);
        using var provider = nativeServices.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfiguration>();
        Assert.Equal("587", configuration["Mail:Port"]);
        Assert.Equal("30", configuration["Mail:Endpoint:Timeout"]);
        Assert.Equal("True", configuration["external:flag"]);
        Assert.Equal("9007199254740993", configuration["external:large"]);
        Assert.Equal("00123", configuration["external:text"]);
        Assert.Equal("", configuration["external:array"]);
        Assert.Null(configuration["external:empty"]);
        Assert.Equal(new[] { "Array", "Empty", "Flag", "Large", "Null", "Text" }, configuration.GetSection("External").GetChildren().Select(section => section.Key));
        var calls = protector.UnprotectCalls;
        Assert.Equal("native-test-secret", provider.GetRequiredService<IOptions<MailSettings>>().Value.Password);
        Assert.Equal("native-test-secret", configuration["Mail:credential"]);
        Assert.Equal(calls, protector.UnprotectCalls);
        var original = File.ReadAllText(fixture.FilePath);
        Assert.Throws<NotSupportedException>(() => configuration["Mail:Port"] = "999");
        Assert.Equal(original, File.ReadAllText(fixture.FilePath));
        Assert.DoesNotContain("native-test-secret", original);
    }

    [Fact]
    public async Task NativeOptionsKeepTheirCacheNamesAndFileDataSource()
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Mail":{"port":465}}""");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:Port"] = "25" });
        var nativeServices = new ServiceCollection();
        await nativeServices.AddConfigurationFileAsync(configuration, TestSettingsContext.Default, fixture.FilePath, cancellationToken: fixture.Token, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Equal("465", configuration["Mail:Port"]);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:Port"] = "2525" });
        nativeServices.Configure<MailSettings>(mail => mail.Label = "configured");
        nativeServices.PostConfigure<MailSettings>(mail => mail.Label += ":post");
        nativeServices.Configure<MailSettings>("named", mail => mail.Label = "named");
        await using var provider = nativeServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var file = provider.GetRequiredService<ConfigurationFile>();
        var settings = provider.GetRequiredService<ISettings<MailSettings>>();
        var options = provider.GetRequiredService<IOptions<MailSettings>>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
        using var scope = provider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>();
        Assert.Equal(465, options.Value.Port);
        Assert.Equal(465, snapshot.Value.Port);
        Assert.Equal("configured:post", monitor.CurrentValue.Label);
        var named = monitor.Get("named");
        Assert.Equal(587, named.Port);
        Assert.Equal("named", named.Label);
        Assert.Equal("2525", configuration["Mail:Port"]);
        await settings.UpdateAsync(mail => mail.Port = 2025, fixture.Token);
        Assert.Equal(2025, monitor.CurrentValue.Port);
        Assert.Equal(465, options.Value.Port);
        Assert.Equal(465, snapshot.Value.Port);
        Assert.Same(named, monitor.Get("named"));
        using var nextScope = provider.CreateScope();
        Assert.Equal(2025, nextScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>().Value.Port);
        monitor.CurrentValue.Port = 999;
        Assert.Equal(2025, file.Read<MailSettings>().Port);
        Assert.Equal("default", file.Read<MailSettings>().Label);
        file.Reset<MailSettings>();
        Assert.Equal(587, monitor.CurrentValue.Port);
        Assert.Equal("2525", configuration["Mail:Port"]);
    }

    [Fact]
    public async Task RootAndFileReloadPublishExternalEditsAndRetainTheSnapshotOnFailure()
    {
        using var fixture = new TestFile();
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        using var provider = nativeServices.BuildServiceProvider();
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        var file = provider.GetRequiredService<ConfigurationFile>();
        var config = (IConfigurationRoot)provider.GetRequiredService<IConfiguration>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
        var before = monitor.CurrentValue;
        var notifications = 0;
        using var subscription = file.OnChange<MailSettings>(_ => notifications++);
        fixture.Write("""{"Mail":{"port":465}}""");
        config.Reload();
        Assert.Equal("465", config["Mail:Port"]);
        Assert.NotSame(before, monitor.CurrentValue);
        Assert.Equal(465, monitor.CurrentValue.Port);
        Assert.Equal(1, notifications);
        fixture.Write("""{"Mail":{"port":2525}}""");
        await file.ReloadAsync(fixture.Token);
        Assert.Equal("2525", config["Mail:Port"]);
        Assert.Equal(2525, monitor.CurrentValue.Port);
        Assert.Equal(2, notifications);
        var token = config.GetReloadToken();
        fixture.Write("not JSON");
        Assert.Throws<InvalidDataException>(config.Reload);
        Assert.False(token.HasChanged);
        Assert.Equal("2525", config["Mail:Port"]);
        Assert.Equal(2, notifications);
        Assert.Equal("not JSON", File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void RootsShareReloadsAndProtectExternalPlaintextWithoutOwningTheBorrowedFile()
    {
        using var fixture = new TestFile();
        using var file = new ConfigurationFile(TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = new byte[32] });
        file.Read<MailSettings>();
        fixture.Write("not JSON");
        var firstServices = new ServiceCollection();
        using var firstConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        firstServices.AddConfigurationFile(firstConfiguration, file);
        using var first = firstServices.BuildServiceProvider();
        var secondServices = new ServiceCollection();
        using var secondConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        secondServices.AddConfigurationFile(secondConfiguration, file);
        using (var second = secondServices.BuildServiceProvider())
        {
            Assert.Equal(587, second.GetRequiredService<IOptionsMonitor<MailSettings>>().CurrentValue.Port);
            fixture.Write("""{"Mail":{"port":465,"credential":"root-reload-secret"}}""");
            ((IConfigurationRoot)first.GetRequiredService<IConfiguration>()).Reload();
            Assert.Equal("465", second.GetRequiredService<IConfiguration>()["Mail:Port"]);
            Assert.Equal(465, second.GetRequiredService<IOptionsMonitor<MailSettings>>().CurrentValue.Port);
            Assert.Equal("root-reload-secret", file.Read<MailSettings>().Password);
            Assert.DoesNotContain("root-reload-secret", File.ReadAllText(fixture.FilePath));
        }

        file.Update<MailSettings>(mail => mail.Port = 2525);
        Assert.Equal(2525, first.GetRequiredService<IOptionsMonitor<MailSettings>>().CurrentValue.Port);
    }

    [Fact]
    public async Task NotificationAndValidationFailuresCannotUndoCommittedWrites()
    {
        using var fixture = new TestFile();
        using var events = new DiagnosticEvents();
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        nativeServices.AddOptions<MailSettings>().Validate(mail => mail.Port >= 0, "sensitive-validation-detail");
        using var provider = nativeServices.BuildServiceProvider();
        var file = provider.GetRequiredService<ConfigurationFile>();
        var config = provider.GetRequiredService<IConfiguration>();
        var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
        Assert.Equal(587, monitor.CurrentValue.Port);
        file.Save(new MailSettings { Port = -1 });
        Assert.Equal(-1, file.Read<MailSettings>().Port);
        Assert.Equal("-1", config["Mail:Port"]);
        Assert.Throws<OptionsValidationException>(() => monitor.CurrentValue);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        using var subscription = monitor.OnChange((_, _) =>
        {
            Assert.Equal(465, file.Read<MailSettings>().Port);
            Assert.Equal("465", config["Mail:Port"]);
            cancellation.Cancel();
            throw new InvalidOperationException("sensitive-callback-detail");
        });
        await file.SaveAsync(new MailSettings { Port = 465 }, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(465, monitor.CurrentValue.Port);
        using var reopened = fixture.Create();
        Assert.Equal(465, reopened.Read<MailSettings>().Port);
        Assert.Contains(events.Events, item => item.Id == 1);
        Assert.Contains(events.Events, item => item.Payload == nameof(OptionsValidationException));
        Assert.All(events.Events, item => Assert.DoesNotContain("sensitive-", item.Payload));
    }

    [Fact]
    public void NotificationsRunOutsideBothWriteLocksAndCanReadOrCommit()
    {
        using var fixture = new TestFile();
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        using var provider = nativeServices.BuildServiceProvider();
        var file = provider.GetRequiredService<ConfigurationFile>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var committedAgain = false;
        using var subscription = ChangeToken.OnChange(configuration.GetReloadToken, () =>
        {
            if (!committedAgain)
            {
                committedAgain = true;
                file.Update<CounterSettings>(counter => counter.Value++);
            }
        });
        file.Save(new MailSettings { Port = 465 });
        Assert.True(committedAgain);
        Assert.Equal(1, file.Read<CounterSettings>().Value);
        Assert.Equal("1", configuration["Counters:Primary:Value"]);
    }

    [Fact]
    public async Task ProjectionFailurePreservesFileSnapshotAndChangeToken()
    {
        using var fixture = new TestFile();
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        using var provider = nativeServices.BuildServiceProvider();
        var file = provider.GetRequiredService<ConfigurationFile>();
        var config = provider.GetRequiredService<IConfiguration>();
        var token = config.GetReloadToken();
        fixture.Write("""{"External":{"private:key":1,"private":{"key":2}}}""");
        var original = File.ReadAllText(fixture.FilePath);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => file.SaveAsync(new MailSettings { Port = 465 }, fixture.Token));
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(original, File.ReadAllText(fixture.FilePath));
        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal("587", config["Mail:Port"]);
        Assert.False(token.HasChanged);
        using var borrowed = fixture.Create();
        Assert.Equal(587, borrowed.Read<MailSettings>().Port);
        using var rejected = new ConfigurationManager();
        Assert.Throws<InvalidDataException>(() => new ServiceCollection().AddConfigurationFile(rejected, borrowed));
        Assert.Equal(587, borrowed.Read<MailSettings>().Port);
    }

    [Fact]
    public void BorrowedFileReusesLoadedDataAndSurvivesNativeRoots()
    {
        using var fixture = new TestFile();
        using var file = fixture.Create();
        file.Read<MailSettings>();
        fixture.Write("not JSON");
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        nativeServices.AddConfigurationFile(configurationRoot, file);
        using (var provider = nativeServices.BuildServiceProvider())
        {
            Assert.Equal(587, provider.GetRequiredService<IOptions<MailSettings>>().Value.Port);
        }

        Assert.Equal(587, file.Read<MailSettings>().Port);
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddConfigurationFile(file);
        using (var host = builder.Build())
        {
            Assert.Same(file, host.Services.GetRequiredService<ConfigurationFile>());
        }

        Assert.Equal(587, file.Read<MailSettings>().Port);
        Assert.Equal(587, file.Read<MailSettings>().Port);
    }

    [Fact]
    public void ExplicitFactoriesAreKeptAndDuplicateRootsAreRejected()
    {
        using var fixture = new TestFile();
        var services = new ServiceCollection();
        var explicitFactory = new FixedFactory();
        services.AddSingleton<IOptionsFactory<MailSettings>>(explicitFactory);
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        services.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        using var provider = services.BuildServiceProvider();
        Assert.Same(explicitFactory, provider.GetRequiredService<IOptionsFactory<MailSettings>>());
        Assert.Equal(1234, provider.GetRequiredService<IOptions<MailSettings>>().Value.Port);
        Assert.Equal(587, provider.GetRequiredService<ISettings<MailSettings>>().Read().Port);
        Assert.Throws<InvalidOperationException>(() => services.AddConfigurationFile(configurationRoot, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey }));
        var conflicting = new ServiceCollection();
        using var original = new ConfigurationManager();
        conflicting.AddSingleton<IConfiguration>(original);
        using var additional = new ConfigurationManager();
        conflicting.AddConfigurationFile(additional, TestSettingsContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Same(original, conflicting.Single(item => item.ServiceType == typeof(IConfiguration)).ImplementationInstance);
    }

    [Fact]
    public async Task AsyncPreloadWaitsForProtectionAndCancellationCleansConfigurationSources()
    {
        using var fixture = new TestFile();
        using (var seed = fixture.Create())
        {
            seed.Save(new MailSettings { Password = "preload-test-secret" });
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var protector = new ControlledProtector(fixture.EncryptionKey)
        {
            BeforeUnprotectAsync = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var source = new DisposalSource();
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(source);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var build = new ServiceCollection().AddConfigurationFileAsync(configuration, TestSettingsContext.Default, fixture.FilePath, cancellationToken: cancellation.Token, options: new ConfigurationFileOptions { Protector = protector });
        await entered.Task.WaitAsync(fixture.Token);
        Assert.False(build.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.Equal(0, source.Provider.DisposeCount);
        ((IDisposable)configuration).Dispose();
        Assert.Equal(1, source.Provider.DisposeCount);
        Assert.DoesNotContain("preload-test-secret", File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void ConfigurationOwnsItsFileEvenWithoutBusinessResolution()
    {
        using var fixture = new TestFile();
        var configuration = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddConfigurationFile(configuration, TestSettingsContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var owned = Assert.IsType<ConfigurationFile>(services.Single(item => item.ServiceType == typeof(ConfigurationFile)).ImplementationInstance);
        using (services.BuildServiceProvider()) { }
        Assert.Equal(587, owned.Read<MailSettings>().Port);
        ((IDisposable)configuration).Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.Read<MailSettings>());
    }

    [Fact]
    public async Task FailedPreloadLeavesBorrowedFileAndOtherSourcesUsable()
    {
        using var fixture = new TestFile();
        fixture.Write("not JSON");
        using var borrowed = fixture.Create();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["External"] = "kept" });
        var sources = configuration.Sources.Count;
        var services = new ServiceCollection();
        await Assert.ThrowsAsync<InvalidDataException>(() => services.AddConfigurationFileAsync(configuration, borrowed));
        Assert.Equal(sources, configuration.Sources.Count);
        Assert.Empty(services);
        Assert.Equal("kept", configuration["External"]);
        fixture.Write("{}");
        Assert.Equal(587, borrowed.Read<MailSettings>().Port);
    }

    [Fact]
    public async Task HostValidationUsesNativeStartTiming()
    {
        using var fixture = new TestFile();
        var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
        await hostBuilder.AddConfigurationFileAsync(TestSettingsContext.Default, fixture.FilePath, cancellationToken: fixture.Token, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        hostBuilder.Services.AddOptions<MailSettings>().Validate(_ => false, "validation failure").ValidateOnStart();
        using var host = hostBuilder.Build();
        Assert.Equal(587, host.Services.GetRequiredService<ConfigurationFile>().Read<MailSettings>().Port);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(fixture.Token));
    }

    private sealed class FixedFactory : IOptionsFactory<MailSettings>
    {
        public MailSettings Create(string name) => new()
        {
            Port = 1234
        };
    }

    private sealed class DisposalSource : IConfigurationSource
    {
        internal DisposalProvider Provider { get; } = new();

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }

    private sealed class DisposalProvider : ConfigurationProvider, IDisposable
    {
        internal int DisposeCount;
        public void Dispose() => DisposeCount++;
    }

    private sealed class DiagnosticEvents : EventListener
    {
        internal List<(int Id, string Payload)> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Confio-Configuration")
            {
                EnableEvents(eventSource, EventLevel.Error);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (Events)
            {
                Events.Add((eventData.EventId, string.Join(" ", eventData.Payload ?? Array.Empty<object?>().AsReadOnly())));
            }
        }
    }
}

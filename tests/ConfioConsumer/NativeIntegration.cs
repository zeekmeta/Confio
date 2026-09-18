using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using ConfioConsumerModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyNativeContainer(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetExtension(path) == ".json" ? """{"Mail":{"port":465,"credential":"native-consumer-secret"}}""" : "Mail:\n  port: 465\n  credential: native-consumer-secret\n");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:Port"] = "25" });
        var services = new ServiceCollection();
        ConfigurationFile owned;
        await services.AddConfigurationFileAsync(configuration, Contexts, path, cancellationToken: cancellationToken);
        Check(configuration["Mail:Port"] == "465" && configuration["Mail:credential"] == "native-consumer-secret", "The configuration callback must receive the preloaded and decrypted view.");
        Check(!File.ReadAllText(path).Contains("native-consumer-secret"), "Native preload must protect plaintext before application configuration.");
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:Port"] = "2525" });
        services.Configure<MailSettings>(mail => mail.Label = "configured");
        services.PostConfigure<MailSettings>(mail => mail.Label += ":post");
        services.Configure<MailSettings>("named", mail => mail.Label = "named");
        services.AddOptions<MailSettings>().Validate(mail => mail.Port >= 0, "Port must be non-negative.");
        await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            owned = provider.GetRequiredService<ConfigurationFile>();
            var settings = provider.GetRequiredService<ISettings<MailSettings>>();
            var options = provider.GetRequiredService<IOptions<MailSettings>>();
            var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
            using var firstScope = provider.CreateScope();
            var snapshot = firstScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>();
            Check(options.Value.Port == 465 && snapshot.Value.Port == 465 && monitor.CurrentValue.Port == 465, "Native options must start with file data rather than the composed root override.");
            Check(configuration["Mail:Port"] == "2525" && monitor.CurrentValue.Label == "configured:post", "Provider precedence and native option configuration must retain their own semantics.");
            var named = monitor.Get("named");
            Check(named.Port == 587 && named.Label == "named", "Named options must start with model defaults.");
            await settings.UpdateAsync(mail => mail.Port = 2025, cancellationToken);
            Check(monitor.CurrentValue.Port == 2025 && options.Value.Port == 465 && snapshot.Value.Port == 465, "Only the native monitor cache should refresh on a file commit.");
            using var nextScope = provider.CreateScope();
            Check(nextScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>().Value.Port == 2025, "A new scope must use the current file snapshot.");
            Check(ReferenceEquals(named, monitor.Get("named")), "File changes must not invalidate unrelated option names.");
            monitor.CurrentValue.Port = 999;
            Check(settings.Read().Port == 2025 && settings.Read().Label == "default", "Options mutations must not change file state.");
            await provider.GetRequiredService<ISettings<SharedSettings>>().SaveAsync(new SharedSettings { Count = 7 }, cancellationToken);
            Check(provider.GetRequiredService<IOptionsMonitor<SharedSettings>>().CurrentValue.Count == 7, "Cross-project generated declarations must register native options automatically.");
            Check(configuration["Shared:Count"] == "7", "The shared model must enter the same configuration view.");
            owned.Save(new MailSettings { Port = -1 });
            Check(owned.Read<MailSettings>().Port == -1, "Option validation must not roll back committed data.");
            Expect<OptionsValidationException>(() => _ = monitor.CurrentValue);
            using (monitor.OnChange((_, _) => throw new InvalidOperationException("consumer notification failure")))
            {
                await owned.SaveAsync(new MailSettings { Port = 465 }, cancellationToken);
            }

            Check(monitor.CurrentValue.Port == 465, "A valid commit must recover the monitor after validation failure.");
            Expect<NotSupportedException>(() => configuration["Mail:Port"] = "9999");
            using (var external = new ConfigurationFile(Contexts, path))
            {
                external.Update<MailSettings>(mail => mail.Port = 4025);
            }

            ((IConfigurationRoot)configuration).Reload();
            Check(monitor.CurrentValue.Port == 4025 && settings.Read().Port == 4025, "Root reload must use the shared file path and refresh native monitor values.");
            await owned.ReloadAsync(cancellationToken);
            Check(monitor.CurrentValue.Port == 4025, "File reload must refresh native monitor values.");
            owned.Reset<MailSettings>();
            Check(monitor.CurrentValue.Port == 587, "Reset must publish model defaults to the native views.");
            Expect<InvalidOperationException>(() => services.AddConfigurationFile(Contexts, path));
        }

        ((IDisposable)configuration).Dispose();
        Expect<ObjectDisposedException>(() => owned.Read<MailSettings>());
        using var borrowed = new ConfigurationFile(Contexts, path);
        borrowed.Read<MailSettings>();
        var borrowedServices = new ServiceCollection();
        using var borrowedConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        borrowedServices.AddConfigurationFile(borrowedConfiguration, borrowed);
        using (var root = borrowedServices.BuildServiceProvider())
        {
            Check(ReferenceEquals(root.GetRequiredService<ConfigurationFile>(), borrowed), "Native registration must borrow an existing file.");
        }

        Check(borrowed.Read<MailSettings>().Port == 587, "Native root disposal must preserve caller-owned files.");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        canceled.Cancel();
        try
        {
            var canceledServices = new ServiceCollection();
            using var canceledConfiguration = new Microsoft.Extensions.Configuration.ConfigurationManager();
            await canceledServices.AddConfigurationFileAsync(canceledConfiguration, borrowed, cancellationToken: canceled.Token);
            canceledServices.BuildServiceProvider();
            throw new InvalidOperationException("Canceled startup unexpectedly completed.");
        }
        catch (OperationCanceledException)when (canceled.IsCancellationRequested)
        {
            Check(borrowed.Read<MailSettings>().Port == 587, "Canceled startup must not dispose a borrowed file.");
        }
    }

    private static async Task VerifyHost(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetExtension(path) == ".json" ? """{"Logging":{"LogLevel":{"Default":"Debug"}},"Mail":{"port":465,"credential":"host-consumer-secret"}}""" : "Logging:\n  LogLevel:\n    Default: Debug\nMail:\n  port: 465\n  credential: host-consumer-secret\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "appsettings.json"), """{"Earlier":"kept","Mail":{"port":25}}""");
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = Path.GetDirectoryName(path), EnvironmentName = Environments.Production });
        var sources = builder.Configuration.Sources.ToArray();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        HostLogProbe? probe = null;
        ConfigurationFile owned;
        await builder.AddConfigurationFileAsync(Contexts, path, cancellationToken: cancellationToken);
        Check(builder.Configuration["Mail:Port"] == "465", "Host configuration must be ready before the application callback.");
        Check(!File.ReadAllText(path).Contains("host-consumer-secret"), "Host preload must protect handwritten plaintext before constructing services.");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(provider => probe = new HostLogProbe(provider.GetRequiredService<ConfigurationFile>(), provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IOptions<MailSettings>>()));
        builder.Services.AddHostedService<ConfiguredWorker>();
        builder.Services.AddOptions<MailSettings>().Validate(mail => mail.Port > 0, "Port must be positive.").ValidateOnStart();
        using (var host = builder.Build())
        {
            owned = host.Services.GetRequiredService<ConfigurationFile>();
            Check(probe is not null, "Final logging construction must run after configuration preload.");
            Check(host.Services.GetRequiredService<IConfiguration>()["Earlier"] == "kept" && sources.All(source => builder.Configuration.Sources.Contains(source)), "Existing Host providers must remain in their native order.");
            Check(host.Services.GetRequiredService<ILogger<ConfiguredWorker>>().IsEnabled(LogLevel.Debug), "Native logging options must read the preloaded configuration source.");
            await host.StartAsync(cancellationToken);
            Check(owned.Read<MailSettings>().Port == 2025 && host.Services.GetRequiredService<IOptionsMonitor<MailSettings>>().CurrentValue.Port == 2025, "Hosted services must use the same file and native option monitor.");
            await host.StopAsync(cancellationToken);
        }

        Check(probe!.Disposed, "The Host must dispose its native services.");
        Expect<ObjectDisposedException>(() => owned.Read<MailSettings>());
        using var borrowed = new ConfigurationFile(Contexts, path);
        var borrowedBuilder = Host.CreateEmptyApplicationBuilder(null);
        borrowedBuilder.AddConfigurationFile(borrowed);
        using (var host = borrowedBuilder.Build())
        {
            Check(host.Services.GetRequiredService<IOptions<MailSettings>>().Value.Port == 2025, "An empty Host must support native integration without ambient configuration files.");
        }

        Check(borrowed.Read<MailSettings>().Port == 2025, "The Host must not dispose borrowed files.");

    }

    private sealed class HostLogProbe : ILoggerProvider
    {
        private readonly ConfigurationFile _file;
        internal bool Disposed;
        public HostLogProbe(ConfigurationFile file, IConfiguration configuration, IOptions<MailSettings> options)
        {
            _file = file;
            Check(configuration["Mail:Port"] == "465" && options.Value.Port == 465 && file.Read<MailSettings>().Password == "host-consumer-secret", "Logger construction must see the successful decrypted snapshot.");
        }

        public ILogger CreateLogger(string categoryName) => new EnabledLogger();
        public void Dispose()
        {
            Check(_file.Read<MailSettings>().Port == 2025, "The file must outlive services created during Host construction.");
            Disposed = true;
        }
    }

    private sealed class EnabledLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class ConfiguredWorker(ISettings<MailSettings> settings, IOptions<MailSettings> options) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Check(options.Value.Port == 465, "Options must be available when hosted services start.");
            return settings.UpdateAsync(mail => mail.Port = 2025, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

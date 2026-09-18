using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using ConfioConsumerModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifySettingsBehavior(string path, CancellationToken cancellationToken)
    {
        using var file = new ConfigurationFile(Contexts, path);
        var directNotifications = 0;
        using var directSubscription = file.OnChange<SharedSettings>(_ => directNotifications++);
        Check(!Directory.Exists(Path.GetDirectoryName(path)), "Subscribing must not perform file I/O.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"Shared":{"Count":1}}""");
        var initial = await file.ReadAsync<SharedSettings>(cancellationToken);
        Check(initial.Count == 1 && initial.BatchSize == 8 && directNotifications == 0, "A cross-project immutable model must retain missing init defaults without an initial notification.");
        file.Save(initial with { Count = 2, Secret = "consumer-immutable-secret" });
        Check(directNotifications == 1 && !File.ReadAllText(path).Contains("consumer-immutable-secret"), "Immutable saving must protect fields and publish a direct notification.");
        var services = new ServiceCollection();
        services.AddConfigurationFile(file);
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettings<SharedSettings>>();
        var typedNotifications = 0;
        using var typedSubscription = settings.OnChange(_ => typedNotifications++);
        var callbacks = 0;
        settings.Update(value =>
        {
            callbacks++;
            return value with
            {
                Count = value.Count + 1
            };
        });
        await settings.UpdateAsync(value =>
        {
            callbacks++;
            return value with
            {
                Count = value.Count + 1
            };
        }, cancellationToken);
        Check(callbacks == 2 && settings.Read().Count == 4 && typedNotifications == 2 && directNotifications == 3, "Direct and typed services must share functional updates, validation and file notifications.");
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        await nativeServices.AddConfigurationFileAsync(configurationRoot, file, cancellationToken: cancellationToken);
        using var native = nativeServices.BuildServiceProvider();
        var configuration = native.GetRequiredService<IConfiguration>();
        var monitor = native.GetRequiredService<IOptionsMonitor<SharedSettings>>();
        Check(monitor.CurrentValue.Count == 4 && native.GetRequiredService<IOptionsFactory<SharedSettings>>().Create("other").BatchSize == 8, "Native Options must consume the immutable snapshot and its generated default factory.");
        var token = configuration.GetReloadToken();
        var before = File.ReadAllBytes(path);
        Expect<ValidationException>(() => settings.Save(new SharedSettings { Count = -1 }));
        await ExpectValidationAsync(file.SaveAsync(new SharedSettings { BatchSize = 0 }, cancellationToken));
        Expect<ValidationException>(() => file.Update<SharedSettings>(value => value with { Count = -1 }));
        await ExpectValidationAsync(settings.UpdateAsync(value => value with { BatchSize = 0 }, cancellationToken));
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)) && monitor.CurrentValue.Count == 4 && ReferenceEquals(token, configuration.GetReloadToken()) && !token.HasChanged && directNotifications == 3 && typedNotifications == 2, "Model validation failures must preserve the file, snapshot, native view and change token.");
        using (var external = new ConfigurationFile(Contexts, path))
        {
            external.Update<SharedSettings>(value => value with { Count = 9 });
        }

        Check(settings.Read().Count == 4 && directNotifications == 3, "External writes require an explicit reload.");
        await file.ReloadAsync(cancellationToken);
        Check(settings.Read().Count == 9 && settings.Read().Secret == "consumer-immutable-secret" && monitor.CurrentValue.Count == 9 && directNotifications == 4, "Reload must validate decrypted immutable models before updating all views.");
        File.WriteAllText(path, """{"Shared":{"Count":-1}}""");
        await ExpectValidationAsync(file.ReloadAsync(cancellationToken));
        Check(settings.Read().Count == 9 && directNotifications == 4, "An invalid external edit must leave the last valid snapshot and subscriptions intact.");
        await settings.SaveAsync(initial with { Count = 5 }, cancellationToken);
        settings.Reset();
        await settings.ResetAsync(cancellationToken);
        Check(settings.Read().Count == 0 && settings.Read().BatchSize == 8, "Reset must validate and restore generated immutable defaults.");
        using (file.OnChange<SharedSettings>(_ => throw new InvalidOperationException("consumer callback failure")))
        {
            await file.SaveAsync(new SharedSettings { Count = 6 }, cancellationToken);
        }

        Check(monitor.CurrentValue.Count == 6 && configuration["Shared:Count"] == "6", "A failing direct subscriber must not undo a commit or block the native consumers.");
        directSubscription.Dispose();
        typedSubscription.Dispose();
        var directBefore = directNotifications;
        var typedBefore = typedNotifications;
        file.Reload();
        Check(directNotifications == directBefore && typedNotifications == typedBefore, "Disposing either subscription must unsubscribe.");
    }

    private static async Task ExpectValidationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (ValidationException)
        {
            return;
        }

        throw new InvalidOperationException("Expected a model validation failure.");
    }
}

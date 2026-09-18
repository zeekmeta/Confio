using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using ConfioConsumerModels;
using Microsoft.Extensions.DependencyInjection;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyOptions(string directory, CancellationToken cancellationToken)
    {
        foreach (var format in new[] { ConfigurationFormat.Json, ConfigurationFormat.Yaml, ConfigurationFormat.Toml, ConfigurationFormat.Ini })
        {
            var path = Path.Combine(directory, format.ToString(), "settings.conf");
            var options = new ConfigurationFileOptions
            {
                Format = format,
                KeyFilePath = Path.Combine(directory, "keys", "automatic.key")
            };
            using var file = new ConfigurationFile(RootOptionsContext.Default, path, options);
            Check(file.Read<RootOptionsSettings>().Port == 587 && !File.Exists(path), "A root model must read defaults without writing.");
            var model = new RootOptionsSettings
            {
                Port = 0,
                SecretNumber = 42,
                Entries = new Dictionary<string, RootOptionsEntry> { ["A"] = new RootOptionsEntry { Secret = "upper-secret" } }
            };
            if (format != ConfigurationFormat.Ini) model.Entries.Add("a", new RootOptionsEntry { Secret = "lower-secret" });
            await file.SaveAsync(model, cancellationToken);
            Check(File.Exists(file.KeyFilePath) && !File.ReadAllText(path).Contains("upper-secret"),
                "Automatic protection must persist its key and protect nested values.");
            var key = File.ReadAllBytes(file.KeyFilePath!);
            var movedPath = Path.Combine(Path.GetDirectoryName(path)!, "moved.conf");
            File.Copy(path, movedPath);
            using var reopened = new ConfigurationFile(RootOptionsContext.Default, movedPath, options);
            var restored = reopened.Read<RootOptionsSettings>();
            Check(restored.Port == 0 && restored.SecretNumber == 42 && restored.Entries["A"].Secret == "upper-secret",
                "Explicit Never, protected value types and a moved file must round-trip through static metadata.");
            if (format != ConfigurationFormat.Ini)
                Check(restored.Entries["a"].Secret == "lower-secret", "Case-sensitive dictionary keys must remain distinct.");
            reopened.Save(restored);
            Check(key.SequenceEqual(File.ReadAllBytes(file.KeyFilePath!)), "Opening and saving in a new instance must reuse the same key.");
            await reopened.ResetAsync<RootOptionsSettings>(cancellationToken);
            Check(reopened.Read<RootOptionsSettings>().Port == 587, "Root reset must restore model defaults.");
        }

        var readOnlyPath = Path.Combine(directory, "plaintext.json");
        var readOnlyKey = Path.Combine(directory, "keys", "read-only.key");
        File.WriteAllText(readOnlyPath, "{\"secretnumber\":73}");
        var original = File.ReadAllBytes(readOnlyPath);
        using (var file = new ConfigurationFile(RootOptionsContext.Default, readOnlyPath,
            new ConfigurationFileOptions { KeyFilePath = readOnlyKey, ProtectPlaintextOnLoad = false }))
        {
            Check((await file.ReadAsync<RootOptionsSettings>(cancellationToken)).SecretNumber == 73, "Read-only loading must use the selected property matching.");
            file.Reload();
            Check(original.SequenceEqual(File.ReadAllBytes(readOnlyPath)) && !File.Exists(readOnlyKey),
                "Disabled automatic plaintext protection must preserve the file and avoid key creation.");
            file.Update<RootOptionsSettings>(value => value.Port = 465);
            Check(File.Exists(readOnlyKey) && File.ReadAllText(readOnlyPath).Contains("enc:v1:"),
                "Explicit updates must still protect the model.");
        }

        var services = new ServiceCollection();
        var suppliedKey = PortableKey;
        var explicitOptions = new ConfigurationFileOptions { EncryptionKey = suppliedKey };
        var rootPath = Path.Combine(directory, "root.json");
        services.AddConfigurationFile(RootOptionsContext.Default, rootPath, explicitOptions);
        services.AddConfigurationFile(SharedSettingsContext.Default, Path.Combine(directory, "shared.json"), explicitOptions);
        Array.Clear(suppliedKey, 0, suppliedKey.Length);
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ISettings<RootOptionsSettings>>().Save(new RootOptionsSettings { SecretNumber = 29 });
            await provider.GetRequiredService<ISettings<SharedSettings>>().SaveAsync(new SharedSettings { Count = 3 }, cancellationToken);
            Check(provider.GetServices<ConfigurationFile>().Count() == 2 && !File.ReadAllText(rootPath).Contains("count"),
                "Different models must resolve their own file without per-model application factories.");
        }
        using (var reopened = new ConfigurationFile(RootOptionsContext.Default, rootPath,
            new ConfigurationFileOptions { EncryptionKey = PortableKey }))
            Check(reopened.Read<RootOptionsSettings>().SecretNumber == 29, "DI registration must freeze its key before caller mutation.");

        var keyPath = Path.Combine(directory, "keys", "failure.key");
        var encrypted = Path.Combine(directory, "failure.json");
        await RunFileKey(new[] { "file-key", "write", encrypted, keyPath, "async" }, cancellationToken);
        await RunFileKey(new[] { "file-key", "read", encrypted, keyPath, "sync" }, cancellationToken);
        File.WriteAllBytes(keyPath, new byte[3]);
        await RunFileKey(new[] { "file-key", "corrupt", encrypted, keyPath, "async" }, cancellationToken);
        File.Delete(keyPath);
        await RunFileKey(new[] { "file-key", "missing", encrypted, keyPath, "sync" }, cancellationToken);
        await RunFileKey(new[] { "file-key", "cancel", Path.Combine(directory, "cancel.json"), keyPath, "async" }, cancellationToken);
    }
}

[SettingsSection]
public sealed class RootOptionsSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Port { get; set; } = 587;

    [Protected]
    public int SecretNumber { get; set; }

    public Dictionary<string, RootOptionsEntry> Entries { get; set; } = new();
}

public sealed class RootOptionsEntry
{
    [Protected]
    public string Secret { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RootOptionsSettings))]
public partial class RootOptionsContext : JsonSerializerContext;

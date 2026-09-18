using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using ConfioConsumerModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tomlyn;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyAdditionalFormats(string directory, string extension, CancellationToken cancellationToken)
    {
        Check(!TomlSerializer.IsReflectionEnabledByDefault, "TOML document conversion must work with reflection disabled.");
        ISettingsContext[] contexts = [..Contexts, AdditionalFormatValuesContext.Default];
        foreach (var asynchronous in new[]
        {
            false,
            true
        }

        )
            foreach (var key in new byte[]? []
            {
                null,
                PortableKey
            }

            )
            {
                var path = Path.Combine(directory, (key is null ? "system-" : "aes-") + asynchronous, "settings." + extension);
                using var file = new ConfigurationFile(contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key });
                Check(file.Read<MailSettings>().Port == 587 && !File.Exists(path), "New formats must load defaults without creating files.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var input = extension == "toml" ? """
                    [Mail]
                    credential = "handwritten-format-secret"
                    [External]
                    Date = 2026-09-16
                    Text = "2026-09-16"
                    Float = 1.2345678901234567
                    Escapes = "\e\x41"
                    Time = 12:34
                    Timestamp = 2026-09-17T12:34Z
                    Nested = {
                        "dotted.key" = { enabled = true, },
                        x.y = 3,
                    }
                    [[External.Accounts]]
                    Name = "first"
                    [External.Accounts.Profile]
                    Count = 1
                    [[External.Accounts]]
                    Name = "second"
                    """ : "[Mail]\ncredential=handwritten-format-secret\n[External]\nCode=00123\nBooleanText=true\n";
                File.WriteAllText(path, input);
                await Reload();
                Check(file.Read<MailSettings>().Password == "handwritten-format-secret" && !File.ReadAllText(path).Contains("handwritten-format-secret"), "New formats must protect handwritten plaintext before publishing.");
                var protectedText = File.ReadAllText(path);
                Check(!protectedText.Split('\n').Any(line => string.Equals(line.Split('=')[0].Trim(), "port", StringComparison.OrdinalIgnoreCase)), "Automatic protection must not write missing defaults.");
                await Reload();
                Check(protectedText == File.ReadAllText(path), "Reload must preserve existing ciphertext.");
                var mail = new MailSettings
                {
                    Port = 465,
                    Label = "00123",
                    Password = "enc:v1:the-whole-password",
                    Account = new Credentials
                    {
                        Token = "whole-object-secret"
                    },
                    Servers = [new Credentials
                    {
                        Token = "array-secret"
                    }

                    ],
                    Accounts = new Dictionary<string, Credentials>
                    {
                        ["a/b~c"] = new()
                        {
                            Token = "dictionary-secret"
                        }
                    },
                    OptionalToken = new TokenValue
                    {
                        Value = "nullable-struct-secret"
                    }
                };
                if (asynchronous)
                    await file.SaveAsync(mail, cancellationToken);
                else
                    file.Save(mail);
                await file.SaveAsync(new AdditionalFormatValues(), cancellationToken);
                using (var reopened = new ConfigurationFile(contexts, path, options: new ConfigurationFileOptions { EncryptionKey = key }))
                {
                    var restored = await reopened.ReadAsync<MailSettings>(cancellationToken);
                    var values = reopened.Read<AdditionalFormatValues>();
                    Check(restored.Password == mail.Password && restored.Label == "00123" && restored.Account!.Token == "whole-object-secret" && restored.Servers[0].Token == "array-secret" && restored.Accounts["a/b~c"].Token == "dictionary-secret" && restored.OptionalToken!.Value.Value == "nullable-struct-secret", "Static model and protection metadata must support the complete new-format path.");
                    Check(values.Enabled && values.Code == "00123" && values.BooleanText == "true" && values.Ratio == 1.25m && values.ExactNumber == 1.2345678901234567 &&
#if !NETFRAMEWORK
                    values.Date == new DateOnly(2026, 9, 16) && values.Time == new TimeOnly(12, 34, 56) && values.WideInteger == -17 && values.WideUnsigned == 19 &&
#endif
                    values.Timestamp == new DateTimeOffset(2026, 9, 16, 12, 34, 56, TimeSpan.FromHours(8)) && values.NamedMode == AdditionalFormatMode.Second && double.IsNaN(values.SpecialNumber), "INI scalar restoration and TOML date handling must honor the declared model types.");
                    reopened.Update<MailSettings>(value => value.Port = 2525);
                }

                if (asynchronous)
                    await file.UpdateAsync<MailSettings>(value => value.Port++, cancellationToken);
                else
                    file.Update<MailSettings>(value => value.Port++);
                Check(file.Read<MailSettings>().Port == 2526, "Update must read the latest file before changing it.");
                using (var provider = new ServiceCollection().AddConfigurationFile(file).BuildServiceProvider())
                {
                    Check(provider.GetRequiredService<ISettings<MailSettings>>().Read().Port == 2526, "Ordinary DI must use the same file and declarations.");
                }

                var nativeServices = new ServiceCollection();
                using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
                await nativeServices.AddConfigurationFileAsync(configurationRoot, file, cancellationToken: cancellationToken);
                await using (var provider = nativeServices.BuildServiceProvider())
                {
                    var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
                    var configuration = provider.GetRequiredService<IConfiguration>();
                    var changes = 0;
                    using var subscription = file.OnChange<MailSettings>(_ => changes++);
                    file.Update<MailSettings>(value => value.Port = 4025);
                    Check(monitor.CurrentValue.Port == 4025 && configuration["Mail:port"] == "4025" && changes == 1, "Native views and notifications must follow the successful new-format commit.");
                    if (extension == "toml")
                    {
                        var persisted = File.ReadAllText(path);
                        Check(persisted.Contains("Date = 2026-09-16") && persisted.Contains("Text = \"2026-09-16\""), "Writing another TOML section must preserve native dates and strings independently.");
                        Check(double.Parse(configuration["External:Float"]!, CultureInfo.InvariantCulture) == 1.2345678901234567 && configuration["External:Nested:dotted.key:enabled"] == "True" && configuration["External:Nested:x:y"] == "3" && configuration["External:Accounts:0:Name"] == "first" && configuration["External:Accounts:0:Profile:Count"] == "1" && configuration["External:Accounts:1:Name"] == "second" && configuration["External:Escapes"] == "\u001bA" && TimeSpan.Parse(configuration["External:Time"]!, CultureInfo.InvariantCulture) == new TimeSpan(12, 34, 0) && DateTimeOffset.Parse(configuration["External:Timestamp"]!, CultureInfo.InvariantCulture) == new DateTimeOffset(2026, 9, 17, 12, 34, 0, TimeSpan.Zero), "TOML 1.1 must preserve new syntax values, floating point precision and native node types through protection and commits.");
                    }
                    else
                        Check(configuration["External:Code"] == "00123" && configuration["External:BooleanText"] == "true", "Unclaimed INI values must remain strings.");
                }

                var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
                await hostBuilder.AddConfigurationFileAsync(file, cancellationToken: cancellationToken);
                using (var host = hostBuilder.Build())
                {
                    Check(host.Services.GetRequiredService<IOptions<MailSettings>>().Value.Port == 4025, "Host must preload the same static model without additional application binding.");
                    await host.StartAsync(cancellationToken);
                    await host.StopAsync(cancellationToken);
                }

                var before = File.ReadAllBytes(path);
                mail.Label = null;
                try
                {
                    if (asynchronous)
                        await file.SaveAsync(mail, cancellationToken);
                    else
                        file.Save(mail);
                    throw new InvalidOperationException("Saving null must fail for TOML and INI.");
                }
                catch (ConfigurationValueException exception)
                {
                    Check(exception.Format == extension.ToUpperInvariant() && exception.Path == "/Mail/label" && exception.Reason == ConfigurationValueError.Null && exception.InnerException is null, "Format errors must provide a path using the final serialized names and a structured reason in JIT and Native AOT.");
                }

                Expect<ValidationException>(() => file.Save(new SharedSettings { Count = -1 }));
                Check(before.SequenceEqual(File.ReadAllBytes(path)) && file.Read<MailSettings>().Port == 4025, "Invalid or unrepresentable values must preserve both the file and successful snapshot.");
                if (extension == "toml")
                {
                    foreach (var invalid in new[]
                    {
                        "\"secret\\x4G\"",
                        "\"secret\\q\"",
                        "{ Name=\"secret\", Name=\"duplicate\" }",
                        "25:34"
                    }

                    )
                    {
                        var invalidText = "[External]\nValue = " + invalid;
                        File.WriteAllText(path, invalidText);
                        Expect<InvalidDataException>(file.Reload);
                        Check(File.ReadAllText(path) == invalidText && file.Read<MailSettings>().Port == 4025, "Invalid TOML must fail without rewriting the file or publishing a failed snapshot.");
                    }
                }

                File.WriteAllText(path, extension == "toml" ? "[Mail]\ncredential = \"enc:v1:xxx\"" : "[Mail]\ncredential=enc:v1:xxx");
                var damaged = File.ReadAllBytes(path);
                Expect<InvalidDataException>(file.Reload);
                Check(damaged.SequenceEqual(File.ReadAllBytes(path)) && file.Read<MailSettings>().Port == 4025, "A reserved but invalid ciphertext prefix must never fall back to plaintext.");
                mail.Label = "00123";
                file.Save(mail);
                await file.SaveAsync(new SharedSettings { Count = 5, Secret = "cross-project-secret" }, cancellationToken);
                file.Update<SharedSettings>(value => value with { Count = value.Count + 1 });
                Check(file.Read<SharedSettings>().Count == 6 && file.Read<SharedSettings>().BatchSize == 8, "Cross-project record defaults and updates must support the new formats.");
                if (asynchronous)
                    await file.ResetAsync<MailSettings>(cancellationToken);
                else
                    file.Reset<MailSettings>();
                Check(file.Read<MailSettings>().Port == 587 && file.Read<SharedSettings>().Count == 6, "Reset must remove only the target section and publish model defaults.");
                async Task Reload()
                {
                    if (asynchronous)
                        await file.ReloadAsync(cancellationToken);
                    else
                        file.Reload();
                }
            }

        await VerifyProcesses(Path.Combine(directory, "processes", "settings." + extension), cancellationToken);
    }
}

[SettingsSection("Types")]
public sealed class AdditionalFormatValues
{
    public bool Enabled { get; set; } = true;
    public string Code { get; set; } = "00123";
    public string BooleanText { get; set; } = "true";
    public decimal Ratio { get; set; } = 1.25m;
    public double ExactNumber { get; set; } = 1.2345678901234567;
#if !NETFRAMEWORK
    public DateOnly Date { get; set; } = new(2026, 9, 16);
    public TimeOnly Time { get; set; } = new(12, 34, 56);
    public Int128 WideInteger { get; set; } = -17;
    public UInt128 WideUnsigned { get; set; } = 19;
#endif
    public DateTimeOffset Timestamp { get; set; } = new(2026, 9, 16, 12, 34, 56, TimeSpan.FromHours(8));

    [JsonConverter(typeof(JsonStringEnumConverter<AdditionalFormatMode>))]
    public AdditionalFormatMode NamedMode { get; set; } = AdditionalFormatMode.Second;

    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double SpecialNumber { get; set; } = double.NaN;
}

public enum AdditionalFormatMode
{
    First,
    [JsonStringEnumMemberName("001")]
    Second
}

[JsonSerializable(typeof(AdditionalFormatValues))]
public partial class AdditionalFormatValuesContext : JsonSerializerContext;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyYaml(string path, CancellationToken cancellationToken)
    {
        ISettingsContext[] contexts = [..Contexts, FormatValuesContext.Default];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            %YAML 1.2
            ---
            Template: &mail {port: 0o721, label: null}
            Mail: *mail
            Values:
              Integer: 9007199254740993
              Unsigned: 0xffffffffffffffff
              Amount: 123456789.01234567890123456789
              Ratio: .125
              Date: 2026-09-15
              Time: 08:30:15
              Timestamp: 2026-09-15T08:30:15+08:00
              Id: 271c1b57-e3f4-4dd9-8655-013196892b22
              Mode: Active
              Texts: {'00123': '00123', 'true': 'true', 'null': null}
            External:
              Huge: 900719925474099312345678901234567890
              Precise: +000.12345678901234567890123456789012345
              Exponent: 01.e+2000
              Date: 2026-09-15
              Literal: yes
            """);
        using var file = new ConfigurationFile(contexts, path);
        var mail = await file.ReadAsync<MailSettings>(cancellationToken);
        Check(mail.Port == 465 && mail.Label is null && mail.Password == "", "YAML aliases, octal values, null and defaults must load together.");
        var values = file.Read<FormatValues>();
        CheckValues(values);
        mail.Password = "yaml-consumer-secret";
        mail.Account = new Credentials
        {
            Token = "yaml-consumer-account"
        };
        mail.Accounts["a/b~c"] = new Credentials
        {
            Token = "yaml-consumer-dictionary"
        };
        await file.SaveAsync(mail, cancellationToken);
        var ciphertext = ReadYamlCiphertexts(path);
        Check(ciphertext.Length == 3 && !File.ReadAllText(path).Contains("yaml-consumer-"), "YAML must contain real ciphertext for all protected boundaries.");
        file.Save(values);
        Check(ciphertext.SequenceEqual(ReadYamlCiphertexts(path)), "Saving another YAML section must retain the original ciphertext.");
        var nativeServices = new ServiceCollection();
        using var configurationRoot = new Microsoft.Extensions.Configuration.ConfigurationManager();
        await nativeServices.AddConfigurationFileAsync(configurationRoot, file, cancellationToken: cancellationToken);
        using var provider = nativeServices.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfiguration>();
        CheckValues(provider.GetRequiredService<IOptionsMonitor<FormatValues>>().CurrentValue);
        var calls = 0;
        await file.UpdateAsync<MailSettings>(current =>
        {
            calls++;
            current.Port = 2525;
        }, cancellationToken);
        Check(calls == 1 && configuration["Mail:port"] == "2525" && configuration["Template:port"] == "465", "Expanded YAML aliases must not couple updates to distinct sections.");
        file.Reload();
        CheckValues(file.Read<FormatValues>());
        Check(file.Read<MailSettings>().Password == "yaml-consumer-secret" && file.Read<MailSettings>().Account!.Token == "yaml-consumer-account" && file.Read<MailSettings>().Accounts["a/b~c"].Token == "yaml-consumer-dictionary", "YAML reload must decrypt strings, protected objects and nested dictionary members.");
        Check(configuration["External:Huge"] == "900719925474099312345678901234567890" && configuration["External:Precise"] == "0.12345678901234567890123456789012345" && configuration["External:Exponent"] == "1.0e+2000" && configuration["External:Date"] == "2026-09-15" && configuration["External:Literal"] == "yes", "Unowned YAML scalars must retain precision and Core schema semantics through disk writes.");
        var jsonPath = Path.ChangeExtension(path, ".json");
        using (var json = new ConfigurationFile(FormatValuesContext.Default, jsonPath))
        {
            await json.SaveAsync(values, cancellationToken);
            json.Reload();
            CheckValues(json.Read<FormatValues>());
        }

        await file.ResetAsync<MailSettings>(cancellationToken);
        file.Reset<FormatValues>();
        await file.ReloadAsync(cancellationToken);
        Check(configuration["Mail:port"] == "587" && configuration["External:Literal"] == "yes", "YAML reset must publish defaults and preserve unrelated data.");
    }

    private static void CheckValues(FormatValues values)
    {
        Check(values.Integer == 9007199254740993 && values.Unsigned == ulong.MaxValue && values.Amount == 123456789.01234567890123456789m && values.Ratio == 0.125, "Static metadata must preserve typed number precision across formats.");
        Check(values.Timestamp == new DateTimeOffset(2026, 9, 15, 8, 30, 15, TimeSpan.FromHours(8)) &&
#if !NETFRAMEWORK
        values.Date == new DateOnly(2026, 9, 15) && values.Time == new TimeOnly(8, 30, 15) &&
#endif
        values.Id == Guid.Parse("271c1b57-e3f4-4dd9-8655-013196892b22") && values.Mode == FormatMode.Active, "Dates, times, identifiers and enums must use the shared static model metadata.");
        Check(values.Texts["00123"] == "00123" && values.Texts["true"] == "true" && values.Texts["null"] is null, "String mapping keys and values must not be reinterpreted as YAML scalars.");
    }

    private static string[] ReadYamlCiphertexts(string path)
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

[SettingsSection("Values")]
public sealed class FormatValues
{
    public long Integer { get; set; }
    public ulong Unsigned { get; set; }
    public decimal Amount { get; set; }
    public double Ratio { get; set; }
#if !NETFRAMEWORK
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
#endif
    public DateTimeOffset Timestamp { get; set; }
    public Guid Id { get; set; }
    public FormatMode Mode { get; set; }
    public Dictionary<string, string?> Texts { get; set; } = new();
}

public enum FormatMode
{
    None,
    Active
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(FormatValues))]
public partial class FormatValuesContext : JsonSerializerContext
{
}

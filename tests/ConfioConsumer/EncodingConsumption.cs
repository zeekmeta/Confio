using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyEncodings(string directory, CancellationToken cancellationToken)
    {
        var gbk = CodePagesEncodingProvider.Instance.GetEncoding("gbk")!;
        var cases = new (string Extension, Encoding Encoding)[]
        {
            ("json", Encoding.Unicode),
            ("yaml", new UTF32Encoding(true, true, true)),
            ("toml", new UTF8Encoding(true, true)),
            ("ini", gbk)
        };
        foreach (var item in cases)
        {
            var path = Path.Combine(directory, "settings." + item.Extension);
            var options = new ConfigurationFileOptions { Encoding = item.Encoding, EncryptionKey = PortableKey };
            using var file = new ConfigurationFile(EncodingSettingsContext.Default, path, options);
            Check(file.Read<EncodingSettings>().Value == "default" && !File.Exists(path),
                "Encoded files must keep lazy loading and model defaults.");
            Directory.CreateDirectory(directory);
            var input = item.Extension switch
            {
                "json" => "{\"Text\":{\"Value\":\"手写中文\",\"Secret\":\"手写秘密😀\"}}",
                "yaml" => "Text:\n  Value: 手写中文\n  Secret: 手写秘密😀\n",
                "toml" => "[Text]\nValue = \"手写中文\"\nSecret = \"手写秘密😀\"\n",
                _ => "[Text]\nValue=手写中文\nSecret=手写秘密😀\n"
            };
            // INI 用 UTF-8 BOM 输入验证 BOM 优先，自动保护后按所选 GBK 写回。
            File.WriteAllText(path, input, item.Extension == "ini" ? Encoding.UTF8 : item.Encoding);
            await file.ReloadAsync(cancellationToken);
            var read = await file.ReadAsync<EncodingSettings>(cancellationToken);
            Check(read.Value == "手写中文" && read.Secret == "手写秘密😀", "BOM input and plaintext protection must preserve Unicode values.");
            var bytes = File.ReadAllBytes(path);
            Check(bytes.Take(item.Encoding.GetPreamble().Length).SequenceEqual(item.Encoding.GetPreamble()),
                "Automatic protection must use the selected output preamble.");
            var text = item.Encoding.GetString(bytes);
            Check(text.Contains("手写中文") && text.Contains("enc:v1:") && !text.Contains("手写秘密"),
                "The selected encoding must preserve visible text and protect secrets.");
            await file.SaveAsync(new EncodingSettings { Value = "保存中文 café", Secret = "新的秘密😀" }, cancellationToken);
            file.Update<EncodingSettings>(value => value.Value = "更新中文");
            using (var reopened = new ConfigurationFile(EncodingSettingsContext.Default, path, options))
            {
                Check(reopened.Read<EncodingSettings>().Value == "更新中文" &&
                    (await reopened.ReadAsync<EncodingSettings>(cancellationToken)).Secret == "新的秘密😀",
                    "Encoded sync/async commits must be readable in a new instance.");
            }
            file.Reload();
            await file.ReloadAsync(cancellationToken);

            if (item.Extension == "ini")
            {
                var original = File.ReadAllBytes(path);
                try
                {
                    file.Save(new EncodingSettings { Value = "😀" });
                    throw new InvalidOperationException("GBK must reject characters it cannot represent.");
                }
                catch (InvalidDataException)
                {
                    Check(original.SequenceEqual(File.ReadAllBytes(path)) && file.Read<EncodingSettings>().Value == "更新中文",
                        "Encoding failures must retain the file and snapshot.");
                }

                using var configuration = new ConfigurationManager();
                var services = new ServiceCollection();
                await services.AddConfigurationFileAsync(configuration, EncodingSettingsContext.Default, path,
                    options: options, cancellationToken: cancellationToken);
                using var provider = services.BuildServiceProvider();
                await provider.GetRequiredService<ISettings<EncodingSettings>>().UpdateAsync(value => value.Value = "容器中文", cancellationToken);
                Check(configuration["Text:Value"] == "容器中文" &&
                    provider.GetRequiredService<IOptionsMonitor<EncodingSettings>>().CurrentValue.Secret == "新的秘密😀",
                    "Native configuration and Options must share encoded reads and writes.");
            }
            await file.ResetAsync<EncodingSettings>(cancellationToken);
            file.Reset<EncodingSettings>();
            Check(file.Read<EncodingSettings>().Value == "default", "Reset must preserve defaults with the selected encoding.");
        }

        var defaultPath = Path.Combine(directory, "runtime-default.ini");
        var defaultOptions = new ConfigurationFileOptions { Encoding = Encoding.Default, EncryptionKey = PortableKey };
        using (var file = new ConfigurationFile(EncodingSettingsContext.Default, defaultPath, defaultOptions))
            file.Save(new EncodingSettings { Value = "runtime-default", Secret = "默认编码中的秘密😀" });
        using (var reopened = new ConfigurationFile(EncodingSettingsContext.Default, defaultPath, defaultOptions))
            Check(reopened.Read<EncodingSettings>().Secret == "默认编码中的秘密😀", "File encoding must not change the UTF-8 protection payload.");
        Console.WriteLine("PASS: file encodings, Unicode BOMs, GBK, automatic protection, failure retention and native views. Encoding.Default: " + Encoding.Default.WebName);
    }
}

[SettingsSection("Text")]
public sealed class EncodingSettings
{
    public string Value { get; set; } = "default";

    [Protected]
    public string Secret { get; set; } = "";
}

[JsonSerializable(typeof(EncodingSettings))]
public partial class EncodingSettingsContext : JsonSerializerContext;

using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Confio;

namespace ConfioConsumer;

internal static partial class Program
{
    private static async Task VerifyInheritedProtection(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"Inherited":{"Private":{"secret":"private-base-secret"},"Static":{"secret":"static-base-secret"}}}
            """);
        using var file = new ConfigurationFile(InheritedContext.Default, path, options: new ConfigurationFileOptions { EncryptionKey = PortableKey });
        var settings = await file.ReadAsync<InheritedSettings>(cancellationToken);
        Check(((InheritedSecret)settings.Private).Secret == "private-base-secret" &&
              ((InheritedSecret)settings.Static).Secret == "static-base-secret",
            "Non-serialized hiding members must not discard inherited values.");
        Check(!File.ReadAllText(path).Contains("base-secret"),
            "First loading must protect inherited members hidden by private or static members.");
        ((InheritedSecret)settings.Private).Secret = "saved-private-secret";
        ((InheritedSecret)settings.Static).Secret = "saved-static-secret";
        file.Save(settings);
        Check(!File.ReadAllText(path).Contains("saved-"), "Saving must protect the actual serialized base members.");
        using var reopened = new ConfigurationFile(InheritedContext.Default, path, options: new ConfigurationFileOptions { EncryptionKey = PortableKey });
        settings = reopened.Read<InheritedSettings>();
        Check(((InheritedSecret)settings.Private).Secret == "saved-private-secret" &&
              ((InheritedSecret)settings.Static).Secret == "saved-static-secret",
            "Inherited protected members must decrypt after reopening the file.");
    }
}

[SettingsSection("Inherited")]
public sealed class InheritedSettings
{
    public PrivatelyHiddenSecret Private { get; set; } = new();
    public StaticallyHiddenSecret Static { get; set; } = new();
}

public class InheritedSecret
{
    [Protected, JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
}

public sealed class PrivatelyHiddenSecret : InheritedSecret
{
    private new string Secret { get; set; } = "private-only";
}

public sealed class StaticallyHiddenSecret : InheritedSecret
{
    public new static string Secret { get; set; } = "static-only";
}

[JsonSerializable(typeof(InheritedSettings))]
public partial class InheritedContext : JsonSerializerContext;

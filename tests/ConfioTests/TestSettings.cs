using System.Collections.Generic;
using System.Text.Json.Serialization;
using Confio;

namespace ConfioTests;

[SettingsSection("Mail")]
public sealed class MailSettings
{
    public int Port { get; set; } = 587;
    public string? Label { get; set; } = "default";

    [Protected]
    [JsonPropertyName("credential")]
    public string Password { get; set; } = "";

    public EndpointSettings? Endpoint { get; set; } = new();
    public SecretValue? Value { get; set; }
    public Dictionary<string, EndpointSettings> Servers { get; set; } = new();
    public EndpointList Items { get; set; } = new();
    public Phantom<EndpointSettings> UnusedTypeArgument { get; set; } = new();

    [JsonIgnore]
    public string Ignored { get; set; } = "not-persisted";
}

public class EndpointSettings
{
    public int Timeout { get; set; } = 30;
    public string Name { get; set; } = "server";

    [Protected]
    [JsonPropertyName("api/key~1")]
    public virtual string Token { get; set; } = "";
}

public struct SecretValue
{
    [Protected]
    public string? Secret { get; set; }
}

public sealed class EndpointList : List<EndpointSettings>
{
}

public sealed class Phantom<T>
{
    public int Number { get; set; }
}

[SettingsSection("Counters:Primary")]
public sealed class CounterSettings
{
    public int Value { get; set; }
}

[SettingsSection("Depth")]
public sealed class DepthSettings
{
    public DepthSettings? Child { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MailSettings))]
[JsonSerializable(typeof(CounterSettings))]
[JsonSerializable(typeof(DepthSettings))]
public partial class TestSettingsContext : JsonSerializerContext
{
}

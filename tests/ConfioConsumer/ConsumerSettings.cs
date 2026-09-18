using System.Collections.Generic;
using System.Text.Json.Serialization;
using Confio;

namespace ConfioConsumer
{
    [SettingsSection("Mail")]
    public sealed class MailSettings
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 587;
        public string? Label { get; set; } = "default";

        [Protected]
        [JsonPropertyName("credential")]
        public string Password { get; set; } = "";

        [Protected]
        public Credentials? Account { get; set; }

        public List<Credentials> Servers { get; set; } = new List<Credentials>();
        public Dictionary<string, Credentials> Accounts { get; set; } = new Dictionary<string, Credentials>();
        public TokenValue? OptionalToken { get; set; }
    }

    public struct TokenValue
    {
        [Protected]
        public string? Value { get; set; }
    }

    public sealed class Credentials
    {
        public string User { get; set; } = "test-user";

        [Protected]
        public string Token { get; set; } = "";
    }

    [SettingsSection("Counter")]
    public sealed class CounterSettings
    {
        public int Count { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(MailSettings))]
    [JsonSerializable(typeof(CounterSettings))]
    public partial class ConsumerSettingsContext : JsonSerializerContext
    {
    }
}

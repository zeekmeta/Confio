using Confio;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ConfioConsumerModels
{
    [SettingsSection("Shared")]
    public sealed record SharedSettings : IValidatableSettings
    {
        public int Count { get; init; }
        public int BatchSize { get; init; } = 8;

        [Protected]
        public string Secret { get; init; } = "";

        public void Validate()
        {
            if (Count < 0 || BatchSize < 1)
                throw new ValidationException("The count must be non-negative and the batch size must be positive.");
        }
    }

    [JsonSerializable(typeof(SharedSettings))]
    public partial class SharedSettingsContext : JsonSerializerContext
    {
    }
}

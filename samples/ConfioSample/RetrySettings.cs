using System.ComponentModel.DataAnnotations;
using Confio;

namespace ConfioSample;

[SettingsSection("Retry")]
public sealed record RetrySettings : IValidatableSettings
{
    public int MaxAttempts { get; init; } = 3;
    public int DelaySeconds { get; init; } = 5;

    public void Validate()
    {
        if (MaxAttempts < 1)
            throw new ValidationException("At least one retry attempt is required.");
        if (DelaySeconds < 0)
            throw new ValidationException("The retry delay must be non-negative.");
    }
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Confio;

namespace ConfioSample;

[SettingsSection("Mail")]
public sealed class MailSettings : IValidatableSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;

    [Protected]
    public string? Password { get; set; } = "";

    [Protected]
    public ApiCredentials? Credentials { get; set; } = new();

    public string[] Recipients { get; set; } = ["ops@example.com"];
    public string Notes { get; set; } = "默认值来自模型；读取不会创建文件。";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ValidationException("The mail host is required.");
        if (Port is < 1 or > 65535)
            throw new ValidationException("The mail port must be between 1 and 65535.");
        if (Recipients is null)
            throw new ValidationException("The recipients collection must not be null; use an empty array instead.");
    }
}

[JsonSerializable(typeof(MailSettings))]
[JsonSerializable(typeof(RetrySettings))]
public partial class AppSettingsContext : JsonSerializerContext
{
}

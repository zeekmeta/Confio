using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Confio;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace ConfioTests;

[SettingsSection("Mail")]
public sealed class FormatSettings : IValidatableSettings
{
    public int Port { get; set; } = 587;
    public string Code { get; set; } = "00123";
    public string BooleanText { get; set; } = "true";
    public bool UseTls { get; set; } = true;
    public decimal Ratio { get; set; } = 1.25m;
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double SpecialNumber { get; set; }
    public FormatMode Mode { get; set; } = FormatMode.Second;

    [JsonConverter(typeof(JsonStringEnumConverter<FormatMode>))]
    public FormatMode NamedMode { get; set; } = FormatMode.Second;

    public DateOnly Date { get; set; } = new(2026, 9, 16);
    public TimeOnly Time { get; set; } = new(12, 34, 56, 123);
    public DateTime Local { get; set; } = new(2026, 9, 16, 12, 34, 56, DateTimeKind.Unspecified);
    public DateTimeOffset Timestamp { get; set; } = new(2026, 9, 16, 12, 34, 56, TimeSpan.FromHours(8));
    public string[] Names { get; set; } = ["first", "second"];
    public List<FormatEndpoint> Endpoints { get; set; } = [new()];
    public Dictionary<string, FormatEndpoint> Servers { get; set; } = new() { ["a/b~c"] = new() };

    [Protected]
    public string? Password { get; set; } = "";

    [Protected]
    public FormatPayload? Credentials { get; set; } = new();

    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new ValidationException("The port is outside its allowed range.");
    }
}

public enum FormatMode
{
    First,
    [JsonStringEnumMemberName("001")]
    Second
}

public sealed class FormatEndpoint
{
    public string Name { get; set; } = "server";

    [Protected]
    [JsonPropertyName("api/key~1")]
    public string? Token { get; set; } = "endpoint-secret";
}

public sealed class FormatPayload
{
    public string? Optional { get; set; }
    public string Token { get; set; } = "whole-object-secret";
    public int[] Empty { get; set; } = [];
    public decimal Exact { get; set; } = 0.1234567890123456789012345678m;
}

[SettingsSection("Counter:Primary")]
public sealed record FormatCounter
{
    public int Value { get; init; } = 3;
}

[JsonSerializable(typeof(FormatSettings))]
[JsonSerializable(typeof(FormatCounter))]
public partial class FormatSettingsContext : JsonSerializerContext;

[TomlSerializable(typeof(TomlTable))]
internal partial class TestTomlContext : TomlSerializerContext;

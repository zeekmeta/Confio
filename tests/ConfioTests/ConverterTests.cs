using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confio;
using Xunit;

namespace ConfioTests;

public sealed class ConverterTests
{
    [Fact]
    public void UntypedMembersCannotBypassDeclaredProtection()
    {
        using var fixture = new TestFile();
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(UntypedContext.Default, fixture.FilePath));
    }

    [Fact]
    public void TypeLevelPopulationCannotChangeReplacementSemantics()
    {
        using var fixture = new TestFile();
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(PopulatingContext.Default, fixture.FilePath));
    }

    [Fact]
    public void ExplicitMemberSerializationKeepsItsNativeMeaningInsideProtectedObjects()
    {
        using var fixture = new TestFile();
        using var file = new ConfigurationFile(ConditionalChildContext.Default, fixture.FilePath,
            new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        file.Save(new ConditionalChildSettings { Value = new ConditionalValue { Count = 7 } });
        Assert.Equal(7, file.Read<ConditionalChildSettings>().Value.Count);
        file.Save(new ConditionalChildSettings());
        Assert.Equal(0, file.Read<ConditionalChildSettings>().Value.Count);
    }

    [Fact]
    public void PropertyConvertersCannotHideProtectionAlreadyMappedElsewhere()
    {
        using var fixture = new TestFile();
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(PropertyConverterContext.Default, fixture.FilePath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }

    [Fact]
    public void RuntimeConvertersCannotHideProtectedDescendantsOfAnIntermediateType()
    {
        using var fixture = new TestFile();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TokenContainerConverter());
        var context = new RuntimeConverterContext(options);
        Assert.Throws<NotSupportedException>(() => new ConfigurationFile(context, fixture.FilePath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }

    [Fact]
    public void WholeConvertedValuesAndEquivalentHandwrittenDeclarationsRoundTrip()
    {
        using var fixture = new TestFile();
        using (var file = new ConfigurationFile(WholeValueContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey }))
        {
            file.Save(new WholeValueSettings { Value = new TokenValue { Secret = "converted-secret" } });
        }
        Assert.DoesNotContain("converted-secret", File.ReadAllText(fixture.FilePath));
        using var manual = new ConfigurationFile(new HandwrittenContext(), fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        Assert.Equal("converted-secret", manual.Read<WholeValueSettings>().Value.Secret);
    }

    private sealed class HandwrittenContext : ISettingsContext
    {
        public IReadOnlyList<SettingsDeclaration> GetSettingsDeclarations() => new[]
        {
            SettingsDeclaration.Create<WholeValueSettings>(WholeValueContext.Default, "Whole",
                static type => type == typeof(WholeValueSettings) ? new WholeValueSettings() : null,
                new ProtectedMember(typeof(WholeValueSettings), nameof(WholeValueSettings.Value)))
        };
    }
}

public sealed class TokenValue
{
    [Protected]
    public string Secret { get; set; } = "";
}

public sealed class TokenContainer
{
    public TokenValue Child { get; set; } = new();
}

[SettingsSection("PropertyConverter")]
public sealed class PropertyConverterSettings
{
    public TokenValue Normal { get; set; } = new();

    [JsonConverter(typeof(TokenValueConverter))]
    public TokenValue Converted { get; set; } = new();
}

[SettingsSection("RuntimeConverter")]
public sealed class RuntimeConverterSettings
{
    public TokenValue Normal { get; set; } = new();
    public TokenContainer Converted { get; set; } = new();
}

[SettingsSection("Whole")]
public sealed class WholeValueSettings
{
    [Protected]
    [JsonConverter(typeof(TokenValueConverter))]
    public TokenValue Value { get; set; } = new();
}

public sealed class TokenValueConverter : JsonConverter<TokenValue>
{
    public override TokenValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Secret = reader.GetString()! };

    public override void Write(Utf8JsonWriter writer, TokenValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Secret);
}

public sealed class TokenContainerConverter : JsonConverter<TokenContainer>
{
    public override TokenContainer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Child = new TokenValue { Secret = reader.GetString()! } };

    public override void Write(Utf8JsonWriter writer, TokenContainer value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Child.Secret);
}

[JsonSerializable(typeof(PropertyConverterSettings))]
public partial class PropertyConverterContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(RuntimeConverterSettings))]
public partial class RuntimeConverterContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(WholeValueSettings))]
public partial class WholeValueContext : JsonSerializerContext
{
}

[SettingsSection("Populating")]
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class PopulatingSettings
{
    public List<int> Numbers { get; set; } = new() { 1 };
}

[JsonSerializable(typeof(PopulatingSettings))]
public partial class PopulatingContext : JsonSerializerContext
{
}

[SettingsSection("ConditionalChild")]
public sealed class ConditionalChildSettings
{
    [Protected]
    public ConditionalValue Value { get; set; } = new();
}

public sealed class ConditionalValue
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; set; }
}

[JsonSerializable(typeof(ConditionalChildSettings))]
public partial class ConditionalChildContext : JsonSerializerContext
{
}

[SettingsSection("Untyped")]
public sealed class UntypedSettings
{
    public object Value { get; set; } = new TokenValue();
}

[JsonSerializable(typeof(UntypedSettings))]
[JsonSerializable(typeof(TokenValue))]
public partial class UntypedContext : JsonSerializerContext
{
}

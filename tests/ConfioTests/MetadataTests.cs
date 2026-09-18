using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Confio;
using Xunit;

namespace ConfioTests;

public sealed class MetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSerializedHidingMembersCannotRemoveInheritedProtection(bool asynchronous)
    {
        using var fixture = new TestFile();
        fixture.Write("""{"Inherited":{"Private":{"token":"private-base-secret"},"Static":{"token":"static-base-secret"}}}""");
        var key = fixture.EncryptionKey;
        using var file = new ConfigurationFile(MetadataContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        var settings = asynchronous ? await file.ReadAsync<HiddenInheritanceSettings>(fixture.Token) : file.Read<HiddenInheritanceSettings>();
        Assert.Equal("private-base-secret", ((OriginalSecret)settings.Private).Secret);
        Assert.Equal("static-base-secret", ((OriginalSecret)settings.Static).Secret);
        var persisted = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("private-base-secret", persisted);
        Assert.DoesNotContain("static-base-secret", persisted);
        settings.Private = new PrivateHiddenSecret("saved-private-secret");
        settings.Static = new StaticHiddenSecret("saved-static-secret");
        if (asynchronous) await file.SaveAsync(settings, fixture.Token);
        else file.Save(settings);
        persisted = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("saved-private-secret", persisted);
        Assert.DoesNotContain("saved-static-secret", persisted);
        using var reopened = new ConfigurationFile(MetadataContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = key });
        var restored = reopened.Read<HiddenInheritanceSettings>();
        Assert.Equal("saved-private-secret", ((OriginalSecret)restored.Private).Secret);
        Assert.Equal("saved-static-secret", ((OriginalSecret)restored.Static).Secret);
    }

    [Fact]
    public void OverridesInheritProtectionWhileHiddenMembersKeepTheirOwnDeclaration()
    {
        using var fixture = new TestFile();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, """
            {"Members":{"Original":{"token":"base-secret"},"Overridden":{"token":"override-secret"},
            "Hidden":{"token":"visible-hidden-value"},"field":"field-secret"}}
            """);
        using var file = new ConfigurationFile(MetadataContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var settings = file.Read<MemberSettings>();
        Assert.Equal("base-secret", settings.Original.Secret);
        Assert.Equal("override-secret", settings.Overridden.Secret);
        Assert.Equal("visible-hidden-value", settings.Hidden.Secret);
        Assert.Equal("field-secret", settings.Field);
        var persisted = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("base-secret", persisted);
        Assert.DoesNotContain("override-secret", persisted);
        Assert.DoesNotContain("field-secret", persisted);
        Assert.Contains("visible-hidden-value", persisted);
    }

    [Fact]
    public void InterfaceCollectionsRetainNestedInitDefaultsAndProtection()
    {
        using var fixture = new TestFile();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, """
            {"Collections":{"Items":[{}, {"Count":0,"Secret":"list-secret"}],
            "Lookup":{"first":{"Secret":"dictionary-secret"}}}}
            """);
        using var file = new ConfigurationFile(MetadataContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var settings = file.Read<InterfaceCollectionSettings>();
        Assert.Equal(5, settings.Items[0].Count);
        Assert.Equal(0, settings.Items[1].Count);
        Assert.Equal(5, settings.Lookup["first"].Count);
        Assert.Equal("list-secret", settings.Items[1].Secret);
        Assert.Equal("dictionary-secret", settings.Lookup["first"].Secret);
        var persisted = File.ReadAllText(fixture.FilePath);
        Assert.DoesNotContain("list-secret", persisted);
        Assert.DoesNotContain("dictionary-secret", persisted);
        file.Save(settings);
        file.Reload();
        Assert.Equal(0, file.Read<InterfaceCollectionSettings>().Items[1].Count);
    }

    [Fact]
    public void ConstructorBindingUsesClrNamesAndRetainsExplicitInitValues()
    {
        using var fixture = new TestFile();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, """{"Constructor":{"Delay":0}}""");
        using var file = new ConfigurationFile(MetadataContext.Default, fixture.FilePath, options: new ConfigurationFileOptions { EncryptionKey = fixture.EncryptionKey });
        var settings = file.Read<ConstructorSettings>();
        Assert.Equal(7, settings.Count);
        Assert.Equal(0, settings.Delay);
        file.Save(new ConstructorSettings(4) { Delay = 23 });
        file.Reload();
        Assert.Equal(4, file.Read<ConstructorSettings>().Count);
        Assert.Equal(23, file.Read<ConstructorSettings>().Delay);
    }

    [Fact]
    public void UnboundReadonlyPropertiesCannotSilentlyLosePersistedValues()
    {
        using var fixture = new TestFile();
        Assert.Throws<NotSupportedException>(() =>
            new ConfigurationFile(ComputedContext.Default, fixture.FilePath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
    }
}

[SettingsSection("Members")]
public sealed class MemberSettings
{
    public OriginalSecret Original { get; set; } = new();
    public OverriddenSecret Overridden { get; set; } = new();
    public HiddenSecret Hidden { get; set; } = new();

    [Protected, JsonInclude, JsonPropertyName("field")]
    public string Field = "";
}

public class OriginalSecret
{
    [Protected, JsonPropertyName("token")]
    public virtual string Secret { get; init; } = "";
}

public sealed class OverriddenSecret : OriginalSecret
{
    [JsonPropertyName("token")]
    public override string Secret { get; init; } = "";
}

public sealed class HiddenSecret : OriginalSecret
{
    [JsonPropertyName("token")]
    public new string Secret { get; set; } = "";
}

[SettingsSection("Inherited")]
public sealed class HiddenInheritanceSettings
{
    public PrivateHiddenSecret Private { get; set; } = new();
    public StaticHiddenSecret Static { get; set; } = new();
}

public sealed class PrivateHiddenSecret : OriginalSecret
{
    public PrivateHiddenSecret(string secret = "") => base.Secret = secret;
    private new string Secret { get; set; } = "private-only";
}

public sealed class StaticHiddenSecret : OriginalSecret
{
    public StaticHiddenSecret(string secret = "") => base.Secret = secret;
    public new static string Secret { get; set; } = "static-only";
}

[SettingsSection("Collections")]
public sealed class InterfaceCollectionSettings
{
    public IReadOnlyList<InterfaceItem> Items { get; init; } = Array.Empty<InterfaceItem>();
    public IReadOnlyDictionary<string, InterfaceItem> Lookup { get; init; } = new Dictionary<string, InterfaceItem>();
}

public sealed record InterfaceItem
{
    public int Count { get; init; } = 5;
    [Protected] public string Secret { get; init; } = "";
}

[SettingsSection("Constructor")]
public sealed class ConstructorSettings(int count = 7)
{
    [JsonPropertyName("n")]
    public int Count { get; } = count;
    public int Delay { get; init; } = 11;
}

[SettingsSection("Computed")]
public sealed class ComputedSettings
{
    public int Count => 7;
}

[JsonSerializable(typeof(MemberSettings))]
[JsonSerializable(typeof(HiddenInheritanceSettings))]
[JsonSerializable(typeof(InterfaceCollectionSettings))]
[JsonSerializable(typeof(ConstructorSettings))]
public partial class MetadataContext : JsonSerializerContext;

[JsonSerializable(typeof(ComputedSettings))]
public partial class ComputedContext : JsonSerializerContext;

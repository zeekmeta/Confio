using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Confio;
using ConfioGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConfioTests;

public sealed class GeneratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothGeneratorsCompileTheSameInputInEitherOrderAndTheResultRuns(bool reverse)
    {
        const string source = """
            using Confio;
            using System.ComponentModel.DataAnnotations;
            using System.Text.Json.Serialization;
            namespace GeneratedConsumer
            {
                [SettingsSection("Mail")]
                public sealed record Settings : IValidatableSettings
                {
                    public int Port { get; init; } = 587;
                    [Protected] public string Password { get; init; } = "";

                    public void Validate()
                    {
                        if (Port < 1) throw new ValidationException("The port must be positive.");
                    }
                }
                public partial class Holder
                {
                    [JsonSerializable(typeof(Settings))]
                    [JsonSerializable(typeof(int))]
                    public partial class Context : JsonSerializerContext { }
                }
                public static class Probe
                {
                    public static int Run(string path, byte[] encryptionKey)
                    {
                        using var file = new ConfigurationFile(Holder.Context.Default, path,
                            options: new ConfigurationFileOptions { EncryptionKey = encryptionKey });
                        file.Save(file.Read<Settings>() with { Password = "generated-secret" });
                        file.Update<Settings>(value => value with { Port = 465 });
                        try
                        {
                            file.Save(new Settings { Port = -1 });
                            return -1;
                        }
                        catch (ValidationException) { }
                        file.Reload();
                        return file.Read<Settings>().Password == "generated-secret" ? file.Read<Settings>().Port : -1;
                    }
                }
            }
            """;
        var compilation = Compile(source);
        var jsonAssembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "System.Text.Json.SourceGeneration.dll"));
        var json = ((IIncrementalGenerator)Activator.CreateInstance(
            jsonAssembly.GetType("System.Text.Json.SourceGeneration.JsonSourceGenerator")!)!).AsSourceGenerator();
        var confio = new SettingsGenerator().AsSourceGenerator();
        var generators = reverse ? new[] { json, confio } : new[] { confio, json };
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: new CSharpParseOptions(LanguageVersion.CSharp12));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        using var assemblyBytes = new MemoryStream();
        var emitted = output.Emit(assemblyBytes);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        var first = driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources)
            .Select(generated => (generated.HintName, Text: generated.SourceText.ToString())).ToArray();
        driver = driver.RunGenerators(compilation);
        var repeated = driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources)
            .Select(generated => (generated.HintName, Text: generated.SourceText.ToString())).ToArray();
        Assert.Equal(first, repeated);

        assemblyBytes.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyBytes);
        using var fixture = new TestFile();
        var value = assembly.GetType("GeneratedConsumer.Probe")!.GetMethod("Run")!.Invoke(null,
            new object[] { fixture.FilePath, fixture.EncryptionKey });
        Assert.Equal(465, value);
        Assert.DoesNotContain("generated-secret", File.ReadAllText(fixture.FilePath));
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void InvalidDeclarationsReportActionableDiagnostics(string source, string expectedId)
    {
        var driver = CSharpGeneratorDriver.Create(new[] { new SettingsGenerator().AsSourceGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp12));
        var result = driver.RunGenerators(Compile(source)).GetRunResult();
        var diagnostic = Assert.Single(result.Diagnostics.Where(item => item.Id == expectedId));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Empty(result.Results.SelectMany(item => item.GeneratedSources));
    }

    public static IEnumerable<object[]> InvalidDeclarations()
    {
        const string imports = "using Confio; using System.Text.Json.Serialization; ";
        const string model = "[SettingsSection(\"Mail\")] public class Settings { public int Port { get; set; } } ";
        const string context = "[JsonSerializable(typeof(Settings))] public partial class Context : JsonSerializerContext { }";
        yield return new object[] { imports + model + context.Replace("partial ", ""), "CONFIO001" };
        yield return new object[] { imports + model.Replace("public class", "public abstract class") + context, "CONFIO002" };
        yield return new object[] { imports + model.Replace("public int Port", "public required int Port") + context, "CONFIO002" };
        yield return new object[] { imports + model.Replace("Mail", " Mail") + context, "CONFIO003" };
        yield return new object[] { imports + model + "[SettingsSection(\"Mail:Child\")] public class Second { } " +
                                   "[JsonSerializable(typeof(Second))] " + context, "CONFIO003" };
        yield return new object[] { imports + model.Replace("public int Port", "[Protected, JsonIgnore] public string Port") + context, "CONFIO004" };
        yield return new object[] { imports + model.Replace("public int Port", "[Protected] private string Port") + context, "CONFIO004" };
        yield return new object[] { imports + model.Replace("public int Port", "[Protected] public static string Port") + context, "CONFIO004" };
        yield return new object[] { imports + model + "[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)] " + context,
            "CONFIO005" };
        yield return new object[] { imports + model + "[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)] " + context,
            "CONFIO005" };
    }

    [Fact]
    public void UnselectedModelsDoNotRegisterThemselves()
    {
        const string source = """
            using Confio;
            using System.Text.Json.Serialization;
            [SettingsSection("Unused")] public class Unused { }
            [JsonSerializable(typeof(string))] public partial class Context : JsonSerializerContext { }
            """;
        var driver = CSharpGeneratorDriver.Create(new SettingsGenerator());
        var result = driver.RunGenerators(Compile(source)).GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Results.SelectMany(item => item.GeneratedSources));
    }

    private static CSharpCompilation Compile(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[] { typeof(ConfigurationFile).Assembly.Location }).Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path)).ToImmutableArray();
        return CSharpCompilation.Create("GeneratedConsumer_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12)) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}

using System;
using System.IO;
using Confio.Internal;

namespace Confio.Formats;

internal sealed class FileFormat
{
    private static readonly FileFormat Json = new(bytes =>
        new NodeDocument(bytes is null ? JsonData.Object() : JsonData.ParseFile(bytes), JsonData.Encode));
    private static readonly FileFormat Yaml = new(bytes =>
        new NodeDocument(bytes is null ? JsonData.Object() : YamlData.ParseFile(bytes), YamlData.Encode));
    private static readonly FileFormat Toml = new(TomlDocument.Parse);
    private static readonly FileFormat Ini = new(IniDocument.Parse);
    private readonly Func<byte[]?, FileDocument> _parse;

    private FileFormat(Func<byte[]?, FileDocument> parse)
    {
        _parse = parse;
    }

    internal static FileFormat FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => Json,
        ".yaml" or ".yml" => Yaml,
        ".toml" => Toml,
        ".ini" => Ini,
        _ => throw new NotSupportedException("Supported configuration file extensions are .json, .yaml, .yml, .toml and .ini.")
    };

    internal static FileFormat FromFormat(ConfigurationFormat format) => format switch
    {
        ConfigurationFormat.Json => Json,
        ConfigurationFormat.Yaml => Yaml,
        ConfigurationFormat.Toml => Toml,
        ConfigurationFormat.Ini => Ini,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal FileDocument Parse(byte[] bytes) => _parse(bytes);
    internal FileDocument Empty() => _parse(null);

    internal StringComparison PathComparison => this == Ini ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal void Validate(SettingsDeclaration declaration)
    {
        if (this == Ini)
        {
            IniDocument.Validate(declaration);
        }
    }
}

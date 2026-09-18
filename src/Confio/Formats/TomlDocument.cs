using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confio.Internal;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;

namespace Confio.Formats;

internal sealed class TomlDocument(TomlTable document) : FileDocument
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static TomlDocument Parse(byte[]? bytes)
    {
        if (bytes is null) return new TomlDocument(new TomlTable());
        try
        {
            var text = Utf8.GetString(bytes);
            // 固定节点转换会覆盖内联表的重复键；先执行上游的格式语义校验。
            SyntaxParser.ParseStrict(text, TomlDocumentContext.Default.Options, validate: true);
            var table = TomlSerializer.Deserialize(text, TomlDocumentContext.Default.TomlTable)!;
            Validate(table, 0);
            return new TomlDocument(table);
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException($"Invalid TOML at line {exception.Line}, column {exception.Column}.");
        }
    }

    internal override JsonObject ReadNodes(IEnumerable<SettingsDeclaration> declarations, SettingsDeclaration? excluded = null) =>
        (JsonObject)ToNode(document, excluded?.Segments)!;

    internal override void Set(string[] segments, JsonNode value, ModelSchema? schema = null)
    {
        var native = FromNode(value, schema, ConfigurationPath.Pointer(segments), segments.Length);
        if (segments.Length == 0)
        {
            document.Clear();
            foreach (var pair in (TomlTable)native) document.Add(pair.Key, pair.Value);
            return;
        }
        object current = document;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (current is TomlTable table)
            {
                var key = Key(table, segments[index]);
                if (!table.TryGetValue(key, out var child)) table[key] = child = new TomlTable();
                current = child;
            }
            else
            {
                current = Element(current, segments[index]);
            }
        }
        if (current is TomlTable target) target[Key(target, segments[^1])] = native;
        else if (current is TomlArray array) array[Index(segments[^1])] = native;
        else throw new InvalidDataException("A TOML configuration value has a non-table parent.");
    }

    internal override bool Remove(string[] segments)
    {
        if (segments.Length == 0)
        {
            var changed = document.Count != 0;
            document.Clear();
            return changed;
        }
        var current = document;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!current.TryGetValue(Key(current, segments[index]), out var child)) return false;
            current = child as TomlTable ?? throw new InvalidDataException("A TOML section has a non-table parent.");
        }
        return current.Remove(Key(current, segments[^1]));
    }

    internal override byte[] Encode()
    {
        try
        {
            return Utf8.GetBytes(TomlSerializer.Serialize(document, TomlDocumentContext.Default.TomlTable));
        }
        catch (TomlException)
        {
            throw new InvalidDataException("The configuration contains a value that cannot be written as TOML.");
        }
    }

    private static string Key(TomlTable table, string name) =>
        table.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.Ordinal)) ?? name;

    private static int Index(string segment) => int.Parse(segment, CultureInfo.InvariantCulture);

    private static object Element(object parent, string segment) => parent switch
    {
        TomlArray array => array[Index(segment)]!,
        TomlTableArray array => array[Index(segment)],
        _ => throw new InvalidDataException("A TOML configuration value has a non-container parent.")
    };

    private static void Validate(object? value, int depth)
    {
        if (value is TomlTable or TomlArray or TomlTableArray && depth >= JsonData.MaximumDepth)
            throw new InvalidDataException("The TOML configuration exceeds the maximum depth of 64.");
        if (value is TomlTable table)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in table)
            {
                if (!names.Add(pair.Key))
                    throw new InvalidDataException("TOML configuration property names must be unique.");
                Validate(pair.Value, depth + 1);
            }
        }
        else if (value is TomlArray array)
        {
            foreach (var child in array) Validate(child, depth + 1);
        }
        else if (value is TomlTableArray tables)
        {
            foreach (var child in tables) Validate(child, depth + 1);
        }
    }

    private static JsonNode? ToNode(object? value, string[]? excluded = null, int pathIndex = 0)
    {
        if (excluded is { Length: 0 }) return JsonData.Object();
        if (value is TomlTable table)
        {
            var result = JsonData.Object();
            foreach (var pair in table)
            {
                var matches = excluded is not null && string.Equals(pair.Key, excluded[pathIndex], StringComparison.Ordinal);
                if (matches && pathIndex == excluded!.Length - 1) continue;
                result.Add(pair.Key, ToNode(pair.Value, matches ? excluded : null, pathIndex + 1));
            }
            return result;
        }
        if (value is TomlArray array) return new JsonArray(array.Select(item => ToNode(item)).ToArray());
        if (value is TomlTableArray tables) return new JsonArray(tables.Select(item => ToNode(item)).ToArray());
        return value switch
        {
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            long integer => JsonValue.Create(integer),
            double number when IsFinite(number) => JsonValue.Create(number),
            TomlDateTime date => JsonValue.Create(date.ToString()),
            _ => throw new InvalidDataException("TOML values require tables, arrays, strings, finite numbers, Booleans or dates.")
        };
    }

    private static object FromNode(JsonNode? node, ModelSchema? schema, string pointer, int depth)
    {
        if (node is null) throw new ConfigurationValueException("TOML", pointer, ConfigurationValueError.Null);
        if (node is JsonObject or JsonArray && depth >= JsonData.MaximumDepth)
            throw new ConfigurationValueException("TOML", pointer, ConfigurationValueError.MaximumDepthExceeded);
        if (node is JsonObject obj)
        {
            var result = new TomlTable();
            foreach (var pair in obj)
                result.Add(pair.Key, FromNode(pair.Value, schema?.Child(pair.Key), ConfigurationPath.Append(pointer, pair.Key), depth + 1));
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new TomlArray();
            for (var index = 0; index < array.Count; index++)
                result.Add(FromNode(array[index], schema?.Element,
                    ConfigurationPath.Append(pointer, index.ToString(CultureInfo.InvariantCulture)), depth + 1));
            return result;
        }
        if (node.GetValueKind() == JsonValueKind.String)
        {
            var text = node.GetValue<string>();
            return NativeDate(text, schema) ?? (object)text;
        }
        if (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return node.GetValue<bool>();

        var raw = node.ToJsonString();
        var type = schema?.Metadata.Type;
        var floating = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ||
#if !NETFRAMEWORK
            type == typeof(Half) ||
#endif
            raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
        if (!floating)
        {
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return integer;
            throw new ConfigurationValueException("TOML", pointer, ConfigurationValueError.IntegerOutOfRange);
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !IsFinite(number) ||
            !JsonNode.DeepEquals(node, JsonNode.Parse(number.ToString("R", CultureInfo.InvariantCulture))))
        {
            throw new ConfigurationValueException("TOML", pointer, ConfigurationValueError.NumericPrecisionLoss);
        }
        return number;
    }

    private static TomlDateTime? NativeDate(string text, ModelSchema? schema)
    {
        if (schema is null || schema.Metadata.Converter.GetType().Assembly != typeof(JsonSerializer).Assembly) return null;
        var type = schema.Metadata.Type;
#if !NETFRAMEWORK
        if (type == typeof(DateOnly))
        {
            var date = DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new TomlDateTime(date.Year, date.Month, date.Day);
        }
        if (type == typeof(TimeOnly))
        {
            var time = TimeOnly.Parse(text, CultureInfo.InvariantCulture);
            return new TomlDateTime(new DateTimeOffset(DateOnly.MinValue.ToDateTime(time), TimeSpan.Zero), 7, TomlDateTimeKind.LocalTime);
        }
#endif
        if (type == typeof(DateTimeOffset))
            return new TomlDateTime(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture), 7, TomlDateTimeKind.OffsetDateTimeByNumber);
        if (type == typeof(DateTime))
        {
            var date = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var offset = date.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(date, TimeSpan.Zero) : new DateTimeOffset(date);
            var kind = date.Kind switch
            {
                DateTimeKind.Utc => TomlDateTimeKind.OffsetDateTimeByZ,
                DateTimeKind.Local => TomlDateTimeKind.OffsetDateTimeByNumber,
                _ => TomlDateTimeKind.LocalDateTime
            };
            return new TomlDateTime(offset, 7, kind);
        }
        return null;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// 只生成固定文件节点类型；业务模型继续使用应用已有的 JSON 静态上下文。
[TomlSourceGenerationOptions(MaxDepth = JsonData.MaximumDepth)]
[TomlSerializable(typeof(TomlTable))]
internal partial class TomlDocumentContext : TomlSerializerContext;

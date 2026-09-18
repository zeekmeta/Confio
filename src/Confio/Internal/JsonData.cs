using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace Confio.Internal;

internal static class JsonData
{
    internal const int MaximumDepth = 64;
    private static readonly JavaScriptEncoder FileEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    internal static JsonObject Object() => new(new JsonNodeOptions { PropertyNameCaseInsensitive = false });

    internal static JsonNode? Serialize(object value, JsonTypeInfo metadata)
    {
        // SerializeToNode 会把属性匹配选项套到字典节点上；节点键必须保留原有大小写。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, metadata);
        try
        {
            return JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { MaxDepth = MaximumDepth });
        }
        finally { bytes.AsSpan().Clear(); }
    }

    internal static JsonObject ParseFile(byte[] bytes)
    {
        try
        {
            var node = JsonNode.Parse(bytes,
                new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaximumDepth });
            var root = node as JsonObject ?? throw new InvalidDataException("The configuration document must be a JSON object.");
            Validate(root);
            return root;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}.");
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The configuration document contains conflicting property names.");
        }
    }

    internal static void Validate(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in obj)
            {
                if (!names.Add(property.Key))
                {
                    throw new InvalidDataException("Configuration property names must be unique.");
                }
                Validate(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                Validate(child);
            }
        }
    }

    internal static bool NeedsProtection(JsonNode? node) =>
        node is not null && !(node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0);

    internal static bool IsConversionFailure(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException;

    internal static InvalidDataException InvalidValue(string path, Exception? exception = null) =>
        new($"The configuration value at section '{path}', member '{(exception as JsonException)?.Path ?? "$"}' cannot be converted using its static metadata.");

    internal static string NormalizeNumber(string value, bool floating = false)
    {
        // 调用方先校验词法；只整理符号、前导零和小数点，不经过有限精度的 CLR 数值。
        var sign = value[0] == '-' ? "-" : "";
        value = value.TrimStart('+', '-');
        var exponentIndex = value.IndexOfAny(['e', 'E']);
        var exponent = exponentIndex < 0 ? "" : value[exponentIndex..];
        var mantissa = exponentIndex < 0 ? value : value[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        var integer = (dot < 0 ? mantissa : mantissa[..dot]).TrimStart('0');
        var fraction = dot < 0 ? "" : mantissa[(dot + 1)..];
        if (integer.Length == 0) integer = "0";
        var suffix = dot >= 0 || (floating && exponent.Length == 0)
            ? "." + (fraction.Length == 0 ? "0" : fraction) : "";
        return sign + integer + suffix + exponent;
    }

    internal static byte[] Encode(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            MaxDepth = MaximumDepth,
            Encoder = FileEncoder
        }))
        {
            node.WriteTo(writer);
        }
        return stream.ToArray();
    }
}

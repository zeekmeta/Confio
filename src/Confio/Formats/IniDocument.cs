using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Confio.Internal;
using Confio.Protection;
using Microsoft.Extensions.Configuration.Ini;

namespace Confio.Formats;

internal sealed class IniDocument(JsonObject document) : NodeDocument(document, Encode)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static IniDocument Parse(byte[]? bytes)
    {
        var root = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        if (bytes is null) return new IniDocument(root);
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var stream = new MemoryStream(bytes);
            foreach (var pair in IniStreamConfigurationProvider.Read(stream))
            {
                var segments = ConfigurationPath.Parse(pair.Key);
                if (segments.Length > JsonData.MaximumDepth)
                    throw new InvalidDataException("The INI configuration exceeds the maximum depth of 64.");
                ConfigurationPath.Get(root, segments, out var exists);
                if (exists)
                    throw new InvalidDataException("INI keys cannot be both scalar values and parent paths.");
                ConfigurationPath.Set(root, segments, JsonValue.Create(pair.Value ?? ""));
            }
            return new IniDocument(root);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // 原生解析异常包含原文或键名，不能把它们作为内部异常传播。
            throw new InvalidDataException("The INI document contains invalid UTF-8, invalid lines or conflicting keys.");
        }
    }

    internal static void Validate(SettingsDeclaration declaration)
    {
        foreach (var segment in declaration.Segments) ValidateSegment(segment);
        var visited = new HashSet<ModelSchema>();
        Visit(declaration.Schema);

        void Visit(ModelSchema schema)
        {
            if (!visited.Add(schema)) return;
            if (schema.Metadata.Kind == JsonTypeInfoKind.None)
            {
                ValidateScalar(schema.Metadata.Type, schema.Metadata.Converter);
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in schema.Properties.Values)
            {
                if (!names.Add(property.Name))
                    throw new NotSupportedException("INI model property names must be unique ignoring case.");
                ValidateSegment(property.Name);
                // 整体保护后的载荷是字符串，内部数据不受 INI 表达范围限制。
                if (property.IsProtected) continue;
                if (property.Child is not null) Visit(property.Child);
                else ValidateScalar(property.Metadata.PropertyType, property.Metadata.CustomConverter!);
            }
            if (schema.Element is not null) Visit(schema.Element);
        }
    }

    internal override JsonObject ReadNodes(IEnumerable<SettingsDeclaration> declarations, SettingsDeclaration? excluded = null)
    {
        var nodes = base.ReadNodes(declarations, excluded);
        foreach (var declaration in declarations)
        {
            if (declaration == excluded) continue;
            var node = ConfigurationPath.Get(nodes, declaration.Segments, out var exists);
            if (exists)
            {
                ConfigurationPath.Set(nodes, declaration.Segments, ReadValue(node, declaration.Schema)!);
            }
        }
        return nodes;
    }

    internal override bool Remove(string[] segments)
    {
        if (!base.Remove(segments)) return false;
        // INI 的节头没有空对象语义；删除最后一个键时，同时移除只剩路径作用的父节点。
        for (var length = segments.Length - 1; length > 0; length--)
        {
            var parent = segments.AsSpan(0, length).ToArray();
            if (ConfigurationPath.Get(Document, parent, out _) is not JsonObject { Count: 0 }) break;
            ConfigurationPath.Remove(Document, parent);
        }
        return true;
    }

    private static JsonNode? ReadValue(JsonNode? node, ModelSchema schema)
    {
        if (node is null) return null;
        if (schema.Metadata.Kind is JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary && node is JsonObject obj)
        {
            var result = JsonData.Object();
            foreach (var pair in obj)
            {
                var value = pair.Value;
                var property = schema.Properties.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (property is not null)
                {
                    if (property.IsProtected && (NodeProtection.IsEncrypted(value) || !JsonData.NeedsProtection(value)))
                    {
                        result.Add(property.Name, value?.DeepClone());
                    }
                    else
                    {
                        result.Add(property.Name, property.Child is not null
                            ? ReadValue(value, property.Child)
                            : ReadScalar(value, property.Metadata.PropertyType, property.Metadata.CustomConverter!));
                    }
                }
                else
                {
                    result.Add(pair.Key, schema.Element is not null ? ReadValue(value, schema.Element) : value?.DeepClone());
                }
            }
            return result;
        }
        if (schema.Metadata.Kind == JsonTypeInfoKind.Enumerable)
        {
            var result = new JsonArray();
            if (node is JsonArray array)
            {
                foreach (var value in array) result.Add(ReadValue(value!, schema.Element!));
                return result;
            }
            if (node is JsonObject indices)
            {
                for (var index = 0; index < indices.Count; index++)
                {
                    if (!indices.TryGetPropertyValue(index.ToString(CultureInfo.InvariantCulture), out var value))
                        throw new InvalidDataException("INI array indices must be consecutive integers starting at zero.");
                    result.Add(ReadValue(value!, schema.Element!));
                }
                return result;
            }
            throw new InvalidDataException("An INI collection requires indexed child keys.");
        }
        if (schema.Metadata.Kind == JsonTypeInfoKind.None)
            return ReadScalar(node, schema.Metadata.Type, schema.Metadata.Converter, schema.Metadata);
        throw new InvalidDataException("An INI object requires child keys.");
    }

    private static JsonNode? ReadScalar(JsonNode? node, Type type, JsonConverter converter, JsonTypeInfo? metadata = null)
    {
        ValidateScalar(type, converter);
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return node?.DeepClone();
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            // 名称可能恰好是数字文本；先遵守实际枚举转换器，再尝试数值形式。
            if (metadata is not null)
            {
                try
                {
                    _ = node.Deserialize(metadata);
                    return node.DeepClone();
                }
                catch (JsonException) { }
            }
            else if (converter is JsonStringEnumConverter || converter.GetType().IsGenericType &&
                     converter.GetType().GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>))
            {
                return node.DeepClone();
            }
        }
        if (type == typeof(bool))
        {
            if (bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            throw new InvalidDataException("An INI Boolean value must be true or false.");
        }
        if ((IsNumber(type) || type.IsEnum) && NumberPattern.IsMatch(text))
        {
            return JsonNode.Parse(JsonData.NormalizeNumber(text))!;
        }
        // 命名浮点值等文本是否有效，继续由上下文或属性上的原生转换选项决定。
        return node.DeepClone();
    }

    private static void ValidateScalar(Type type, JsonConverter converter)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (converter.GetType().Assembly != typeof(JsonSerializer).Assembly ||
            !(type.IsEnum || IsNumber(type) || type == typeof(bool) || type == typeof(string) || type == typeof(char) ||
              type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
#if !NETFRAMEWORK
              type == typeof(DateOnly) || type == typeof(TimeOnly) ||
#endif
              type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Version) || type == typeof(byte[])))
        {
            throw new NotSupportedException("INI scalar values require supported built-in static converters. A complete protected value may use other converters.");
        }
    }

    private static bool IsNumber(Type type) => !type.IsEnum && (Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal
#if !NETFRAMEWORK
        || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Half)
#endif
        );

    private static readonly Regex NumberPattern = new(@"\A[-+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?\z", RegexOptions.CultureInvariant);

    private static byte[] Encode(JsonObject root)
    {
        ValidateNames(root, "");
        var text = new StringBuilder();
        // 全局标量必须先输出；进入节以后，原生 INI 语法没有返回全局节的标记。
        foreach (var pair in root.Where(pair => pair.Value is not JsonObject and not JsonArray))
        {
            var pointer = ConfigurationPath.Append("", pair.Key);
            ValidateSegment(pair.Key, pointer);
            WriteValue(text, pair.Key, pair.Value, pointer, 0);
        }
        foreach (var pair in root.Where(pair => pair.Value is JsonObject or JsonArray))
        {
            var pointer = ConfigurationPath.Append("", pair.Key);
            ValidateSegment(pair.Key, pointer);
            text.Append('\n').Append('[').Append(pair.Key).Append("]\n");
            WriteChildren(text, "", pair.Value!, pointer, 1);
        }
        return Utf8.GetBytes(text.ToString());
    }

    private static void WriteChildren(StringBuilder text, string prefix, JsonNode node, string pointer, int depth)
    {
        if (depth >= JsonData.MaximumDepth)
            throw new ConfigurationValueException("INI", pointer, ConfigurationValueError.MaximumDepthExceeded);
        if (node is JsonObject obj && obj.Count != 0)
        {
            ValidateNames(obj, pointer);
            foreach (var pair in obj)
            {
                var childPointer = ConfigurationPath.Append(pointer, pair.Key);
                ValidateSegment(pair.Key, childPointer);
                WriteValue(text, prefix + pair.Key, pair.Value, childPointer, depth);
            }
        }
        else if (node is JsonArray array && array.Count != 0)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var segment = index.ToString(CultureInfo.InvariantCulture);
                WriteValue(text, prefix + segment, array[index], ConfigurationPath.Append(pointer, segment), depth);
            }
        }
        else
        {
            throw new ConfigurationValueException("INI", pointer, node is JsonArray
                ? ConfigurationValueError.EmptyCollection : ConfigurationValueError.EmptyObject);
        }
    }

    private static void WriteValue(StringBuilder text, string path, JsonNode? node, string pointer, int depth)
    {
        if (node is JsonObject or JsonArray)
        {
            WriteChildren(text, path + ":", node, pointer, depth + 1);
            return;
        }
        if (node is null) throw new ConfigurationValueException("INI", pointer, ConfigurationValueError.Null);
        if (path[0] is ';' or '#' or '/' or '[')
            throw new ConfigurationValueException("INI", pointer, ConfigurationValueError.UnsupportedKey);
        var value = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ConfigurationValueException("INI", pointer, ConfigurationValueError.MultilineString);
        text.Append(path).Append('=');
        if (value.Length != 0)
        {
            // 仅在原生解析器会修剪内容时增加一对外层引号，不引入额外转义协议。
            var quote = value != value.Trim() || value.Length >= 2 && value[0] == '"' && value[^1] == '"';
            if (quote) text.Append('"').Append(value).Append('"');
            else text.Append(value);
        }
        text.Append('\n');
    }

    private static void ValidateSegment(string segment, string? pointer = null)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment != segment.Trim() || segment.AsSpan().IndexOfAny(":=\r\n".AsSpan()) >= 0)
        {
            if (pointer is not null) throw new ConfigurationValueException("INI", pointer, ConfigurationValueError.UnsupportedKey);
            throw new NotSupportedException("INI path segments must be non-empty, unpadded and cannot contain ':', '=', CR or LF.");
        }
    }

    private static void ValidateNames(JsonObject obj, string pointer)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in obj)
        {
            if (!names.Add(pair.Key))
                throw new ConfigurationValueException("INI", ConfigurationPath.Append(pointer, pair.Key), ConfigurationValueError.UnsupportedKey);
        }
    }
}

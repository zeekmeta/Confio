using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Confio.Internal;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Confio.Formats;

internal static class YamlData
{
    private const string TagPrefix = "tag:yaml.org,2002:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static JsonObject ParseFile(byte[] bytes)
    {
        try
        {
            using var text = new StringReader(Utf8.GetString(bytes));
            var reader = new Parser(text);
            reader.Consume<StreamStart>();
            if (reader.Accept<StreamEnd>(out _))
            {
                throw new InvalidDataException("The configuration document must be a YAML mapping.");
            }
            var start = reader.Consume<DocumentStart>();
            if (start.Version is { Version: var version } && (version.Major != 1 || version.Minor != 2))
            {
                throw Invalid("Only YAML 1.2 is supported", start.Start);
            }
            var document = new DocumentReader(reader).ReadNode(0);
            reader.Consume<DocumentEnd>();
            reader.Consume<StreamEnd>();
            return document as JsonObject ??
                throw new InvalidDataException("The configuration document must be a YAML mapping.");
        }
        catch (YamlException exception)
        {
            // 第三方异常可能包含配置正文；只保留位置，不传播正文或内部异常。
            throw Invalid("Invalid YAML", exception.Start);
        }
    }

    internal static byte[] Encode(JsonObject document)
    {
        try
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            var emitter = new Emitter(text, new EmitterSettings().WithNewLine("\n"));
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            WriteNode(emitter, document, 0);
            emitter.Emit(new DocumentEnd(isImplicit: true));
            emitter.Emit(new StreamEnd());
            return Utf8.GetBytes(text.ToString());
        }
        catch (YamlException)
        {
            throw new InvalidDataException("The configuration contains a value that cannot be written as YAML.");
        }
    }

    private static void WriteNode(Emitter emitter, JsonNode? node, int depth)
    {
        if (node is JsonObject obj)
        {
            CheckDepth(depth, Mark.Empty);
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
            foreach (var property in obj)
            {
                WriteString(emitter, property.Key);
                WriteNode(emitter, property.Value, depth + 1);
            }
            emitter.Emit(new MappingEnd());
        }
        else if (node is JsonArray array)
        {
            CheckDepth(depth, Mark.Empty);
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
            foreach (var value in array)
            {
                WriteNode(emitter, value, depth + 1);
            }
            emitter.Emit(new SequenceEnd());
        }
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            WriteString(emitter, value.GetValue<string>());
        }
        else
        {
            emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, node?.ToJsonString() ?? "null", ScalarStyle.Plain, true, true));
        }
    }

    private static void WriteString(Emitter emitter, string value)
    {
        // 转义换行，避免 YAML 的行结束符归一改变字符串及字典键。
        var quoted = ResolveTag(value) != "str" || value.AsSpan().IndexOfAny("\r\n\u0085\u2028\u2029".AsSpan()) >= 0;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value,
            quoted ? ScalarStyle.DoubleQuoted : ScalarStyle.Any, true, true));
    }

    private static JsonNode? ReadScalar(Scalar scalar)
    {
        var value = scalar.Value;
        var tag = (scalar.Tag.IsEmpty ? null : scalar.Tag.Value) switch
        {
            null => scalar.Style == ScalarStyle.Plain ? ResolveTag(value) : "str",
            "!" => "str",
            var explicitTag when explicitTag.StartsWith(TagPrefix, StringComparison.Ordinal) => explicitTag[TagPrefix.Length..],
            _ => throw Invalid("Unsupported YAML tag", scalar.Start)
        };
        switch (tag)
        {
            case "str":
                return JsonValue.Create(value);
            case "null" when ResolveTag(value) == "null":
                return null;
            case "bool" when ResolveTag(value) == "bool":
                return JsonValue.Create(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            case "int" when IntegerPattern.IsMatch(value):
                return JsonNode.Parse(NormalizeInteger(value));
            case "float" when FloatPattern.IsMatch(value):
                if (value.TrimStart('+', '-') is ".inf" or ".Inf" or ".INF" or ".nan" or ".NaN" or ".NAN")
                {
                    throw Invalid("Non-finite YAML numbers are not supported", scalar.Start);
                }
                return JsonNode.Parse(JsonData.NormalizeNumber(value, floating: true));
            default:
                throw Invalid("Unsupported YAML tag or invalid scalar for its tag", scalar.Start);
        }
    }

    private static string ResolveTag(string value)
    {
        if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            return "null";
        }
        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return "bool";
        }
        if (IntegerPattern.IsMatch(value))
        {
            return "int";
        }
        return FloatPattern.IsMatch(value) ? "float" : "str";
    }

    private static string NormalizeInteger(string value)
    {
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            return BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
        }
        if (value.StartsWith("0o", StringComparison.Ordinal))
        {
            var number = BigInteger.Zero;
            foreach (var digit in value.AsSpan(2))
            {
                number = (number << 3) + (digit - '0');
            }
            return number.ToString(CultureInfo.InvariantCulture);
        }
        return JsonData.NormalizeNumber(value);
    }

    private static readonly Regex IntegerPattern = new(@"\A(?:[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex FloatPattern = new(@"\A(?:[-+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))\z", RegexOptions.CultureInvariant);

    private static void CheckDepth(int depth, Mark mark)
    {
        if (depth >= JsonData.MaximumDepth)
        {
            throw Invalid("The YAML configuration exceeds the maximum depth of 64", mark);
        }
    }

    private static InvalidDataException Invalid(string reason, Mark mark) =>
        new($"{reason} at line {mark.Line}, column {mark.Column}.");

    private sealed class DocumentReader(IParser reader)
    {
        private readonly Dictionary<AnchorName, Anchor> _anchors = new();
        private int _remainingAliasNodes = 100_000;

        internal JsonNode? ReadNode(int depth)
        {
            if (reader.TryConsume<AnchorAlias>(out var alias))
            {
                if (!_anchors.TryGetValue(alias.Value, out var source) || !source.Complete)
                {
                    throw Invalid("YAML aliases must refer to an earlier, completed node", alias.Start);
                }
                CheckAlias(source.Node, depth, alias.Start);
                return source.Node?.DeepClone();
            }

            if (!reader.Accept<NodeEvent>(out var start))
            {
                throw new InvalidDataException("Expected a YAML configuration value.");
            }
            Anchor? anchor = null;
            if (!start.Anchor.IsEmpty)
            {
                // 完成前占位，拒绝循环引用；同名锚点遵循最近一次声明。
                _anchors[start.Anchor] = anchor = new Anchor();
            }
            JsonNode? node;
            if (start is Scalar scalar)
            {
                reader.Consume<Scalar>();
                node = ReadScalar(scalar);
            }
            else if (start is MappingStart mapping)
            {
                CheckContainer(mapping, "map", depth);
                reader.Consume<MappingStart>();
                var obj = JsonData.Object();
                while (!reader.Accept<MappingEnd>(out _))
                {
                    var mark = reader.Current!.Start;
                    var key = ReadNode(depth + 1);
                    if (key is not JsonValue keyValue || keyValue.GetValueKind() != JsonValueKind.String)
                    {
                        throw Invalid("YAML configuration keys must be strings", mark);
                    }
                    var property = keyValue.GetValue<string>();
                    if (obj.ContainsKey(property))
                    {
                        throw Invalid("Configuration property names must be unique ignoring case", mark);
                    }
                    obj.Add(property, ReadNode(depth + 1));
                }
                reader.Consume<MappingEnd>();
                node = obj;
            }
            else if (start is SequenceStart sequence)
            {
                CheckContainer(sequence, "seq", depth);
                reader.Consume<SequenceStart>();
                var array = new JsonArray();
                while (!reader.Accept<SequenceEnd>(out _))
                {
                    array.Add(ReadNode(depth + 1));
                }
                reader.Consume<SequenceEnd>();
                node = array;
            }
            else
            {
                throw Invalid("Unsupported YAML configuration node", start.Start);
            }
            if (anchor is not null)
            {
                anchor.Node = node;
                anchor.Complete = true;
            }
            return node;
        }

        private static void CheckContainer(NodeEvent node, string tag, int depth)
        {
            CheckDepth(depth, node.Start);
            if (!node.Tag.IsEmpty && node.Tag.Value != "!" && node.Tag.Value != TagPrefix + tag)
            {
                throw Invalid("Unsupported YAML collection tag", node.Start);
            }
        }

        private void CheckAlias(JsonNode? node, int depth, Mark mark)
        {
            // 在复制之前限制展开量和实际深度，避免小型别名文档指数膨胀。
            if (--_remainingAliasNodes < 0)
            {
                throw Invalid("YAML aliases exceed the expansion limit of 100000 nodes", mark);
            }
            if (node is JsonObject obj)
            {
                CheckDepth(depth, mark);
                foreach (var property in obj)
                {
                    CheckAlias(property.Value, depth + 1, mark);
                }
            }
            else if (node is JsonArray array)
            {
                CheckDepth(depth, mark);
                foreach (var child in array)
                {
                    CheckAlias(child, depth + 1, mark);
                }
            }
        }

        private sealed class Anchor
        {
            internal JsonNode? Node { get; set; }
            internal bool Complete { get; set; }
        }
    }
}

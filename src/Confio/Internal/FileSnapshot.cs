using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Confio.Internal;

// 模型节点与原生投影共享一次发布，发布后均不再修改。
internal sealed class FileSnapshot(JsonObject document, Dictionary<string, string?>? configuration = null)
{
    internal JsonObject Document { get; } = document;
    internal Dictionary<string, string?>? Configuration { get; } = configuration;

    internal FileSnapshot WithConfiguration()
    {
        if (Configuration is not null)
        {
            return this;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in Document)
        {
            Visit(property.Value, property.Key, values);
        }
        return new FileSnapshot(Document, values);
    }

    private static void Visit(JsonNode? node, string path, Dictionary<string, string?> values)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count > 0:
                foreach (var property in obj)
                {
                    Visit(property.Value, path + ":" + property.Key, values);
                }
                return;
            case JsonArray array when array.Count > 0:
                for (var index = 0; index < array.Count; index++)
                {
                    Visit(array[index], path + ":" + index.ToString(CultureInfo.InvariantCulture), values);
                }
                return;
        }

        var value = node switch
        {
            JsonArray => string.Empty,
            JsonObject or null => null,
            _ => node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                _ => node.ToJsonString()
            }
        };
        if (values.ContainsKey(path))
        {
            // 路径可能来自敏感字典键，诊断不包含键或值。
            throw new InvalidDataException("The configuration document contains conflicting flattened keys.");
        }
        values.Add(path, value);
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Confio.Internal;

internal static class ConfigurationPath
{
    internal static string[] Parse(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length == 0) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A non-empty configuration section path is required.", nameof(path));
        }

        var segments = path.Split(':');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment != segment.Trim())
            {
                throw new ArgumentException("Configuration section paths cannot contain empty or whitespace-padded segments.", nameof(path));
            }
        }
        return segments;
    }

    internal static JsonNode? Get(JsonObject root, string[] segments, out bool exists)
    {
        if (segments.Length == 0)
        {
            exists = root.Count != 0;
            return root;
        }
        JsonObject current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!current.TryGetPropertyValue(segments[index], out var child))
            {
                exists = false;
                return null;
            }
            if (index == segments.Length - 1)
            {
                exists = true;
                return child;
            }
            current = child as JsonObject ??
                throw new InvalidDataException("A configuration section has a non-object parent.");
        }
        throw new InvalidOperationException("The section path is empty.");
    }

    internal static void Set(JsonObject root, string[] segments, JsonNode value)
    {
        if (segments.Length == 0)
        {
            var replacement = value.AsObject();
            root.Clear();
            foreach (var pair in replacement) root.Add(pair.Key, pair.Value?.DeepClone());
            return;
        }
        JsonNode current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (current is JsonArray array)
            {
                current = array[int.Parse(segments[index], CultureInfo.InvariantCulture)]!;
            }
            else if (current is not JsonObject obj)
            {
                throw new InvalidDataException("A configuration value has a non-container parent.");
            }
            else if (!obj.TryGetPropertyValue(segments[index], out var child))
            {
                var parent = new JsonObject(obj.Options);
                obj.Add(segments[index], parent);
                current = parent;
            }
            else
            {
                current = child!;
            }
        }
        if (current is JsonObject target)
        {
            target[segments[^1]] = value;
        }
        else if (current is JsonArray array)
        {
            array[int.Parse(segments[^1], CultureInfo.InvariantCulture)] = value;
        }
        else
        {
            throw new InvalidDataException("A configuration value has a non-container parent.");
        }
    }

    internal static bool Remove(JsonObject root, string[] segments)
    {
        if (segments.Length == 0)
        {
            var changed = root.Count != 0;
            root.Clear();
            return changed;
        }
        JsonObject current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!current.TryGetPropertyValue(segments[index], out var child))
            {
                return false;
            }
            current = child as JsonObject ??
                throw new InvalidDataException("A configuration section has a non-object parent.");
        }
        return current.Remove(segments[segments.Length - 1]);
    }

    internal static string Pointer(string[] segments)
    {
        var pointer = "";
        foreach (var segment in segments)
        {
            pointer = Append(pointer, segment);
        }
        return pointer;
    }

    internal static string Append(string pointer, string segment) =>
        pointer + "/" + segment.Replace("~", "~0").Replace("/", "~1");

    internal static string[] FromPointer(string pointer)
    {
        var segments = pointer[1..].Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = segments[index].Replace("~1", "/").Replace("~0", "~");
        }
        return segments;
    }
}

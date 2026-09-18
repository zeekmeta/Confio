using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Confio.Internal;

internal sealed class ModelSchema
{
    private readonly JsonTypeInfo _metadata;
    private readonly Func<Type, object?> _createDefault;
    private readonly Dictionary<string, Property> _properties;
    private ModelSchema? _element;
    private ModelSchema? _nullable;

    internal JsonTypeInfo Metadata => _nullable?.Metadata ?? _metadata;
    internal IReadOnlyDictionary<string, Property> Properties => _nullable?.Properties ?? _properties;
    internal ModelSchema? Element => _nullable?.Element ?? _element;

    internal ModelSchema? Child(string name) => Metadata.Kind == JsonTypeInfoKind.Dictionary
        ? Element : Properties.TryGetValue(name, out var property) && !property.IsProtected ? property.Child : null;

    private ModelSchema(JsonTypeInfo metadata, Func<Type, object?> createDefault)
    {
        _metadata = metadata;
        _properties = new Dictionary<string, Property>(metadata.Options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _createDefault = createDefault;
    }

    internal static ModelSchema Create(JsonSerializerContext context, JsonTypeInfo root,
        ProtectedMember[] protectedMembers, Func<Type, object?> createDefault)
    {
        var options = context.Options;
        if (options.DefaultIgnoreCondition != JsonIgnoreCondition.Never ||
            options.PreferredObjectCreationHandling != JsonObjectCreationHandling.Replace ||
            options.IgnoreReadOnlyFields || options.IgnoreReadOnlyProperties)
        {
            throw new NotSupportedException("Configuration metadata must retain values and use replacement semantics.");
        }

        var schemas = new Dictionary<Type, ModelSchema>();
        var remaining = new HashSet<ProtectedMember>(protectedMembers);
        if (remaining.Contains(null!))
        {
            throw new ArgumentException("Protected member declarations cannot be null.", nameof(protectedMembers));
        }
        var protectedTypes = new HashSet<Type>(protectedMembers.SelectMany(member =>
            member.ContainingTypes.Concat(new[] { member.DeclaringType })));

        ModelSchema Build(JsonTypeInfo metadata)
        {
            if (schemas.TryGetValue(metadata.Type, out var cached))
            {
                return cached;
            }
            if (metadata.Type == typeof(object) || metadata.PolymorphismOptions is not null)
            {
                throw new NotSupportedException("Configuration members require statically declared types. Use JsonNode or JsonElement for arbitrary JSON data.");
            }
            if (metadata.PreferredPropertyObjectCreationHandling == JsonObjectCreationHandling.Populate)
            {
                throw new NotSupportedException("Configuration objects must use replacement semantics.");
            }
            var nullableType = Nullable.GetUnderlyingType(metadata.Type);
            if (metadata.Kind == JsonTypeInfoKind.None && protectedTypes.Contains(metadata.Type) &&
                (nullableType is null || options.Converters.Any(converter => converter.CanConvert(metadata.Type))))
            {
                throw new NotSupportedException("A custom converter cannot hide individually protected descendants. Protect the complete converted member instead.");
            }
            var schema = new ModelSchema(metadata, createDefault);
            schemas.Add(metadata.Type, schema);
            if (nullableType is not null)
            {
                schema._nullable = Build(context.GetTypeInfo(nullableType) ??
                    throw new NotSupportedException("Nullable values require static metadata."));
            }
            else if (metadata.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in metadata.Properties)
                {
                    if (property.Get is null && property.Set is null)
                    {
                        continue;
                    }
                    if (property.ObjectCreationHandling == JsonObjectCreationHandling.Populate ||
                        property.Get is null ||
                        (property.Set is null && property.AssociatedParameter is null && !property.IsExtensionData))
                    {
                        throw new NotSupportedException("Persisted properties must support replacement.");
                    }
                    var matches = protectedMembers.Where(member => member.DeclaringType == property.DeclaringType &&
                        string.Equals(member.JsonName ?? options.PropertyNamingPolicy?.ConvertName(member.MemberName) ?? member.MemberName,
                            property.Name, StringComparison.Ordinal)).ToArray();
                    foreach (var match in matches)
                    {
                        remaining.Remove(match);
                    }
                    var isProtected = matches.Length > 0;
                    if (isProtected && property.IsExtensionData)
                    {
                        throw new NotSupportedException("Extension data cannot be protected as a single member.");
                    }
                    ModelSchema? child = null;
                    if (!isProtected && property.CustomConverter is not null && protectedTypes.Contains(property.PropertyType))
                    {
                        throw new NotSupportedException("A property converter cannot hide individually protected descendants. Protect the complete converted member instead.");
                    }
                    if (property.CustomConverter is null)
                    {
                        var childMetadata = context.GetTypeInfo(property.PropertyType) ??
                            throw new NotSupportedException("Nested configuration types require static metadata.");
                        child = Build(childMetadata);
                    }
                    if (schema._properties.ContainsKey(property.Name))
                    {
                        throw new NotSupportedException("Model property names conflict under the selected name comparison.");
                    }
                    schema._properties.Add(property.Name, new Property(property, isProtected, child));
                }
            }
            else if (metadata.ElementType is not null)
            {
                if (metadata.Kind == JsonTypeInfoKind.Dictionary && metadata.KeyType != typeof(string))
                {
                    throw new NotSupportedException("Configuration dictionaries must use string keys.");
                }
                schema._element = Build(context.GetTypeInfo(metadata.ElementType) ??
                    throw new NotSupportedException("Collection elements require static metadata."));
            }
            metadata.MakeReadOnly();
            return schema;
        }

        var result = Build(root);
        if (remaining.Count != 0)
        {
            throw new NotSupportedException("Protected members cannot be mapped to persisted metadata: " +
                string.Join(", ", remaining.Select(member => member.DeclaringType.FullName + "." + member.MemberName)) + ".");
        }
        return result;
    }

    internal JsonNode? Normalize(JsonNode? node, bool applyDefaults = false)
    {
        if (_nullable is not null)
        {
            return _nullable.Normalize(node, applyDefaults);
        }
        if (node is null)
        {
            return null;
        }
        if (_metadata.Kind == JsonTypeInfoKind.Object)
        {
            if (node is not JsonObject obj)
            {
                throw new InvalidDataException("An object configuration value was expected.");
            }
            var result = JsonData.Object();
            foreach (var item in obj)
            {
                if (_properties.TryGetValue(item.Key, out var property))
                {
                    if (result.ContainsKey(property.Name)) throw new InvalidDataException("Configuration properties conflict under the selected name comparison.");
                    result.Add(property.Name, property.Child?.Normalize(item.Value, applyDefaults) ?? item.Value?.DeepClone());
                }
                else
                {
                    result.Add(item.Key, item.Value?.DeepClone());
                }
            }
            if (applyDefaults)
            {
                JsonObject? defaults = null;
                foreach (var property in _metadata.Properties)
                {
                    if (property.AssociatedParameter?.IsMemberInitializer != true || property.IsRequired ||
                        result.ContainsKey(property.Name))
                    {
                        continue;
                    }
                    // 原生生成器把 init 成员作为构造参数，缺失时会覆盖属性初始化值。
                    // 仅补齐缺失成员；显式嵌套对象仍使用其自身默认值，不叠加父对象的赋值。
                    defaults ??= JsonData.Serialize(_createDefault(_metadata.Type) ??
                        throw new NotSupportedException("Missing init-only values require a statically declared default constructor."), _metadata)!.AsObject();
                    result.Add(property.Name, defaults[property.Name]?.DeepClone());
                }
            }
            return result;
        }
        if (_metadata.Kind == JsonTypeInfoKind.Dictionary && node is JsonObject dictionary)
        {
            var result = JsonData.Object();
            foreach (var item in dictionary)
            {
                result.Add(item.Key, _element?.Normalize(item.Value, applyDefaults) ?? item.Value?.DeepClone());
            }
            return result;
        }
        if (_metadata.Kind == JsonTypeInfoKind.Enumerable && node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(_element?.Normalize(item, applyDefaults) ?? item?.DeepClone());
            }
            return result;
        }
        return node.DeepClone();
    }

    internal void CollectProtected(JsonNode? node, string pointer, List<NodeSlot> slots) =>
        CollectProtected(node, pointer, pointer, slots);

    private void CollectProtected(JsonNode? node, string pointer, string protectionPointer, List<NodeSlot> slots)
    {
        if (_nullable is not null)
        {
            _nullable.CollectProtected(node, pointer, protectionPointer, slots);
            return;
        }
        if (node is JsonObject obj)
        {
            foreach (var item in obj)
            {
                var knownProperty = _properties.TryGetValue(item.Key, out var property);
                var path = ConfigurationPath.Append(pointer, item.Key);
                // 原文位置用于回写；认证用途使用模型名称，字典键仍保留原始大小写。
                var purpose = ConfigurationPath.Append(protectionPointer, knownProperty ? property!.Name : item.Key);
                if (knownProperty)
                {
                    if (property!.IsProtected && JsonData.NeedsProtection(item.Value))
                    {
                        slots.Add(new NodeSlot(obj, item.Key, path, purpose));
                    }
                    else if (!property.IsProtected)
                    {
                        property.Child?.CollectProtected(item.Value, path, purpose, slots);
                    }
                }
                else if (_metadata.Kind == JsonTypeInfoKind.Dictionary)
                {
                    _element?.CollectProtected(item.Value, path, purpose, slots);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var segment = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _element?.CollectProtected(array[index], ConfigurationPath.Append(pointer, segment),
                    ConfigurationPath.Append(protectionPointer, segment), slots);
            }
        }
    }

    internal sealed class Property
    {
        internal Property(JsonPropertyInfo metadata, bool isProtected, ModelSchema? child)
        {
            Metadata = metadata;
            IsProtected = isProtected;
            Child = child;
        }

        internal string Name => Metadata.Name;
        internal JsonPropertyInfo Metadata { get; }
        internal bool IsProtected { get; }
        internal ModelSchema? Child { get; }
    }
}

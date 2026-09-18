using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Confio.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Confio;

/// <summary>
/// 将配置节与静态 JSON 元数据关联，供生成上下文和等价手写声明使用。
/// </summary>
public abstract class SettingsDeclaration
{
    internal SettingsDeclaration(string sectionPath, JsonTypeInfo metadata, ModelSchema schema)
    {
        SectionPath = sectionPath;
        Segments = ConfigurationPath.Parse(sectionPath);
        ModelType = metadata.Type;
        Schema = schema;
    }

    /// <summary>
    /// 获取该模型拥有的配置节路径。
    /// </summary>
    public string SectionPath { get; }

    /// <summary>
    /// 获取声明的模型类型。
    /// </summary>
    public Type ModelType { get; }

    internal string[] Segments { get; }
    internal ModelSchema Schema { get; }

    /// <summary>
    /// 从显式 JSON 上下文创建声明；必须提供可读写的静态对象元数据与默认对象工厂。
    /// </summary>
    /// <param name="context">提供模型图静态元数据的 JSON 上下文。</param>
    /// <param name="sectionPath">该模型拥有的配置节路径。</param>
    /// <param name="createDefault">为模型及其可默认构造的嵌套类型创建新对象；其他类型返回 null。生成器自动提供静态构造接线。</param>
    /// <param name="protectedMembers">静态保护声明。</param>
    public static SettingsDeclaration Create<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        JsonSerializerContext context, string sectionPath, Func<Type, object?> createDefault,
        params ProtectedMember[] protectedMembers)
        where T : class
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        if (protectedMembers is null)
        {
            throw new ArgumentNullException(nameof(protectedMembers));
        }
        if (createDefault is null) throw new ArgumentNullException(nameof(createDefault));
        ConfigurationPath.Parse(sectionPath);
        if (context.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> metadata ||
            metadata.Kind != JsonTypeInfoKind.Object)
        {
            throw new NotSupportedException("A configuration model requires static object metadata.");
        }
        var schema = ModelSchema.Create(context, metadata, protectedMembers, createDefault);
        return new TypedDeclaration<T>(sectionPath, metadata, schema, createDefault);
    }

    internal abstract object Read(JsonNode node);
    internal abstract JsonNode Serialize(object value);
    internal abstract JsonNode Complete(JsonNode? node, bool exists);
    internal abstract void Register(IServiceCollection services, Func<IServiceProvider, ConfigurationFile> resolve);
    internal abstract void RegisterOptions(IServiceCollection services, Func<IServiceProvider, ConfigurationFile> resolve);

    private sealed class TypedDeclaration<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>
        : SettingsDeclaration where T : class
    {
        private readonly JsonTypeInfo<T> _metadata;
        private readonly Func<Type, object?> _createDefault;

        internal TypedDeclaration(string path, JsonTypeInfo<T> metadata, ModelSchema schema, Func<Type, object?> createDefault)
            : base(path, metadata, schema)
        {
            _metadata = metadata;
            _createDefault = createDefault;
        }

        internal override object Read(JsonNode node)
        {
            try
            {
                return node.Deserialize(_metadata) ??
                    throw new InvalidOperationException("A configuration section cannot be null.");
            }
            catch (Exception exception) when (JsonData.IsConversionFailure(exception))
            {
                throw JsonData.InvalidValue(SectionPath, exception);
            }
        }

        internal override JsonNode Serialize(object value)
        {
            try
            {
                var node = JsonData.Serialize(value, _metadata) ??
                    throw new InvalidOperationException("A configuration section cannot be null.");
                JsonData.Validate(node);
                return Schema.Normalize(node)!;
            }
            catch (Exception exception) when (JsonData.IsConversionFailure(exception))
            {
                throw JsonData.InvalidValue(SectionPath, exception);
            }
        }

        internal override JsonNode Complete(JsonNode? node, bool exists)
        {
            if (exists && node is null)
            {
                throw JsonData.InvalidValue(SectionPath);
            }
            var model = exists ? (T)Read(Schema.Normalize(node, applyDefaults: true)!) : CreateDefault();
            if (model is IValidatableSettings validatable)
            {
                validatable.Validate();
            }
            return Serialize(model);
        }

        private T CreateDefault() => _createDefault(typeof(T)) as T ??
            throw new InvalidOperationException("The default factory must return a new instance of the declared model.");

        internal override void Register(IServiceCollection services, Func<IServiceProvider, ConfigurationFile> resolve)
        {
            services.TryAddSingleton<ISettings<T>>(provider =>
                new Settings<T>(resolve(provider)));
        }

        internal override void RegisterOptions(IServiceCollection services, Func<IServiceProvider, ConfigurationFile> resolve)
        {
            services.TryAddTransient<IOptionsFactory<T>>(provider => new SettingsOptionsFactory<T>(
                resolve(provider), CreateDefault,
                provider.GetServices<IConfigureOptions<T>>(), provider.GetServices<IPostConfigureOptions<T>>(),
                provider.GetServices<IValidateOptions<T>>()));
            services.AddSingleton<IOptionsChangeTokenSource<T>>(provider =>
                new SettingsChangeTokenSource<T>(resolve(provider)));
        }
    }
}

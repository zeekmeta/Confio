using System;
using System.Collections.Generic;
using System.Linq;
using Confio.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Confio;

/// <summary>
/// 注册文件与模型服务；首次业务操作加载，不进行文件或密钥 I/O。
/// </summary>
public static class ConfigurationFileServiceCollectionExtensions
{
    /// <summary>
    /// 注册由容器创建和释放的单例文件，不同文件可以注册不同模型。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services,
        ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null) =>
        AddConfigurationFile(services, new[] { context ?? throw new ArgumentNullException(nameof(context)) }, path, options);

    /// <summary>
    /// 为同一文件合并多个静态上下文；设置在注册时固定，显式 ISettings 注册保持不变。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services,
        IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null)
    {
        var definition = new FileDefinition(contexts, path, options);
        ValidateRegistration(services, definition);
        services.AddKeyedSingleton<ConfigurationFile>(definition, (_, _) => new ConfigurationFile(definition));
        ConfigurationFile Resolve(IServiceProvider provider) => provider.GetRequiredKeyedService<ConfigurationFile>(definition);
        services.AddSingleton(Resolve);
        Register(services, definition, Resolve, native: false);
        return services;
    }

    /// <summary>
    /// 借用已有文件；容器不释放该实例，调用方在全部操作完成后释放。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services, ConfigurationFile file)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        ValidateRegistration(services, file.Definition);
        services.AddSingleton(file);
        Register(services, file.Definition, _ => file, native: false);
        return services;
    }

    internal static void ValidateRegistration(IServiceCollection services, FileDefinition definition)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (services.Where(item => item.ServiceType == typeof(FileDefinition)).Select(item => (FileDefinition)item.ImplementationInstance!)
            .Any(existing => existing.Declarations.Keys.Intersect(definition.Declarations.Keys).Any()))
            throw new InvalidOperationException("A settings model is already assigned to a configuration file. Use explicit file instances for multiple files of the same model.");
    }

    internal static void Register(IServiceCollection services, FileDefinition definition,
        Func<IServiceProvider, ConfigurationFile> resolve, bool native)
    {
        services.AddSingleton(definition);
        if (native) services.AddOptions();
        foreach (var declaration in definition.Declarations.Values)
        {
            declaration.Register(services, resolve);
            if (native) declaration.RegisterOptions(services, resolve);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Confio;

/// <summary>
/// 预加载文件并接入原生 Configuration、Options 和类型服务；配置根负责释放自建文件。
/// ConfigurationManager 由调用方或 Host 按原生生命周期释放。
/// </summary>
public static class ConfigurationFileConfigurationExtensions
{
    /// <summary>
    /// 同步加载单个静态上下文，保留其他配置源及原生优先级。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services, IConfigurationManager configuration,
        ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null) =>
        AddConfigurationFile(services, configuration, new[] { context ?? throw new ArgumentNullException(nameof(context)) }, path, options);

    /// <summary>
    /// 为同一文件接入多个上下文，加载失败时清理尚未交接的自建文件。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services, IConfigurationManager configuration,
        IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null) =>
        Attach(services, configuration, new ConfigurationFile(contexts, path, options), ownsFile: true);

    /// <summary>
    /// 接入已有文件，复用成功快照；文件始终由调用方释放。
    /// </summary>
    public static IServiceCollection AddConfigurationFile(this IServiceCollection services, IConfigurationManager configuration,
        ConfigurationFile file) => Attach(services, configuration, file, ownsFile: false);

    /// <summary>
    /// 异步加载单个静态上下文，保留其他配置源及原生优先级。
    /// </summary>
    public static Task<IServiceCollection> AddConfigurationFileAsync(this IServiceCollection services, IConfigurationManager configuration,
        ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null, CancellationToken cancellationToken = default) =>
        AddConfigurationFileAsync(services, configuration, new[] { context ?? throw new ArgumentNullException(nameof(context)) }, path, options, cancellationToken);

    /// <summary>
    /// 为同一文件接入多个上下文，加载失败时清理尚未交接的自建文件。
    /// </summary>
    public static Task<IServiceCollection> AddConfigurationFileAsync(this IServiceCollection services, IConfigurationManager configuration,
        IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null, CancellationToken cancellationToken = default) =>
        AttachAsync(services, configuration, new ConfigurationFile(contexts, path, options), ownsFile: true, cancellationToken);

    /// <summary>
    /// 接入已有文件，复用成功快照；文件始终由调用方释放。
    /// </summary>
    public static Task<IServiceCollection> AddConfigurationFileAsync(this IServiceCollection services, IConfigurationManager configuration,
        ConfigurationFile file, CancellationToken cancellationToken = default) => AttachAsync(services, configuration, file, ownsFile: false, cancellationToken);

    private static IServiceCollection Attach(IServiceCollection services, IConfigurationManager configuration,
        ConfigurationFile file, bool ownsFile)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        try
        {
            Validate(services, configuration, file);
            file.EnableConfiguration();
            Register(services, configuration, file, ownsFile);
            return services;
        }
        catch
        {
            if (ownsFile) file.Dispose();
            throw;
        }
    }

    private static async Task<IServiceCollection> AttachAsync(IServiceCollection services, IConfigurationManager configuration,
        ConfigurationFile file, bool ownsFile, CancellationToken cancellationToken)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        try
        {
            Validate(services, configuration, file);
            await file.EnableConfigurationAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Register(services, configuration, file, ownsFile);
            return services;
        }
        catch
        {
            if (ownsFile) file.Dispose();
            throw;
        }
    }

    private static void Validate(IServiceCollection services, IConfigurationManager configuration, ConfigurationFile file)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        ConfigurationFileServiceCollectionExtensions.ValidateRegistration(services, file.Definition);
    }

    private static void Register(IServiceCollection services, IConfigurationManager configuration, ConfigurationFile file, bool ownsFile)
    {
        ((IConfigurationBuilder)configuration).Add(new ConfigurationFileSource(file, ownsFile));
        services.AddSingleton(file);
        services.TryAddSingleton<IConfiguration>(configuration);
        ConfigurationFileServiceCollectionExtensions.Register(services, file.Definition, _ => file, native: true);
    }
}

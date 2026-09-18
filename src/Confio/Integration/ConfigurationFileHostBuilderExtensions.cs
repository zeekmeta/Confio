using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Confio;

/// <summary>
/// 在原生 Host 构建前接入配置；预加载完成后继续调用原生 Build，不接管整个容器构建。
/// </summary>
public static class ConfigurationFileHostBuilderExtensions
{
    /// <summary>
    /// 同步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static IHostApplicationBuilder AddConfigurationFile(this IHostApplicationBuilder builder,
        ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.Services.AddConfigurationFile(builder.Configuration, context, path, options);
        return builder;
    }

    /// <summary>
    /// 同步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static IHostApplicationBuilder AddConfigurationFile(this IHostApplicationBuilder builder,
        IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.Services.AddConfigurationFile(builder.Configuration, contexts, path, options);
        return builder;
    }

    /// <summary>
    /// 同步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static IHostApplicationBuilder AddConfigurationFile(this IHostApplicationBuilder builder,
        ConfigurationFile file)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.Services.AddConfigurationFile(builder.Configuration, file);
        return builder;
    }

    /// <summary>
    /// 异步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static async Task AddConfigurationFileAsync(this IHostApplicationBuilder builder,
        ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        await builder.Services.AddConfigurationFileAsync(builder.Configuration, context, path, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 异步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static async Task AddConfigurationFileAsync(this IHostApplicationBuilder builder,
        IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        await builder.Services.AddConfigurationFileAsync(builder.Configuration, contexts, path, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 异步加载并注册文件及类型服务；借用已有文件时仍由调用方释放。
    /// </summary>
    public static async Task AddConfigurationFileAsync(this IHostApplicationBuilder builder,
        ConfigurationFile file, CancellationToken cancellationToken = default)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        await builder.Services.AddConfigurationFileAsync(builder.Configuration, file, cancellationToken).ConfigureAwait(false);
    }

}

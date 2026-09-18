using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ConfioSample;

internal static class IntegrationExamples
{
    public static async Task<string> ReadContainerAsync(ConfigurationFile file, CancellationToken cancellationToken)
    {
        using var provider = CreateContainer(file);
        using var scope = provider.CreateScope();
        var mail = await scope.ServiceProvider.GetRequiredService<MailService>().ReadAsync(cancellationToken);
        var shared = scope.ServiceProvider.GetRequiredService<ConfigurationFile>();
        var retry = await scope.ServiceProvider.GetRequiredService<ISettings<RetrySettings>>().ReadAsync(cancellationToken);
        return "一次注册 → 业务服务注入 ISettings<MailSettings> → 读取当前配置\n\n" + $"作用域与窗口共享文件：{(ReferenceEquals(file, shared) ? "是" : "否")}\n" + $"Mail 当前配置：{mail.Host}:{mail.Port}\n" + $"Retry 类型服务已自动注册：{retry.MaxAttempts} 次 / {retry.DelaySeconds} 秒\n\n" + "本次读取已有快照，保留配置文件和未保存的表单草稿。\n" + "手工修改文件后，请回到“读写与加密”页点击“重新加载文件”。\n\n" + "普通 DI 注册不启用 Configuration / Options，也不要求 Host。\n" + "本例的根容器和业务作用域已释放；借用的文件仍由窗口管理。";
    }

    public static async Task<string> SaveContainerAsync(ConfigurationFile file, MailSettings draft, CancellationToken cancellationToken)
    {
        using var provider = CreateContainer(file);
        using var scope = provider.CreateScope();
        var notifications = 0;
        using var subscription = scope.ServiceProvider.GetRequiredService<ISettings<MailSettings>>().OnChange(_ => Interlocked.Increment(ref notifications));
        var service = scope.ServiceProvider.GetRequiredService<MailService>();
        await service.SaveAsync(draft, cancellationToken);
        var saved = await service.ReadAsync(cancellationToken);
        return "邮件表单 → 业务服务注入 ISettings<MailSettings> → 保存 → 读取结果\n\n" + $"Mail 保存结果：{saved.Host}:{saved.Port}\n" + $"类型服务直接通知：{notifications} 次\n\n" + "已将“读写与加密”页的邮件表单写入当前文件，Retry 配置保留。\n" + "模型校验和字段保护由同一运行库完成；可回到该页查看程序值和文件原文。\n\n" + "本例的根容器和业务作用域已释放；借用的文件仍由窗口管理。";
    }

    private static ServiceProvider CreateContainer(ConfigurationFile file)
    {
        var services = new ServiceCollection();
        // 独立应用通常用 services.AddConfigurationFile(AppSettingsContext.Default, path: "appsettings.json")。
        // 两个按钮借用窗口已有的文件，容器释放后由窗口继续管理它。
        services.AddConfigurationFile(file);
        services.AddTransient<MailService>();
        return services.BuildServiceProvider();
    }

    public static async Task<(OptionsObservation[] Rows, string NativeView)> RunOptionsAsync(ConfigurationFile file, ConfigurationFileOptions fileOptions, CancellationToken cancellationToken)
    {
        await file.UpdateAsync<MailSettings>(mail => mail.Port = 465, cancellationToken);
        var services = new ServiceCollection();
        using var configurationRoot = new ConfigurationManager();
        await services.AddConfigurationFileAsync(configurationRoot, file, cancellationToken: cancellationToken);
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettings<MailSettings>>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var cached = provider.GetRequiredService<IOptions<MailSettings>>().Value;
        using var originalScope = provider.CreateScope();
        var scoped = originalScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>().Value;
        var monitor = provider.GetRequiredService<IOptionsMonitor<MailSettings>>();
        var notifications = 0;
        using var subscription = monitor.OnChange((_, _) => Interlocked.Increment(ref notifications));
        OptionsObservation Observe(string stage)
        {
            using var newScope = provider.CreateScope();
            var fresh = newScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MailSettings>>().Value;
            return new OptionsObservation(stage, configuration["Mail:Port"], cached.Port, scoped.Port, fresh.Port, monitor.CurrentValue.Port, Volatile.Read(ref notifications));
        }

        List<OptionsObservation> rows = [Observe("初始值 465")];
        await settings.UpdateAsync(mail => mail.Port = 587, cancellationToken);
        rows.Add(Observe("保存为 587"));
        using (var external = new ConfigurationFile(AppSettingsContext.Default, file.Path, options: fileOptions))
        {
            await external.UpdateAsync<MailSettings>(mail => mail.Port = 2525, cancellationToken);
        }

        rows.Add(Observe("外部写入 2525"));
        cancellationToken.ThrowIfCancellationRequested();
        ((IConfigurationRoot)configuration).Reload();
        rows.Add(Observe("原生 Reload"));
        // IConfiguration 是已解密的读取投影，展示时按模型的保护路径隐藏敏感值。
        var nativeView = string.Join("\n", configuration.AsEnumerable().Where(pair => pair.Value is not null || !configuration.GetSection(pair.Key).GetChildren().Any()).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            var secret = pair.Key.Equals("Mail:Password", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("Mail:Credentials:", StringComparison.OrdinalIgnoreCase);
            var value = pair.Value is null ? "null" : pair.Value.Length == 0 ? "\"\"" : secret ? "•••（已还原）" : pair.Value.Replace("\r", "\\r").Replace("\n", "\\n");
            return $"{pair.Key} = {value}";
        }));
        return (rows.ToArray(), nativeView);
    }

    public static async Task<string> RunHostAsync(ConfigurationFile file, CancellationToken cancellationToken)
    {
        var output = new StringBuilder("加载配置 → 构建 Host → 启动服务 → 正常停止\n\n");
        var builder = Host.CreateEmptyApplicationBuilder(null);
        await builder.AddConfigurationFileAsync(file, cancellationToken: cancellationToken);
        builder.Services.AddOptions<MailSettings>().ValidateOnStart();
        builder.Services.AddHostedService(services => new MailWorker(
            services.GetRequiredService<IOptionsMonitor<MailSettings>>(), message => output.AppendLine(message)));
        using var host = builder.Build();
        output.AppendLine("模型校验已在加载时通过，Host 已构建。");
        await host.StartAsync(cancellationToken);
        output.AppendLine("Options 已按原生启动时序创建，Host 已启动。");
        await host.StopAsync(cancellationToken);
        output.AppendLine("Host 已停止。没有发送邮件或启动后台监听。");
        return output.ToString();
    }

    private sealed class MailService(ISettings<MailSettings> settings)
    {
        public Task<MailSettings> ReadAsync(CancellationToken cancellationToken) => settings.ReadAsync(cancellationToken);
        public Task SaveAsync(MailSettings mail, CancellationToken cancellationToken) => settings.SaveAsync(mail, cancellationToken);
    }

    private sealed class MailWorker(IOptionsMonitor<MailSettings> settings, Action<string> report) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var mail = settings.CurrentValue;
            report($"托管服务读取到：{mail.Host}:{mail.Port}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            report("托管服务已停止。");
            return Task.CompletedTask;
        }
    }
}

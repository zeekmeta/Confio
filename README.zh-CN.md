# Confio

[English](https://github.com/zeekmeta/Confio/blob/main/README.md) | **简体中文**

**面向 .NET 的强类型配置文件读写库。** 用同一套模型和 API 读写 JSON、YAML、TOML 和 INI，管理默认值、校验规则与敏感字段。

[NuGet](https://www.nuget.org/packages/Confio) · [使用指南](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md) · [交互示例](https://github.com/zeekmeta/Confio/blob/main/samples/ConfioSample/README.md) · [更新日志](https://github.com/zeekmeta/Confio/blob/main/CHANGELOG.md) · [项目仓库](https://github.com/zeekmeta/Confio)

## 特性

- **直接读写**：创建 `ConfigurationFile` 即可使用，提供同步和异步 API，首次操作自动加载。
- **同一套模型，四种格式**：支持 JSON、YAML/YML、TOML 1.1 和 INI，可读写根对象或指定配置节。
- **默认值与校验**：默认值声明在模型中，支持嵌套对象、集合、`record/init` 和业务校验。
- **敏感字段保护**：`[Protected]` 标记字符串、值类型或子对象，自动加解密；内置 Windows DPAPI 和跨平台 AES-256-GCM。
- **源生成与 Native AOT**：生成器随 NuGet 包交付，JIT 与 AOT 使用同一套 API。
- **按需接入容器**：支持普通 DI、多文件模型归属，以及原生 Configuration / Options / Host。

## 支持平台

| 应用目标 | 平台 | 发布方式 |
| --- | --- | --- |
| .NET 10 LTS | Windows、Linux、macOS | JIT、Native AOT |
| .NET 8 / 9 | Windows、Linux、macOS | JIT、Native AOT |
| .NET Framework 4.6.2～4.8.1 | Windows | 托管应用 |

使用 SDK 风格项目、`PackageReference` 和 C# 12 或更高版本；仓库使用 .NET 10 SDK 构建。编译工具版本与应用运行时独立。Framework 各版本提供对应包资产，.NET 9 使用 net8.0 资产。具体工具链与实测范围见[支持矩阵](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md#21-配置组件)。

## 安装

从 [NuGet.org](https://www.nuget.org/packages/Confio) 安装：

```sh
dotnet add package Confio --version 1.0.0
```

使用默认 nuget.org 源即可，无需添加其他包源。编译要求见[安装指南](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#安装与编译要求)。体验源码可直接[运行示例](#运行示例)，或[构建本地候选包](https://github.com/zeekmeta/Confio/blob/main/docs/开发与验证.md#本地候选包)。

1.0 建立公开 API 与持久化配置格式的兼容基线，破坏性调整通过大版本发布。升级前请查看[更新日志](https://github.com/zeekmeta/Confio/blob/main/CHANGELOG.md)。

## 快速开始

下面是一个完整的 `Program.cs`。声明模型与 JSON 上下文后即可读写，源生成器会自动完成配置声明：

```csharp
using Confio;
using System.Text.Json.Serialization;

using var config = new ConfigurationFile(
    AppSettingsContext.Default, path: "settings.json");

var mail = await config.ReadAsync<MailSettings>();
mail.Host = "smtp.example.com";
mail.Port = 465;
mail.Password = "example-password";
await config.SaveAsync(mail);

[SettingsSection("Mail")]
public sealed class MailSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 587;

    [Protected]
    public string Password { get; set; } = "";
}

[JsonSerializable(typeof(MailSettings))]
public partial class AppSettingsContext : JsonSerializerContext
{
}
```

文件不存在时读取模型默认值；保存后 `Mail` 节写入文件，密码自动变成 `enc:v1:` 开头的密文。应用读取时得到解密后的原值。

需要同步操作时，使用同一实例的 `Read<MailSettings>()` 和 `Save(mail)`。省略 `path` 会使用当前用户的应用配置目录；用 `[SettingsSection]` 可以直接读写文件根对象。

## 常用操作

| 需要做什么 | API | 行为 |
| --- | --- | --- |
| 读取配置 | `Read<T>()` / `ReadAsync<T>()` | 返回最近成功加载或提交的独立副本 |
| 保存编辑结果 | `Save(model)` / `SaveAsync(model)` | 完整替换模型对应的配置节 |
| 基于最新文件修改 | `Update<T>(edit)` / `UpdateAsync<T>(edit)` | 读取磁盘最新值，执行修改并保存 |
| 恢复默认值 | `Reset<T>()` / `ResetAsync<T>()` | 删除该模型的持久化内容，恢复模型默认值 |
| 读取外部修改 | `Reload()` / `ReloadAsync()` | 重新加载文件，失败保留已有成功快照 |
| 订阅变化 | `OnChange<T>(listener)` | 成功提交或重载后通知；释放返回值可退订 |

异步操作均接受 `CancellationToken`。读取结果的修改需要显式保存；外部文件修改需要显式重载，订阅不会自动监听磁盘。

## 字段保护

默认情况下，Windows 使用当前用户 DPAPI；Linux 和 macOS 使用自动持久化密钥的 AES-256-GCM。自动密钥只生成一次，后续启动复用。

所有支持平台都可以显式选择自动 AES：

```csharp
using var config = new ConfigurationFile(
    AppSettingsContext.Default,
    path: "settings.json",
    options: new ConfigurationFileOptions
    {
        Protection = ConfigurationProtection.AesGcm
    });
```

跨机器共享配置时，使用 `EncryptionKey` 提供稳定的 32 字节密钥；需要自定义自动密钥位置时使用 `KeyFilePath`。实际路径可以通过 `config.Path` 和 `config.KeyFilePath` 查看。

加载已有明文时，受保护字段默认会加密并写回；设置 `ProtectPlaintextOnLoad = false` 可关闭加载回写。密钥丢失或认证失败会报错并保留配置。密钥存储、跨机器使用及文件前缀规则见[字段保护指南](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#字段保护)。

## 文件格式与行为边界

通过扩展名选择格式，也可用 `ConfigurationFileOptions.Format` 显式指定 `.conf` 或无扩展名文件。

| 格式 | 扩展名 | 主要边界 |
| --- | --- | --- |
| JSON | `.json` | 支持 `null`，允许注释与尾逗号输入 |
| YAML | `.yaml`、`.yml` | YAML 1.2 Core，字符串键映射 |
| TOML | `.toml` | TOML 1.1，没有原生 `null` |
| INI | `.ini` | 使用 `:` 层级和连续数组索引，不支持 `null`、空容器及多行值 |

保存会保留其他配置节的数据，但不承诺保留原注释、排版或目标节的未知属性。无法表达的值会报告具体路径与原因，详情见[格式指南](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#文件格式)。

## 运行示例

[ConfioSample](https://github.com/zeekmeta/Confio/blob/main/samples/ConfioSample/README.md) 是 Avalonia 桌面示例，并排展示“正在编辑”“程序读到”“磁盘保存”的内容，可以直接体验保存、重载、加密、Save / Update 对照及容器接入。

从仓库根目录运行：

```sh
dotnet run --project samples/ConfioSample/ConfioSample.csproj -c Release
```

## 构建与验证

```sh
dotnet build Confio.sln -c Release
dotnet test tests/ConfioTests/ConfioTests.csproj -c Release --no-build
```

完整消费者、Native AOT、隔离 NuGet、WSL 及发布命令见[开发与验证](https://github.com/zeekmeta/Confio/blob/main/docs/开发与验证.md)。各平台的验收状态单独维护在[验收记录](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md#81-独立交付边界与当前状态)。

## 参与贡献

通过[项目仓库](https://github.com/zeekmeta/Confio)提交问题或改进建议。问题报告请包含包版本、操作系统、目标框架、最小模型与复现步骤，并移除真实密码和密钥。

代码修改遵循[开发规范](https://github.com/zeekmeta/Confio/blob/main/开发规范.md)；行为变更带上对应验证。产品契约与技术取舍见[设计方案](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md)。

## 许可证

Confio 采用 [MIT 许可证](https://github.com/zeekmeta/Confio/blob/main/LICENSE)。允许商业使用、修改和分发，分发时须保留版权声明与许可证文本。

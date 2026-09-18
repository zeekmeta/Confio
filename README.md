# Confio

**English** | [简体中文](https://github.com/zeekmeta/Confio/blob/main/README.zh-CN.md)

**A strongly typed configuration file library for .NET.** Read and write JSON, YAML, TOML, and INI with the same models and APIs, including model defaults, validation, and protection for sensitive fields.

[NuGet](https://www.nuget.org/packages/Confio) · [User guide](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md) · [Interactive sample](https://github.com/zeekmeta/Confio/blob/main/samples/ConfioSample/README.md) · [Changelog](https://github.com/zeekmeta/Confio/blob/main/CHANGELOG.md) · [Repository](https://github.com/zeekmeta/Confio)

Detailed guides and the sample UI are currently in Chinese.

## Features

- **Direct file access**: create a `ConfigurationFile` and start reading or writing. Both synchronous and asynchronous APIs are available, with automatic loading on the first operation.
- **One set of models, four formats**: JSON, YAML/YML, TOML 1.1, and INI, with support for a root object or named configuration sections.
- **Defaults and validation**: declare defaults in your models, with support for nested objects, collections, `record/init`, and business validation rules.
- **Sensitive field protection**: mark strings, value types, or nested objects with `[Protected]` for automatic encryption and decryption. Windows DPAPI and cross-platform AES-256-GCM are built in.
- **Source generation and Native AOT**: the source generator ships in the NuGet package. JIT and AOT use the same APIs.
- **Optional container integration**: use ordinary DI, associate models with separate files, or integrate with native Configuration / Options / Host APIs.

## Supported platforms

| Application target | Platforms | Deployment |
| --- | --- | --- |
| .NET 10 LTS | Windows, Linux, macOS | JIT, Native AOT |
| .NET 8 / 9 | Windows, Linux, macOS | JIT, Native AOT |
| .NET Framework 4.6.2–4.8.1 | Windows | Managed applications |

Use an SDK-style project, `PackageReference`, and C# 12 or later. Building this repository requires the .NET 10 SDK. The compiler toolchain and the application runtime are separate requirements. Each supported Framework version has a matching package asset; .NET 9 uses the net8.0 asset. See the [support matrix](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md#21-配置组件) for toolchain requirements and verified environments.

## Installation

Install from [NuGet.org](https://www.nuget.org/packages/Confio):

```sh
dotnet add package Confio --version 1.0.0
```

The default nuget.org feed is sufficient; no additional package source is required. See the [installation guide](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#安装与编译要求) for compiler requirements. To try the source, [run the sample](#run-the-sample) or [build a local candidate package](https://github.com/zeekmeta/Confio/blob/main/docs/开发与验证.md#本地候选包).

Version 1.0 establishes the public API and persisted configuration format baseline. Breaking changes require a major version increment. Read the [changelog](https://github.com/zeekmeta/Confio/blob/main/CHANGELOG.md) before upgrading.

## Quick start

The following is a complete `Program.cs`. Declare your model and JSON context; the source generator supplies the configuration declarations:

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

If the file does not exist, reading returns the model defaults. Saving writes the `Mail` section to the file and encrypts the password as a value starting with `enc:v1:`. Reading through the API returns the decrypted value.

For synchronous operations, use `Read<MailSettings>()` and `Save(mail)` on the same instance. Omitting `path` selects the current user's application configuration directory. Use `[SettingsSection]` without a name to read and write the file's root object.

## Common operations

| Task | API | Behavior |
| --- | --- | --- |
| Read configuration | `Read<T>()` / `ReadAsync<T>()` | Return an independent copy of the most recently loaded or committed configuration |
| Save edits | `Save(model)` / `SaveAsync(model)` | Replace the entire section associated with the model |
| Update the latest file contents | `Update<T>(edit)` / `UpdateAsync<T>(edit)` | Read the latest values from disk, apply the edit, and save |
| Restore defaults | `Reset<T>()` / `ResetAsync<T>()` | Remove the model's persisted content and restore its declared defaults |
| Read external changes | `Reload()` / `ReloadAsync()` | Reload the file; retain the last successful snapshot if loading fails |
| Subscribe to changes | `OnChange<T>(listener)` | Notify after a successful commit or reload; dispose the returned subscription to unsubscribe |

All asynchronous operations accept a `CancellationToken`. Changes to a returned model must be saved explicitly. External file edits require an explicit reload; subscriptions do not watch the file system.

## Field protection

By default, Windows uses DPAPI for the current user. Linux and macOS use AES-256-GCM with an automatically persisted key. The key is generated once and reused across application launches.

You can explicitly select AES with an automatically managed key on every supported platform:

```csharp
using var config = new ConfigurationFile(
    AppSettingsContext.Default,
    path: "settings.json",
    options: new ConfigurationFileOptions
    {
        Protection = ConfigurationProtection.AesGcm
    });
```

To share encrypted configuration across machines, supply the same stable 32-byte key through `EncryptionKey`. Use `KeyFilePath` to customize the location of an automatically managed key. Inspect `config.Path` and `config.KeyFilePath` to see the resolved paths.

When loading existing plaintext, protected fields are encrypted and written back by default. Set `ProtectPlaintextOnLoad = false` to disable this write-back. Missing keys or authentication failures produce an error and leave the configuration intact. See the [field protection guide](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#字段保护) for key storage, cross-machine use, and file prefix rules.

## File formats and behavior

The file extension selects the format. Use `ConfigurationFileOptions.Format` to specify it explicitly for `.conf` files or files without an extension.

| Format | Extensions | Main constraints |
| --- | --- | --- |
| JSON | `.json` | Supports `null`; accepts comments and trailing commas on input |
| YAML | `.yaml`, `.yml` | YAML 1.2 Core with string-keyed mappings |
| TOML | `.toml` | TOML 1.1; no native `null` |
| INI | `.ini` | Uses `:` for hierarchy and consecutive array indices; no `null`, empty containers, or multiline values |

Saving preserves data in other sections, but does not guarantee preservation of comments, formatting, or unknown properties in the section being saved. Values that a format cannot represent produce an error with the affected path and reason. See the [format guide](https://github.com/zeekmeta/Confio/blob/main/docs/使用指南.md#文件格式) for details.

## Run the sample

[ConfioSample](https://github.com/zeekmeta/Confio/blob/main/samples/ConfioSample/README.md) is an Avalonia desktop application. It shows draft values, values read by the application, and values saved on disk side by side. Try saving, reloading, encryption, Save / Update comparisons, and container integration.

Run from the repository root:

```sh
dotnet run --project samples/ConfioSample/ConfioSample.csproj -c Release
```

## Build and verify

```sh
dotnet build Confio.sln -c Release
dotnet test tests/ConfioTests/ConfioTests.csproj -c Release --no-build
```

See [development and verification](https://github.com/zeekmeta/Confio/blob/main/docs/开发与验证.md) for consumer, Native AOT, isolated NuGet, WSL, and publishing commands. Platform verification status is maintained in the [verification records](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md#81-独立交付边界与当前状态).

## Contributing

Report issues or suggest improvements through the [repository](https://github.com/zeekmeta/Confio). Include the package version, operating system, target framework, a minimal model, and steps to reproduce. Remove real passwords and keys from reports.

Follow the [development guidelines](https://github.com/zeekmeta/Confio/blob/main/开发规范.md) and include verification for behavior changes. Product contracts and technical decisions are documented in the [design](https://github.com/zeekmeta/Confio/blob/main/docs/设计方案.md).

## License

Confio is licensed under the [MIT License](https://github.com/zeekmeta/Confio/blob/main/LICENSE). Commercial use, modification, and distribution are permitted. Distributions must retain the copyright notice and license text.

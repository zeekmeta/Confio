# 更新日志

本文件记录影响使用者的功能和行为变化。1.0 建立公开 API 与持久化配置格式的兼容基线，破坏性调整通过大版本发布。

## 1.1.0（2026-09-18）

- 新增 `ConfigurationFileOptions.Encoding`，默认 UTF-8 无 BOM，支持显式 Unicode 编码、代码页及 `Encoding.Default`；读取识别 Unicode BOM，保存和自动保护写回使用所选编码。
- 编码失败保留文件和成功快照，拒绝有损字符替换；YAML 支持 UTF-8/16/32，TOML 遵守 UTF-8 要求。
- Sample 增加编码选择及保存编码、BOM 显示，文件预览和手写明文演示使用相同设置。
- 修复 Windows 上多个配置文件并发首次使用同一自动 AES 密钥时，读取与原子重命名之间的文件共享冲突。

## 1.0.0（2026-09-18）

### 新增

- 首个正式版本，支持 JSON、YAML/YML、TOML 1.1 和 INI 的同步异步读写，以及模型默认值、校验、更新、重置和变化订阅。
- 一个 NuGet 包交付六个 Framework 资产、`net8.0` / `net10.0` 资产和源生成器；.NET 8 / 9 / 10 支持 Native AOT。
- 直接创建、普通 DI 及原生 Configuration / Options / Host 接入共享同一配置运行库。
- 统一的 `ConfigurationFileOptions`，集中设置文件格式、保护方式、密钥来源和加载明文时的写回行为。
- 自动生成、保存并复用 AES-256-GCM 密钥；可通过 `KeyFilePath` 指定位置，或用 `EncryptionKey` 提供自己的密钥。
- `[SettingsSection]` 根模型与显式文件格式，支持 `.conf` 和无扩展名文件。
- 同一容器中将不同模型注册到不同配置文件；类型服务自动定位所属文件。
- 受保护值类型，以及遵循上下文的属性匹配和属性级 `JsonIgnore` 条件。
- Sample 打开已有配置、显示密钥位置，并隔离会自动修改文件的教学操作。
- 采用 MIT 许可证，NuGet 包包含许可证文本与标准许可元数据。
- GitHub Actions 完成 Windows / Linux 包验收，版本标签验证通过后通过受信发布上传 nuget.org。

### 相对内部预览版的变更

- **破坏性变更**：移除 `ApplicationId`，文件路径与自动密钥路径使用默认值或显式选项。
- **破坏性变更**：Windows 默认使用当前用户 DPAPI；Linux/macOS 默认使用自动文件密钥的 AES-GCM，移除内置 Keychain / Secret Service 接入。特殊保护设施可通过 `IConfigurationProtector` 接入。
- **破坏性变更**：密文认证用途绑定逻辑字段路径，不再包含应用名。此前开发版的密文不提供兼容解密入口；修改后的保护方式与认证用途需要匹配的密钥和重新保存的配置。
- **破坏性变更**：原生集成改用 `AddConfigurationFile[Async]`，随后调用原生 `Build` / `BuildServiceProvider`；移除专用构建包装。
- JSON、YAML 和 TOML 保留节名及字典键大小写；INI 遵循其不区分大小写的键规则。
- 运行库引用 Hosting 抽象，完整 Host 依赖由实际使用 Host 的应用引用。
- 提供默认英文、可切换中文的 README，完善 NuGet 包说明、快速开始、使用指南和开发文档。

### 修复

- 属性忽略大小写匹配时，自动保护替换文件中的原始键，避免遗留明文。
- 字典中的 `A` 与 `a` 在区分大小写的格式中可同时往返，不受模型属性匹配设置影响。

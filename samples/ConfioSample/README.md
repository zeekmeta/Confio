# ConfioSample

[ConfioSample](../../samples/ConfioSample/MainWindow.axaml) 是 Avalonia 配置读写示例。首页提供四步引导，解释预期变化和对应 API；保存、恢复默认、读取和重新加载等常用按钮始终可见，无需先完成或收起引导。下方并排展示“你正在编辑、程序实际读到、磁盘实际保存”：程序值先用服务器、端口和密码状态展示，完整模型 JSON 按需展开。

## 运行

以下命令均从仓库根目录执行：

```powershell
dotnet run --project samples/ConfioSample/ConfioSample.csproj -c Release
```

## 四步入门

先依次点击引导中的四个按钮，无需准备配置文件：

1. **填入示例值**：左边端口变为 `465`，中间仍为默认值 `587`，右边说明文件尚未创建。
2. **保存邮件配置**：中间采用 `465`，右边出现真实文件，非空密码和整个凭据对象自动加密。保存失败会停在这一步，并保留输入和上次成功结果。
3. **模拟外部修改**：另一个配置实例将文件端口改为 `2525`，当前程序仍读到 `465`。
4. **重新加载文件**：程序和表单采用 `2525`。点击“继续自由探索”可收起引导。

每一步均可通过“查看模型与用法源码”查看实际模型声明。保存或“读取到表单”后，勾选密码旁的“显示”可核对解密后的原值；完整模型与操作记录默认隐藏敏感内容。可以随时收起引导，切到“重试”或“加密与失败演示”也会收起引导。“异步 API”默认勾选，取消勾选后操作和引导代码一起切换到同步入口。

## 功能演示

完成引导后可按以下场景继续体验；容器功能放在独立的“进阶：容器接入”页。

| 界面操作 | 可观察的行为 |
| --- | --- |
| 顶部：打开配置 | 打开已有 Sample 模型的配置文件，先加载观察，显式保存时再保护；支持继续编辑、保存和重载。自动改值、Options 对照和主动损坏等教学动作需要新建演示文件 |
| 顶部：打开目录 | 用系统文件管理器打开当前配置文件所在目录；尚未保存时只创建空目录，不生成配置文件，也不改变表单草稿 |
| 邮件：填入示例、保存 | 编辑不会改变程序已读取的值或文件；展开“集合与多行文本”可编辑收件人和多行备注 |
| Save / Update 对照：新建文件并运行对照 | 两份草稿都只改端口，另一个实例修改文件中的主机；重新打开文件后，Save 读到草稿中的旧主机，Update 读到外部修改后的主机。使用两个独立文件，保留当前文件与草稿，可打开对照目录查看原文；同步和异步均可运行 |
| 密码与 API 凭据 | `Password` 单字段保护，`Credentials` 子对象整体保护；快照保留模型结构并隐藏敏感内容，文件展示真实密文 |
| 重试：保存重试配置、次数 +1、恢复重试默认值 | Retry 使用 `record/init`；通过 `with` 基于最新文件值更新，保存或重置一个配置节保留另一个节及其表单草稿 |
| 加密与失败演示：正常保存空字符串、enc:v1:plain | 四种格式均可保存空字符串，它与 null 不同；前缀密码通过模型 API 保存后加密并完整还原 |
| 加密与失败演示：null | JSON / YAML 标为“正常保存”；TOML / INI 单列为“预期失败”，点击“验证 null 保存失败”后显示 `/Mail/Password` 与不支持 null 的原因，保留原文件、旧配置、草稿和通知状态 |
| 格式边界：空收件人、多行备注 | 当前 INI 方言拒绝空集合与多行值，错误分别定位到 `/Mail/Recipients`、`/Mail/Notes`，保留文件和编辑草稿；JSON / YAML / TOML 支持这两种值。顶部始终显示当前格式的限制 |
| 加密与失败演示：新建手写明文文件 → 加载文件，观察自动加密 | 两个相邻按钮先展示虚构明文，再用 Reload 自动加密写回，无需 Save；再次重载不重写有效密文，之前的演示文件保留 |
| 加密与失败演示：验证空白主机保存失败 | 标为“预期失败”，调用真实保存并由模型拒绝；文件、快照与通知次数保留，日志记录失败 |
| 刷新观察、读取到表单、重新加载文件 | 刷新观察保留草稿且不重载；读取到表单用已有快照替换草稿；重新加载从文件更新快照 |
| 加密与失败演示：写入损坏内容、还原演示文件 | 重载损坏文件失败后仍可读旧快照；仅撤销这次演示制造的损坏，不代表运行库提供备份恢复 |
| 普通 DI：读取当前配置、保存邮件表单 | 业务服务注入 `ISettings<T>`，与窗口共享文件实例。读取已有快照，保留文件及未保存草稿；保存使用“读写与加密”页的邮件表单，执行校验、保护并展示变更通知。外部文件修改仍需显式重新加载 |
| Options 对照与原生读取视图 | 用实际取值表格比较 Configuration、IOptions、原作用域与新作用域的 Snapshot、Monitor 及通知次数；下方展示已解密并脱敏的扁平读取投影 |
| Host 启停 | 使用空 Host 构建器，加载时执行模型校验，按原生启动时序创建 Options，显示托管服务的启动、读取与停止结果 |
| 用法源码、操作记录 | 查看实际参与编译的模型、读写事件与容器代码；查看最近 100 条操作和直接通知，包含顺序、时间与安全的失败提示 |

两个配置节模型使用同一个 [AppSettingsContext](../../samples/ConfioSample/MailSettings.cs)，[ApiCredentials](../../samples/ConfioSample/ApiCredentials.cs) 作为 Mail 的受保护子对象。[窗口事件](../../samples/ConfioSample/MainWindow.Actions.cs)直接调用正式 API，[入门引导](../../samples/ConfioSample/MainWindow.Guide.cs)复用相同动作，[容器场景](../../samples/ConfioSample/IntegrationExamples.cs)按方法组织。窗口拥有文件实例，容器场景借用它并在操作结束后释放各自的根；关闭窗口会先取消并等待正在执行的操作。源码页随构建嵌入实际模型与消费代码，不维护另一份展示代码。示例不发送邮件或自动监听配置文件。UI 采用 Avalonia 12.1.2（MIT，提供 `net10.0` 资产），相关依赖仅由示例工程引用。

## 打开文件与选择保护方式

默认使用每次独立的临时 JSON 路径，读取默认值不创建文件，顶部显示配置与实际密钥位置。展开“新建演示与设置”，选择格式和保护方式后点击“新建演示”可重新开始，之前的文件保留。基础示例使用四种格式均可表达的数据；取消凭据勾选才表示 `null`。保护选择在创建或打开文件时生效，当前标签保持实际使用的选择。可通过“打开配置”或命令行路径加载已有 Sample 文件；打开时关闭明文自动回写，常规编辑和保存仍可使用，教学改写保持隔离：

```powershell
dotnet run --project samples/ConfioSample/ConfioSample.csproj -c Release -- artifacts/sample-run/appsettings.yaml
```

## Native AOT

示例启用 AOT 分析，关闭默认 JSON 反射；TOML 直接处理固定节点，不调用对象反射转换。Windows x64 可通过下列命令发布和运行 Native AOT，需要 Visual Studio 的“使用 C++ 的桌面开发”工作负载和 Windows SDK：

```powershell
dotnet publish samples/ConfioSample/ConfioSample.csproj -c Release -r win-x64 -o artifacts/sample-aot
./artifacts/sample-aot/ConfioSample.exe
```

macOS arm64 使用已安装的 Apple Command Line Tools 发布同一示例：

```sh
dotnet publish samples/ConfioSample/ConfioSample.csproj -c Release -r osx-arm64 -o artifacts/sample-aot/osx-arm64
./artifacts/sample-aot/osx-arm64/ConfioSample --verify --aes
```

示例可选择默认保护或 AES-GCM，AES 使用组件自动生成并持久化的密钥，不使用公开固定密钥。`--aes` 可直接选择 AES。正常运行使用组件默认密钥位置；`--verify` 使用验收输出目录内的专用密钥文件，不接触真实应用的密钥。

## 自动验收

同一桌面程序提供 `--verify` 验收入口，依次触发可用按钮，断言同步/异步四步引导、失败不推进、可读摘要、JSON 展开与密码显示，以及集合、格式限制、字段及子对象保护、手写明文自动保护、前缀密码通过模型 API 往返、空值区别、校验失败、草稿隔离、不可变更新、通知、损坏文件恢复、独立文件中的 Save / Update 对照、DI / Options 作用域 / Host、源码资源、取消及新会话隔离。INI 会验证多行值与空集合失败，TOML / INI 会验证 null 失败；其他格式验证对应值往返。Save / Update 对照核实实际文件结果、当前文件与草稿保留，以及当前格式和保护选择不受下次新建设置影响。保护结果通过重新创建文件实例读取核对；除明确展示虚构手写文件的步骤外，观察区与日志检查敏感值没有泄漏。完成后自动退出；需要桌面会话及所选保护设施。省略路径时使用新的临时目录，也可在 `--verify` 后传入一个尚不存在的 `.json`、`.yaml`、`.toml` 或 `.ini` 路径：

```powershell
dotnet run --project samples/ConfioSample/ConfioSample.csproj -c Release -- --verify
./artifacts/sample-aot/ConfioSample.exe --verify --aes
```

成功返回 `0`，在初始配置路径旁生成 `.verification.txt` 和默认值、引导关键步骤、完整模型、读写、集合、子对象、边界失败、明文自动保护前后、Save / Update 对照、容器、源码、日志及较小窗口的 PNG 截图；失败返回非零退出码。最小窗口检查引导展开与收起时编辑区及观察区的可用高度，内容较多时可独立滚动。UI 入口用于理解和验证消费体验，运行库的并发、保护认证失败及其他复杂边界仍由[行为测试](../../tests/ConfioTests/ConfigurationFileTests.cs)、[格式与操作顺序测试](../../tests/ConfioTests/AdditionalFormatTests.cs)、[保护测试](../../tests/ConfioTests/ProtectionTests.cs)、[明文保护测试](../../tests/ConfioTests/PlaintextProtectionTests.cs)、[平台文件测试](../../tests/ConfioTests/PlatformFileTests.cs)、[原生接入测试](../../tests/ConfioTests/NativeIntegrationTests.cs)和[真实消费者](../../tests/ConfioConsumer/Program.cs)验收。

各平台的实际验收状态统一维护在[设计第 8.1 节](../../docs/设计方案.md#81-独立交付边界与当前状态)。

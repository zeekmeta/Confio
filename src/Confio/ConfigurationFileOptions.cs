namespace Confio;

/// <summary>
/// 配置文件的可选设置；创建或注册时固定设置并复制密钥，后续修改不影响已有实例。
/// </summary>
public sealed class ConfigurationFileOptions
{
    /// <summary>
    /// 显式指定格式；省略时根据文件扩展名选择，默认文件使用 JSON。
    /// </summary>
    public ConfigurationFormat? Format { get; set; }

    /// <summary>
    /// 选择保护方式；Auto 在 Windows 使用 DPAPI，其他支持平台使用自动 AES。
    /// 指定 AES 密钥来源时 Auto 使用 AES。
    /// </summary>
    public ConfigurationProtection Protection { get; set; }

    /// <summary>
    /// 应用提供的稳定 32 字节 AES 密钥；组件复制使用，不另行保存。
    /// </summary>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>
    /// 自动 AES 密钥文件的位置；省略时使用当前用户的应用密钥目录。
    /// 不能与 EncryptionKey 或外部保护器同时指定。
    /// </summary>
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// 是否在加载已有明文时自动保护并写回；关闭后显式保存仍执行保护。
    /// </summary>
    public bool ProtectPlaintextOnLoad { get; set; } = true;

    /// <summary>
    /// 借用自定义保护器；不能同时指定内置保护方式或 AES 密钥来源，组件不释放该实例。
    /// </summary>
    public IConfigurationProtector? Protector { get; set; }
}

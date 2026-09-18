namespace Confio;

/// <summary>
/// 内置字段保护方式，密钥来源由文件选项指定。
/// </summary>
public enum ConfigurationProtection
{
    /// <summary>
    /// Windows 使用 DPAPI，macOS / Linux 使用自动 AES；指定 AES 密钥来源时使用 AES。
    /// </summary>
    Auto,

    /// <summary>
    /// Windows 当前用户 DPAPI。
    /// </summary>
    Dpapi,

    /// <summary>
    /// AES-256-GCM；未提供密钥时自动生成、保存并复用。
    /// </summary>
    AesGcm
}

namespace Confio;

/// <summary>
/// 配置文件的持久化格式。
/// </summary>
public enum ConfigurationFormat
{
    /// <summary>
    /// JSON。
    /// </summary>
    Json,

    /// <summary>
    /// YAML，包含 .yml 扩展名。
    /// </summary>
    Yaml,

    /// <summary>
    /// TOML 1.1。
    /// </summary>
    Toml,

    /// <summary>
    /// Confio 支持的 INI 方言。
    /// </summary>
    Ini
}

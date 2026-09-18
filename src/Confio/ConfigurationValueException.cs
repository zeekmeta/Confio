using System;
using System.Text.Json;

namespace Confio;

/// <summary>
/// 候选配置值无法按所选文件格式无损写入；失败发生在文件提交之前。
/// </summary>
/// <remarks>
/// 异常只携带格式、位置和原因，不包含配置值或第三方异常正文。
/// 应用可按结构化属性生成提示，无需解析英文异常消息。
/// </remarks>
public sealed class ConfigurationValueException : NotSupportedException
{
    internal ConfigurationValueException(string format, string path, ConfigurationValueError reason)
        : base($"{format} cannot persist {Describe(reason)} at '{JsonEncodedText.Encode(path)}'.")
    {
        Format = format;
        Path = path;
        Reason = reason;
    }

    /// <summary>
    /// 无法写入的文件格式名称，例如 TOML 或 INI。
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// 相对于文件根的 JSON Pointer，使用最终序列化名称及从零开始的数组索引。
    /// 路径段中的 ~ 和 / 分别转义为 ~0 和 ~1。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 无法写入的具体原因。
    /// </summary>
    public ConfigurationValueError Reason { get; }

    private static string Describe(ConfigurationValueError reason) => reason switch
    {
        ConfigurationValueError.Null => "null",
        ConfigurationValueError.EmptyObject => "an empty object or dictionary",
        ConfigurationValueError.EmptyCollection => "an empty collection",
        ConfigurationValueError.MultilineString => "a multiline string",
        ConfigurationValueError.IntegerOutOfRange => "an integer outside the signed 64-bit range",
        ConfigurationValueError.NumericPrecisionLoss => "a number without loss through a finite 64-bit float",
        ConfigurationValueError.UnsupportedKey => "an unsupported key",
        ConfigurationValueError.MaximumDepthExceeded => "a container beyond the maximum depth of 64",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}

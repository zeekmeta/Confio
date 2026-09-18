namespace Confio;

/// <summary>
/// 配置值不能按所选文件格式无损写入的原因。
/// </summary>
public enum ConfigurationValueError
{
    /// <summary>
    /// 格式不支持显式 null。
    /// </summary>
    Null,

    /// <summary>
    /// 格式不支持空对象或空字典。
    /// </summary>
    EmptyObject,

    /// <summary>
    /// 格式不支持空数组或空列表。
    /// </summary>
    EmptyCollection,

    /// <summary>
    /// 格式不支持包含换行的字符串。
    /// </summary>
    MultilineString,

    /// <summary>
    /// 整数超出格式支持的有符号 64 位范围。
    /// </summary>
    IntegerOutOfRange,

    /// <summary>
    /// 数值无法通过格式的有限 64 位浮点数无损往返。
    /// </summary>
    NumericPrecisionLoss,

    /// <summary>
    /// 键名无法按格式的路径或行规则往返。
    /// </summary>
    UnsupportedKey,

    /// <summary>
    /// 容器深度超过 64 层，包含配置节外层。
    /// </summary>
    MaximumDepthExceeded
}

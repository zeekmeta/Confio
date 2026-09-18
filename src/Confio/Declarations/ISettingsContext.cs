using System.Collections.Generic;

namespace Confio;

/// <summary>
/// 为文件实例提供显式选择的静态配置声明，由生成的上下文或等价手写声明实现。
/// </summary>
public interface ISettingsContext
{
    /// <summary>
    /// 取得本上下文贡献的配置声明；文件实例在构造时固定这些声明。
    /// </summary>
    IReadOnlyList<SettingsDeclaration> GetSettingsDeclarations();
}

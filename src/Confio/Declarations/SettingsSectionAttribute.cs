using System;

namespace Confio;

/// <summary>
/// 声明配置模型在选定文件中拥有的配置节，层级使用冒号分隔。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SettingsSectionAttribute : Attribute
{
    /// <summary>
    /// 创建配置节声明；省略路径表示模型拥有整个文件根对象。
    /// </summary>
    public SettingsSectionAttribute(string path = "")
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>
    /// 获取配置节路径。
    /// </summary>
    public string Path { get; }
}

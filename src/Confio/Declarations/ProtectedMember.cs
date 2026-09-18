using System;
using System.Collections.Generic;

namespace Confio;

/// <summary>
/// 将编译期识别的受保护成员关联到上下文的实际序列化名称。
/// </summary>
public sealed class ProtectedMember
{
    /// <summary>
    /// 创建成员声明；jsonName 为显式 JSON 名称，省略时采用命名策略。
    /// containingTypes 是该成员经未整体保护路径可达的所有外层类型，供静态声明检查转换器遮蔽保护。
    /// </summary>
    public ProtectedMember(Type declaringType, string memberName, string? jsonName = null, params Type[] containingTypes)
    {
        DeclaringType = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
        MemberName = memberName ?? throw new ArgumentNullException(nameof(memberName));
        JsonName = jsonName;
        ContainingTypes = containingTypes ?? throw new ArgumentNullException(nameof(containingTypes));
    }

    /// <summary>
    /// 获取声明该成员的 CLR 类型。
    /// </summary>
    public Type DeclaringType { get; }

    /// <summary>
    /// 获取 CLR 成员名。
    /// </summary>
    public string MemberName { get; }

    /// <summary>
    /// 获取显式 JSON 名称；null 表示采用命名策略。
    /// </summary>
    public string? JsonName { get; }

    internal IReadOnlyList<Type> ContainingTypes { get; }
}

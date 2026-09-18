using System;

namespace Confio;

/// <summary>
/// 声明持久化时保护字符串或整个子对象；空字符串与 null 保持原值。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ProtectedAttribute : Attribute
{
}

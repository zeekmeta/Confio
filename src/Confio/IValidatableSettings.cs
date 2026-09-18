namespace Confio;

/// <summary>
/// 为配置节声明业务校验；加载、保存、更新和重置在发布候选快照前调用。
/// </summary>
/// <remarks>
/// 校验只读取模型，不修改值、不执行 I/O，也不重入文件操作。
/// 不满足规则时抛出 <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>，
/// 消息应描述规则，不包含敏感配置值。异常原样交给调用方。
/// 已发布快照的普通读取不重复校验；嵌套对象与跨字段规则由配置节自行检查。
/// </remarks>
public interface IValidatableSettings
{
    /// <summary>
    /// 检查完整模型是否符合业务规则。
    /// </summary>
    void Validate();
}

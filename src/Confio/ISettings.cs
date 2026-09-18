using System;
using System.Threading;
using System.Threading.Tasks;

namespace Confio;

/// <summary>
/// 提供一个已声明模型的读写入口；默认实现共享文件实例，不拥有独立文件生命周期。
/// </summary>
/// <remarks>
/// 首次加载会自动保护文件中已声明位置的非空明文；Save 和 Update 的模型输入始终视为明文。
/// 写入同时保护其他所选模型的待保护明文，合并为一次文件提交。
/// 格式无法无损表达候选值时抛出 <see cref="ConfigurationValueException"/>，保留文件、已发布快照与通知状态。
/// </remarks>
/// <typeparam name="T">已通过上下文声明的配置模型。</typeparam>
public interface ISettings<T> where T : class
{
    /// <summary>
    /// 首次使用时加载文件并自动保护明文，返回独立的模型副本；之后只访问快照。
    /// </summary>
    T Read();

    /// <summary>
    /// 异步加载、自动保护明文并返回独立副本；已加载时只访问内存，提交后取消不撤回结果。
    /// </summary>
    Task<T> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅共享文件成功提交或重载后的变化，每次提供当前模型的独立副本。
    /// 不立即回调；通知在锁外执行，不保证 UI 线程，释放返回值可退订。
    /// </summary>
    IDisposable OnChange(Action<T> listener);

    /// <summary>
    /// 完整替换该模型拥有的配置节；调用期间不得并发修改输入对象。
    /// </summary>
    void Save(T value);

    /// <summary>
    /// 异步保存完整模型；提交前取消保留原文件，提交后取消不撤回保存。
    /// </summary>
    Task SaveAsync(T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于最新文件值执行一次同步修改并提交；回调只修改内存，不重入文件操作。
    /// </summary>
    void Update(Action<T> update);

    /// <summary>
    /// 异步完成文件协调和提交；同步回调只执行一次，不接受 async void。
    /// </summary>
    Task UpdateAsync(Action<T> update, CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于最新文件值执行一次同步变换并保存返回的非空模型，支持 record 与 init 属性。
    /// 回调只操作内存，不重入文件操作，失败不提交且不重试。
    /// </summary>
    void Update(Func<T, T> update);

    /// <summary>
    /// 异步完成文件协调和提交，同步变换回调只执行一次；返回值不得为 null。
    /// </summary>
    Task UpdateAsync(Func<T, T> update, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除该配置节的持久化覆盖，恢复模型默认值。
    /// </summary>
    void Reset();

    /// <summary>
    /// 异步删除持久化覆盖；覆盖不存在时不创建配置文件。
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}

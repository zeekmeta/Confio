using System.Threading;
using System.Threading.Tasks;

namespace Confio;

/// <summary>
/// 为配置节点提供带用途隔离的认证保护；外部实现的生命周期由调用方管理。
/// </summary>
/// <remarks>
/// 输入缓冲区仅在调用期间借用；返回缓冲区交由组件使用。实现不得在完成后保留明文缓冲区，
/// 组件在转换结束后清零明文字节。字符串与模型仍遵守托管内存生命周期。
/// </remarks>
public interface IConfigurationProtector
{
    /// <summary>
    /// 保护完整载荷；必须认证用途，不能降级为明文。
    /// </summary>
    byte[] Protect(byte[] plaintext, string purpose);

    /// <summary>
    /// 认证并恢复完整载荷；用途不匹配或无法恢复时必须失败。
    /// </summary>
    byte[] Unprotect(byte[] ciphertext, string purpose);

    /// <summary>
    /// 使用设施支持的异步路径保护载荷；平台同步调用期间的取消边界由实现说明。
    /// </summary>
    Task<byte[]> ProtectAsync(byte[] plaintext, string purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用设施支持的异步路径恢复载荷；失败不得生成新密钥替代。
    /// </summary>
    Task<byte[]> UnprotectAsync(byte[] ciphertext, string purpose, CancellationToken cancellationToken = default);
}

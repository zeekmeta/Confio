using System.Diagnostics.Tracing;

namespace Confio.Internal;

[EventSource(Name = "Confio-Configuration")]
internal sealed class ConfigurationDiagnostics : EventSource
{
    internal static readonly ConfigurationDiagnostics Log = new();

    private ConfigurationDiagnostics() { }

    // 不传入异常正文或配置值，消费者的校验消息可能包含秘密。
    [Event(1, Level = EventLevel.Error, Message = "A configuration notification failed after publication ({0}).")]
    public void NotificationFailed(string exceptionType) => WriteEvent(1, exceptionType);

    [Event(2, Level = EventLevel.Error, Message = "Startup failure cleanup encountered an additional error ({0}).")]
    public void CleanupFailed(string exceptionType) => WriteEvent(2, exceptionType);
}

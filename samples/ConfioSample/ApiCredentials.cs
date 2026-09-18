namespace ConfioSample;

// MailSettings.Credentials 上的 [Protected] 保护整个对象，包括两个成员。
public sealed class ApiCredentials
{
    public string UserName { get; set; } = "";
    public string AccessToken { get; set; } = "";
}

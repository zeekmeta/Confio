namespace ConfioSample;

// 每一行在真实操作后取值；保留原作用域，并另建作用域观察 Snapshot 的缓存边界。
public sealed record OptionsObservation(
    string Stage,
    string? Configuration,
    int Options,
    int ExistingScope,
    int NewScope,
    int Monitor,
    int Notifications);

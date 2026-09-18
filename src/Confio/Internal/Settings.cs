using System;
using System.Threading;
using System.Threading.Tasks;

namespace Confio.Internal;

internal sealed class Settings<T> : ISettings<T> where T : class
{
    private readonly ConfigurationFile _file;

    internal Settings(ConfigurationFile file) => _file = file;

    public T Read() => _file.Read<T>();
    public Task<T> ReadAsync(CancellationToken cancellationToken = default) => _file.ReadAsync<T>(cancellationToken);
    public IDisposable OnChange(Action<T> listener) => _file.OnChange(listener);
    public void Save(T value) => _file.Save(value);
    public Task SaveAsync(T value, CancellationToken cancellationToken = default) => _file.SaveAsync(value, cancellationToken);
    public void Update(Action<T> update) => _file.Update(update);
    public Task UpdateAsync(Action<T> update, CancellationToken cancellationToken = default) => _file.UpdateAsync(update, cancellationToken);
    public void Update(Func<T, T> update) => _file.Update(update);
    public Task UpdateAsync(Func<T, T> update, CancellationToken cancellationToken = default) => _file.UpdateAsync(update, cancellationToken);
    public void Reset() => _file.Reset<T>();
    public Task ResetAsync(CancellationToken cancellationToken = default) => _file.ResetAsync<T>(cancellationToken);
}

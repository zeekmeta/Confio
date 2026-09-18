using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio.Formats;
using Confio.Internal;
using Confio.Protection;
using Microsoft.Extensions.Primitives;
using ConfigurationReloadToken = Microsoft.Extensions.Configuration.ConfigurationReloadToken;

namespace Confio;

/// <summary>
/// 管理一个选定的配置文件；构造不做文件或密钥 I/O，首次读写加载并建立共享快照。
/// </summary>
/// <remarks>
/// 读取返回独立副本，修改模型后显式保存；文件加载时自动保护已声明位置的非空明文。
/// 文件中受保护位置的 enc: 前缀保留给密文，Save 和 Update 的模型输入始终视为明文。
/// 写入同时保护其他所选模型的待保护明文，合并为一次文件提交。
/// 格式无法无损表达候选值时抛出 <see cref="ConfigurationValueException"/>，保留文件、已发布快照与通知状态。
/// 调用方应完成全部操作后再释放实例，
/// 不将 Dispose 与读写并发执行；更新回调只修改内存，不重入本实例。
/// 原生读取接入的通知在发布快照并释放文件锁后执行，不保证回调线程；通知失败不撤回提交。
/// </remarks>
public sealed class ConfigurationFile : IDisposable
{
    private readonly FileDefinition _definition;
    private readonly NodeProtection _protection;
    private readonly IDisposable? _ownedProtector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileSnapshot? _snapshot;
    private bool _configurationEnabled;
    private ConfigurationReloadToken _reloadToken = new();
    private int _disposed;

    /// <summary>
    /// 使用一个静态上下文创建文件；省略路径时使用当前用户的应用配置目录。
    /// 设置和密钥在构造时固定，外部保护器始终借用；构造不进行文件或密钥 I/O。
    /// </summary>
    public ConfigurationFile(ISettingsContext context, string? path = null, ConfigurationFileOptions? options = null)
        : this(new[] { context ?? throw new ArgumentNullException(nameof(context)) }, path, options)
    {
    }

    /// <summary>
    /// 一次选择多个上下文；模型及配置节归属不能重复或重叠。
    /// </summary>
    public ConfigurationFile(IEnumerable<ISettingsContext> contexts, string? path = null, ConfigurationFileOptions? options = null)
        : this(new FileDefinition(contexts, path, options))
    {
    }

    internal ConfigurationFile(FileDefinition definition)
    {
        _definition = definition;
        var selected = definition.Protector ?? (definition.Protection == ConfigurationProtection.Dpapi
            ? (IConfigurationProtector)new DpapiProtector()
            : definition.EncryptionKey is { } key ? new AesGcmProtector(key) : new FileKeyProtector(definition.KeyFilePath!));
        _ownedProtector = definition.Protector is null ? selected as IDisposable : null;
        _protection = new NodeProtection(selected);
    }

    /// <summary>
    /// 获取本实例固定使用的绝对配置文件路径。
    /// </summary>
    public string Path => _definition.FilePath;

    /// <summary>
    /// 获取自动 AES 密钥的绝对文件路径；不使用密钥文件时为 null，路径存在不代表文件已创建。
    /// </summary>
    public string? KeyFilePath => _definition.KeyFilePath;

    internal FileDefinition Definition
    {
        get
        {
            ThrowIfDisposed();
            return _definition;
        }
    }

    internal Dictionary<string, string?> ConfigurationValues
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _snapshot)?.Configuration ??
                throw new InvalidOperationException("Native configuration access requires the configuration startup entry point.");
        }
    }

    internal IChangeToken GetReloadToken() => Volatile.Read(ref _reloadToken);

    internal T ReadLoaded<T>() where T : class => ReadValue<T>(Declaration<T>(),
        Volatile.Read(ref _snapshot) ?? throw new InvalidOperationException("The configuration file has not been loaded."));

    internal void EnableConfiguration()
    {
        ThrowIfDisposed();
        _gate.Wait();
        try
        {
            var snapshot = _snapshot?.WithConfiguration() ?? LoadFile(configuration: true);
            _configurationEnabled = true;
            Volatile.Write(ref _snapshot, snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task EnableConfigurationAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _snapshot?.WithConfiguration();
            cancellationToken.ThrowIfCancellationRequested();
            snapshot ??= await LoadFileAsync(cancellationToken, configuration: true).ConfigureAwait(false);
            _configurationEnabled = true;
            Volatile.Write(ref _snapshot, snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 首次使用时加载文件并自动保护已声明位置的明文，返回最近成功加载或提交的独立模型副本。
    /// </summary>
    public T Read<T>() where T : class
    {
        var declaration = Declaration<T>();
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null)
        {
            _gate.Wait();
            try
            {
                snapshot = _snapshot;
                if (snapshot is null)
                {
                    snapshot = LoadFile();
                    Volatile.Write(ref _snapshot, snapshot);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        return ReadValue<T>(declaration, snapshot);
    }

    /// <summary>
    /// 异步加载并自动保护文件中的明文；已有快照时只访问内存，预先取消不发起加载。
    /// </summary>
    public async Task<T> ReadAsync<T>(CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declaration = Declaration<T>();
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                snapshot = _snapshot;
                if (snapshot is null)
                {
                    snapshot = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _snapshot, snapshot);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        return ReadValue<T>(declaration, snapshot);
    }

    /// <summary>
    /// 订阅本实例成功提交或重载后的文件级变化，每次提供当前模型的独立副本。
    /// </summary>
    /// <remarks>
    /// 订阅不加载文件或立即回调；其他配置节的提交也会触发通知，不比较属性差异。
    /// 回调在锁外执行，不保证 UI 线程或逐个交付并发提交的中间值。
    /// 回调失败通过诊断报告，不回滚已提交结果；释放返回值可退订，已开始的回调仍可完成。
    /// </remarks>
    public IDisposable OnChange<T>(Action<T> listener) where T : class
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        var declaration = Declaration<T>();
        return ChangeToken.OnChange(GetReloadToken, () =>
            listener(ReadValue<T>(declaration, Volatile.Read(ref _snapshot)!)));
    }

    /// <summary>
    /// 完整替换该模型拥有的配置节，保留其他配置节；保存期间不得并发修改输入对象。
    /// </summary>
    public void Save<T>(T value) where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        var declaration = Declaration<T>();
        Write(declaration, declaration.Serialize(value), update: null, reset: false);
    }

    /// <summary>
    /// 异步保存完整模型；提交前取消保留原文件，提交后不因迟到取消撤回结果。
    /// </summary>
    public async Task SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var declaration = Declaration<T>();
        var node = declaration.Serialize(value);
        await WriteAsync(declaration, node, update: null, reset: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在协调范围内读取最新值，执行一次同步修改并保存；回调不做 I/O，也不重入文件操作。
    /// </summary>
    public void Update<T>(Action<T> update) where T : class
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }
        Write(Declaration<T>(), value: null, value =>
        {
            update((T)value);
            return value;
        }, reset: false);
    }

    /// <summary>
    /// 异步协调与提交；同步修改回调只执行一次，不使用 async void，不保证回调线程。
    /// </summary>
    public Task UpdateAsync<T>(Action<T> update, CancellationToken cancellationToken = default) where T : class
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }
        return WriteAsync(Declaration<T>(), value: null, value =>
        {
            update((T)value);
            return value;
        }, reset: false, cancellationToken);
    }

    /// <summary>
    /// 基于最新文件值执行一次同步变换并保存返回的模型，支持 record 与 init 属性。
    /// </summary>
    /// <remarks>
    /// 回调遵守可变模型更新的执行约束；返回值不得为 null，失败不提交且不重试。
    /// </remarks>
    public void Update<T>(Func<T, T> update) where T : class
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        Write(Declaration<T>(), value: null, value => update((T)value), reset: false);
    }

    /// <summary>
    /// 异步完成文件协调和提交，同步变换回调只执行一次；返回值不得为 null。
    /// </summary>
    public Task UpdateAsync<T>(Func<T, T> update, CancellationToken cancellationToken = default) where T : class
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        return WriteAsync(Declaration<T>(), value: null, value => update((T)value), reset: false, cancellationToken);
    }

    /// <summary>
    /// 删除该模型的持久化覆盖，恢复默认值；文件或覆盖不存在时不新建配置文件。
    /// </summary>
    public void Reset<T>() where T : class => Write(Declaration<T>(), value: null, update: null, reset: true);

    /// <summary>
    /// 异步删除持久化覆盖并发布默认值，失败或发布前取消保留旧快照。
    /// </summary>
    public Task ResetAsync<T>(CancellationToken cancellationToken = default) where T : class =>
        WriteAsync(Declaration<T>(), value: null, update: null, reset: true, cancellationToken);

    /// <summary>
    /// 重新读取整个文件，自动保护已声明位置的明文；成功后发布新快照，失败保留已有快照。
    /// </summary>
    public void Reload()
    {
        ThrowIfDisposed();
        _gate.Wait();
        try
        {
            var candidate = LoadFile();
            Volatile.Write(ref _snapshot, candidate);
        }
        finally
        {
            _gate.Release();
        }
        NotifyChanged();
    }

    /// <summary>
    /// 异步重新加载并自动保护文件中的明文；提交或发布前取消保留旧快照，成功后不因迟到取消撤回结果。
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, candidate);
        }
        finally
        {
            _gate.Release();
        }
        NotifyChanged();
    }

    /// <summary>
    /// 释放实例拥有的资源，不隐式保存或等待未完成的操作；可重复调用。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _snapshot, null);
            _ownedProtector?.Dispose();
            _gate.Dispose();
        }
    }

    private void Write(SettingsDeclaration declaration, JsonNode? value, Func<object, object>? update, bool reset)
    {
        var published = false;
        _gate.Wait();
        try
        {
            if (reset)
            {
                var input = FileStore.Read(Path, _definition.Format, out var exists);
                if (!exists)
                {
                    Volatile.Write(ref _snapshot, LoadSnapshot(input));
                    published = true;
                    return;
                }
            }

            using var fileLock = FileStore.Acquire(Path);
            var document = FileStore.Read(Path, _definition.Format, out _);
            if (update is not null)
            {
                var current = LoadSnapshot(document);
                var model = declaration.Read(ConfigurationPath.Get(current.Document, declaration.Segments, out _)!);
                value = declaration.Serialize(update(model) ??
                    throw new InvalidOperationException("An update callback cannot return null."));
            }

            bool changed;
            FileSnapshot candidate;
            if (reset)
            {
                changed = document.Remove(declaration.Segments);
                candidate = LoadSnapshot(document);
            }
            else
            {
                candidate = LoadSnapshot(document, declaration, value);
                var persisted = value!.DeepClone();
                _protection.Protect(ProtectionSlots(declaration, persisted));
                document.Set(declaration.Segments, persisted, declaration.Schema);
                changed = true;
            }

            var plaintext = PlaintextSlots(document);
            _protection.Protect(plaintext);
            document.ApplyProtection(plaintext);
            if (changed || plaintext.Count != 0)
            {
                FileStore.Write(Path, document.Encode());
            }
            Volatile.Write(ref _snapshot, candidate);
            published = true;
        }
        finally
        {
            _gate.Release();
            if (published)
            {
                NotifyChanged();
            }
        }
    }

    private async Task WriteAsync(SettingsDeclaration declaration, JsonNode? value, Func<object, object>? update,
        bool reset, CancellationToken cancellationToken)
    {
        var published = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (reset)
            {
                var input = await FileStore.ReadAsync(Path, _definition.Format, cancellationToken).ConfigureAwait(false);
                if (!input.Exists)
                {
                    var defaults = await LoadSnapshotAsync(input.Document, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Volatile.Write(ref _snapshot, defaults);
                    published = true;
                    return;
                }
            }

            using var fileLock = await FileStore.AcquireAsync(Path, cancellationToken).ConfigureAwait(false);
            var latest = await FileStore.ReadAsync(Path, _definition.Format, cancellationToken).ConfigureAwait(false);
            var document = latest.Document;
            if (update is not null)
            {
                var current = await LoadSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
                var model = declaration.Read(ConfigurationPath.Get(current.Document, declaration.Segments, out _)!);
                cancellationToken.ThrowIfCancellationRequested();
                value = declaration.Serialize(update(model) ??
                    throw new InvalidOperationException("An update callback cannot return null."));
            }

            bool changed;
            FileSnapshot candidate;
            if (reset)
            {
                changed = document.Remove(declaration.Segments);
                candidate = await LoadSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                candidate = await LoadSnapshotAsync(document, cancellationToken, declaration, value).ConfigureAwait(false);
                var persisted = value!.DeepClone();
                await _protection.ProtectAsync(ProtectionSlots(declaration, persisted), cancellationToken).ConfigureAwait(false);
                document.Set(declaration.Segments, persisted, declaration.Schema);
                changed = true;
            }

            var plaintext = PlaintextSlots(document);
            await _protection.ProtectAsync(plaintext, cancellationToken).ConfigureAwait(false);
            document.ApplyProtection(plaintext);
            cancellationToken.ThrowIfCancellationRequested();
            if (changed || plaintext.Count != 0)
            {
                await FileStore.WriteAsync(Path, document.Encode(), cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _snapshot, candidate);
            published = true;
        }
        finally
        {
            _gate.Release();
            if (published)
            {
                NotifyChanged();
            }
        }
    }

    private SettingsDeclaration Declaration<T>() where T : class
    {
        ThrowIfDisposed();
        if (!_definition.Declarations.TryGetValue(typeof(T), out var declaration))
        {
            throw new InvalidOperationException("The requested settings type was not declared in the selected contexts.");
        }
        return declaration;
    }

    private static T ReadValue<T>(SettingsDeclaration declaration, FileSnapshot snapshot) where T : class =>
        (T)declaration.Read(ConfigurationPath.Get(snapshot.Document, declaration.Segments, out _)!);

    private FileSnapshot LoadFile(bool configuration = false)
    {
        var document = FileStore.Read(Path, _definition.Format, out _);
        if (!_definition.ProtectPlaintextOnLoad || PlaintextSlots(document).Count == 0)
        {
            return LoadSnapshot(document, configuration: configuration);
        }

        // 只有需要保护写回才取得文件锁，锁内重读，避免覆盖另一个进程的新值。
        using var fileLock = FileStore.Acquire(Path);
        document = FileStore.Read(Path, _definition.Format, out _);
        var candidate = LoadSnapshot(document, configuration: configuration);
        var plaintext = PlaintextSlots(document);
        _protection.Protect(plaintext);
        document.ApplyProtection(plaintext);
        if (plaintext.Count != 0)
        {
            FileStore.Write(Path, document.Encode());
        }
        return candidate;
    }

    private async Task<FileSnapshot> LoadFileAsync(CancellationToken cancellationToken, bool configuration = false)
    {
        var input = await FileStore.ReadAsync(Path, _definition.Format, cancellationToken).ConfigureAwait(false);
        if (!_definition.ProtectPlaintextOnLoad || PlaintextSlots(input.Document).Count == 0)
        {
            var snapshot = await LoadSnapshotAsync(input.Document, cancellationToken, configuration: configuration).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }

        using var fileLock = await FileStore.AcquireAsync(Path, cancellationToken).ConfigureAwait(false);
        input = await FileStore.ReadAsync(Path, _definition.Format, cancellationToken).ConfigureAwait(false);
        var candidate = await LoadSnapshotAsync(input.Document, cancellationToken, configuration: configuration).ConfigureAwait(false);
        var plaintext = PlaintextSlots(input.Document);
        await _protection.ProtectAsync(plaintext, cancellationToken).ConfigureAwait(false);
        input.Document.ApplyProtection(plaintext);
        cancellationToken.ThrowIfCancellationRequested();
        if (plaintext.Count != 0)
        {
            await FileStore.WriteAsync(Path, input.Document.Encode(), cancellationToken).ConfigureAwait(false);
        }
        return candidate;
    }

    private JsonObject PrepareSnapshot(FileDocument document, SettingsDeclaration? replacement = null, JsonNode? value = null)
    {
        var plaintext = document.ReadNodes(_definition.Declarations.Values, replacement);
        if (replacement is not null)
        {
            ConfigurationPath.Set(plaintext, replacement.Segments, value!.DeepClone());
        }
        return plaintext;
    }

    private FileSnapshot CompleteSnapshot(JsonObject plaintext, bool configuration)
    {
        foreach (var declaration in _definition.Declarations.Values)
        {
            var node = ConfigurationPath.Get(plaintext, declaration.Segments, out var exists);
            ConfigurationPath.Set(plaintext, declaration.Segments, declaration.Complete(node, exists));
        }
        JsonData.Validate(plaintext);
        var snapshot = new FileSnapshot(plaintext);
        return _configurationEnabled || configuration ? snapshot.WithConfiguration() : snapshot;
    }

    private FileSnapshot LoadSnapshot(FileDocument document, SettingsDeclaration? replacement = null, JsonNode? value = null,
        bool configuration = false)
    {
        var plaintext = PrepareSnapshot(document, replacement, value);
        // 模型替换值始终是明文，密文识别只用于文件输入中的声明位置。
        _protection.Unprotect(ProtectionSlots(plaintext, replacement).Where(slot => NodeProtection.IsEncrypted(slot.Value)));
        return CompleteSnapshot(plaintext, configuration);
    }

    private async Task<FileSnapshot> LoadSnapshotAsync(FileDocument document, CancellationToken cancellationToken,
        SettingsDeclaration? replacement = null, JsonNode? value = null, bool configuration = false)
    {
        var plaintext = PrepareSnapshot(document, replacement, value);
        await _protection.UnprotectAsync(ProtectionSlots(plaintext, replacement).Where(slot => NodeProtection.IsEncrypted(slot.Value)),
            cancellationToken).ConfigureAwait(false);
        return CompleteSnapshot(plaintext, configuration);
    }

    private List<NodeSlot> ProtectionSlots(JsonObject document, SettingsDeclaration? replacement = null)
    {
        var slots = new List<NodeSlot>();
        foreach (var declaration in _definition.Declarations.Values)
        {
            if (declaration != replacement)
            {
                declaration.Schema.CollectProtected(ConfigurationPath.Get(document, declaration.Segments, out _),
                    ConfigurationPath.Pointer(declaration.Segments), slots);
            }
        }
        return slots;
    }

    private List<NodeSlot> PlaintextSlots(FileDocument document) =>
        ProtectionSlots(document.ReadNodes(_definition.Declarations.Values)).FindAll(slot => !NodeProtection.IsEncrypted(slot.Value));

    private static List<NodeSlot> ProtectionSlots(SettingsDeclaration declaration, JsonNode node)
    {
        var slots = new List<NodeSlot>();
        declaration.Schema.CollectProtected(node, ConfigurationPath.Pointer(declaration.Segments), slots);
        return slots;
    }

    private void NotifyChanged()
    {
        var previous = Interlocked.Exchange(ref _reloadToken, new ConfigurationReloadToken());
        try
        {
            previous.OnReload();
        }
        catch (Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                foreach (var failure in aggregate.Flatten().InnerExceptions)
                {
                    ConfigurationDiagnostics.Log.NotificationFailed(failure.GetType().Name);
                }
            }
            else
            {
                ConfigurationDiagnostics.Log.NotificationFailed(exception.GetType().Name);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ConfigurationFile));
        }
    }
}

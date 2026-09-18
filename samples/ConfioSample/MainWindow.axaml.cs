using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Confio;
using Microsoft.Extensions.Options;

namespace ConfioSample;

public partial class MainWindow : Window
{
    private const string ExamplePassword = "confio-sample-password";
    private const string ExampleUserName = "confio-demo-user";
    private const string ExampleToken = "confio-demo-token";
    private static readonly string[] SourceFiles =
        ["MailSettings.cs", "ApiCredentials.cs", "RetrySettings.cs", "MainWindow.Actions.cs", "IntegrationExamples.cs", "MainWindow.axaml.cs"];
    private static readonly AppSettingsContext DisplayContext = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    private readonly string _initialPath;
    private readonly bool _verify;
    private readonly Queue<string> _history = new();
    private ConfigurationFile? _file;
    private ConfigurationFileOptions _fileOptions = new();
    private bool _openedExisting;
    private byte[]? _fileBeforeDamage;
    private IDisposable? _subscription;
    private int _notifications;
    private int _sequence;
    private CancellationTokenSource? _operationCancellation;
    private Task _pendingOperation = Task.CompletedTask;
    private bool _initialized;
    private bool _busy;
    private bool _closeRequested;
    private bool _lastOperationSucceeded;

    private ConfigurationFile CurrentFile => _file ?? throw new InvalidOperationException("Create a sample file first.");
    private bool UseAsync => UseAsyncInput.IsChecked == true;

    public MainWindow() : this(Array.Empty<string>()) { }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        ProtectionInput.SelectedIndex = args.Contains("--aes", StringComparer.Ordinal) ? 1 : 0;
        args = args.Where(argument => argument != "--aes").ToArray();
        _verify = args.Length > 0 && args[0] == "--verify";
        var pathIndex = _verify ? 1 : 0;
        _initialPath = args.Length > pathIndex ? args[pathIndex] : NewPath(".json");
        FilePathText.Text = _initialPath;
        RuntimeLabel.Text = RuntimeFeature.IsDynamicCodeSupported ? ".NET 10 · JIT" : ".NET 10 · Native AOT";
        ShowActivated = !_verify;
        ShowInTaskbar = !_verify;
        _initialized = true;
        UpdateNullableInputs();
        LoadSource();
        RefreshGuide();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await RunOperationAsync("配置已加载；修改后点击保存。文件不存在时使用模型默认值。",
            token =>
            {
                if (_verify && File.Exists(_initialPath))
                    throw new IOException("Verification requires a new, isolated file path.");
                return CreateFileAsync(_initialPath, token);
            }, Editors.All);
        if (_verify) await VerifyAndExitAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _closeRequested = true;
            _operationCancellation?.Cancel();
            StatusText.Text = "正在结束当前操作，完成后关闭…";
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _subscription?.Dispose();
        _file?.Dispose();
        base.OnClosed(e);
    }

    private static string NewPath(string extension) =>
        Path.Combine(Path.GetTempPath(), "ConfioSample", Guid.NewGuid().ToString("N"), "appsettings" + extension);

    private string SelectedExtension => FormatInput.SelectedIndex switch
    {
        1 => ".yaml",
        2 => ".toml",
        3 => ".ini",
        _ => ".json"
    };

    private string CurrentExtension => Path.GetExtension(CurrentFile.Path).ToLowerInvariant();
    private bool SupportsNull => CurrentExtension is ".json" or ".yaml" or ".yml";

    private async Task CreateFileAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        var existing = File.Exists(path);
        var options = new ConfigurationFileOptions
        {
            Protection = ProtectionInput.SelectedIndex == 1 ? ConfigurationProtection.AesGcm : ConfigurationProtection.Auto,
            // 打开已有文件先只读观察；用户显式保存时仍加密。
            ProtectPlaintextOnLoad = !existing
        };
        if (_verify && (options.Protection == ConfigurationProtection.AesGcm || !OperatingSystem.IsWindows()))
            options.KeyFilePath = Path.Combine(Path.GetDirectoryName(_initialPath)!, "keys", "sample.key");
        var next = new ConfigurationFile(AppSettingsContext.Default, path, options: options);
        try
        {
            if (UseAsync) await next.ReadAsync<MailSettings>(cancellationToken);
            else next.Read<MailSettings>();
        }
        catch
        {
            next.Dispose();
            throw;
        }

        _subscription?.Dispose();
        _file?.Dispose();
        _file = next;
        _fileOptions = options;
        _openedExisting = existing;
        GuideExpander.IsExpanded = !existing;
        _fileBeforeDamage = null;
        _guideStep = 0;
        RevealPasswordInput.IsChecked = false;
        ModelDetails.IsExpanded = false;
        _notifications = 0;
        _sequence = 0;
        _history.Clear();
        EventLog.Text = "";
        NotificationText.Text = "直接变更通知 · 0 次";
        _subscription = next.OnChange<RetrySettings>(retry =>
        {
            var count = Interlocked.Increment(ref _notifications);
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_file, next)) return;
                NotificationText.Text = $"直接变更通知 · {Volatile.Read(ref _notifications)} 次";
                Record("OnChange", $"第 {count} 次通知，Retry = {retry.MaxAttempts} 次 / {retry.DelaySeconds} 秒；同文件其他节提交也会通知。");
            });
        });
        FilePathText.Text = path;
        FormatInput.SelectedIndex = CurrentExtension switch { ".yaml" or ".yml" => 1, ".toml" => 2, ".ini" => 3, _ => 0 };
        FormatHintText.Text = CurrentExtension switch
        {
            ".toml" => "TOML：支持集合与多行文本；没有 null，保存 null 会报错并保留原文件。",
            ".ini" => "INI：用冒号与连续索引表示层级；本方言不支持 null、空容器和多行文本，无法保存时保留原文件。",
            _ => "JSON / YAML：支持 null、空集合与多行文本。四种格式使用相同模型和读写 API。"
        };
        NullPasswordTitleText.Text = SupportsNull ? "正常保存：null 与空字符串" : "预期失败：格式不支持 null";
        NullPasswordButton.Content = SupportsNull ? "保存 null" : "验证 null 保存失败";
        NullPasswordExplanationText.Text = SupportsNull
            ? "null 表示没有值，空字符串表示内容为空；当前格式能区分并保存两者。这两种密码值都不会加密。"
            : "这是主动失败演示：点击后尝试保存 null，观察底部的属性位置与失败原因。原文件、旧配置与未保存草稿保留。空字符串是另一种值，可以正常保存。";
        var protection = next.KeyFilePath is not null ? "AES-GCM · 自动持久化密钥" : "Windows DPAPI";
        SessionSummaryText.Text = $"当前：{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {protection}";
        KeyFilePathText.Text = next.KeyFilePath is { } keyPath ? "密钥文件：" + keyPath : "密钥由 Windows 当前用户 DPAPI 管理";
        ComparisonDirectoryText.Text = "";
        OpenComparisonDirectoryButton.IsEnabled = false;
        SaveComparisonOutput.Text = UpdateComparisonOutput.Text = "点击上方按钮查看真实操作过程。";
        SaveComparisonResult.Text = UpdateComparisonResult.Text = "尚未运行";
        PrepareIntegration();
        IntegrationOutput.Text = "选择左侧场景，这里会显示真实调用结果。\n\n使用代码可在“用法源码”页查看。";
        RefreshGuide();
    }

    private void NullableInputChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized) UpdateNullableInputs();
    }

    private void UpdateNullableInputs()
    {
        PasswordInput.IsEnabled = NullPasswordInput.IsChecked != true;
        CredentialsFields.IsEnabled = CredentialsEnabledInput.IsChecked == true;
    }

    private void SourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized) LoadSource();
    }

    private void LoadSource()
    {
        var name = SourceFiles[SourceInput.SelectedIndex];
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"ConfioSample.{name}")
            ?? throw new InvalidOperationException($"The sample source resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        SourcePreview.Text = reader.ReadToEnd();
    }

    private void PrepareIntegration()
    {
        OptionsTable.IsVisible = false;
        OptionsRows.ItemsSource = null;
        NativePanel.IsVisible = false;
        NativePreview.Text = "";
        IntegrationOutput.Text = "";
        IntegrationScroll.Offset = default;
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private static int ReadInteger(NumericUpDown input)
    {
        if (!int.TryParse(input.Text, NumberStyles.Integer, input.NumberFormat, out var value))
            throw new ArgumentException("Enter a valid 32-bit whole number.");
        return value;
    }

    private Task RunOperationAsync(string success, Func<CancellationToken, Task> action, Editors editors = Editors.None)
    {
        if (_busy) return _pendingOperation;
        _pendingOperation = ExecuteOperationAsync(success, action, editors);
        return _pendingOperation;
    }

    private async Task ExecuteOperationAsync(string success, Func<CancellationToken, Task> action, Editors editors)
    {
        _busy = true;
        _lastOperationSucceeded = false;
        _verificationFailure = null;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        Workspace.IsEnabled = false;
        SessionControls.IsEnabled = false;
        OpenDirectoryButton.IsEnabled = false;
        OpenFileButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ShowStatus("正在执行操作…", false);
        var completed = false;
        try
        {
            await action(cancellation.Token);
            completed = true;
            await RefreshViewAsync(editors, cancellation.Token);
            _lastOperationSucceeded = true;
            ShowStatus(success, false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowStatus(completed ? "操作已完成，界面刷新被取消；请刷新观察以核对结果。" : "操作已取消；请刷新观察核对文件与快照。", true);
        }
        catch (Exception exception)
        {
            if (_verify)
                _verificationFailure = $"{exception.GetType().Name} (0x{exception.HResult:X8})\n{exception.StackTrace}";
            // 重载失败也展示实际磁盘原文，避免旧预览被误认为当前文件。
            if (_file is not null) await RefreshFilePreviewAsync(CancellationToken.None);
            var hint = exception switch
            {
                ConfigurationValueException error => FormatValueHint(error),
                ValidationException => "模型校验未通过：主机不能为空，端口须为 1–65535，收件人集合不能为 null，重试次数至少为 1，间隔不能为负数。当前快照保留。",
                OptionsValidationException => "启动校验未通过，请检查服务器地址和端口。",
                CryptographicException => "字段保护失败，请检查密钥、当前用户及系统保护设施。",
                PlatformNotSupportedException => "当前环境无法使用所选保护设施，可选择 AES-GCM 后新建演示。",
                UnauthorizedAccessException => "没有文件访问权限，请使用可写目录。",
                InvalidDataException => "文件内容无效，旧配置仍可读取。请修正后重载；若由演示损坏，可点击“还原演示文件”。",
                IOException => "文件访问失败，请检查路径、密钥文件、权限和占用。",
                ArgumentException or OverflowException => "输入无效，请检查整数范围和文件扩展名。",
                _ => "操作未完成，请检查输入并重试。"
            };
            // 不透传异常正文；格式失败只使用组件提供的位置和原因生成提示。
            ShowStatus($"{(completed ? "操作已完成，但刷新失败。" : "")}{hint}（{exception.GetType().Name}）", true);
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            Workspace.IsEnabled = _file is not null;
            SessionControls.IsEnabled = true;
            OpenDirectoryButton.IsEnabled = _file is not null;
            OpenFileButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            if (_closeRequested) Close();
        }
    }

    private static string FormatValueHint(ConfigurationValueException exception)
    {
        var reason = exception.Reason switch
        {
            ConfigurationValueError.Null => "当前格式不支持 null",
            ConfigurationValueError.EmptyObject => "当前 INI 方言不支持空对象或空字典",
            ConfigurationValueError.EmptyCollection => "当前 INI 方言不支持空集合",
            ConfigurationValueError.MultilineString => "当前 INI 方言不支持多行字符串",
            ConfigurationValueError.IntegerOutOfRange => "整数超出有符号 64 位范围",
            ConfigurationValueError.NumericPrecisionLoss => "数值无法通过有限 64 位浮点数无损保存",
            ConfigurationValueError.UnsupportedKey => "键名不符合当前 INI 方言的路径或行规则",
            ConfigurationValueError.MaximumDepthExceeded => "配置嵌套超过 64 层（包含配置节外层）",
            _ => "值超出当前格式的表达范围"
        };
        return $"{exception.Format} 无法保存 {exception.Path}：{reason}。原文件与旧配置保留，输入草稿保留。";
    }

    private async Task RefreshViewAsync(Editors editors, CancellationToken cancellationToken)
    {
        var mail = UseAsync ? await CurrentFile.ReadAsync<MailSettings>(cancellationToken) : CurrentFile.Read<MailSettings>();
        var retry = UseAsync ? await CurrentFile.ReadAsync<RetrySettings>(cancellationToken) : CurrentFile.Read<RetrySettings>();
        ReadHostText.Text = mail.Host;
        ReadPortText.Text = mail.Port.ToString(CultureInfo.InvariantCulture);
        ReadTlsText.Text = mail.UseTls ? "安全连接（TLS）：已启用" : "安全连接（TLS）：未启用";
        ReadPasswordText.Text = mail.Password is null ? "没有值（null），不会生成密文。"
            : mail.Password.Length == 0 ? "空字符串，没有密码内容，不会生成密文。"
            : "已还原为原始字符串，默认隐藏。保存或读取到表单后，勾选左边“显示”可核对原值。";
        ReadCredentialsText.Text = mail.Credentials is null ? "API 凭据：未启用（null）" : "API 凭据：完整对象，内容默认隐藏。";
        ReadRecipientsText.Text = $"收件人：{mail.Recipients.Length} 个；完整内容可在下方展开。";
        RetrySnapshotText.Text = $"重试配置：最多 {retry.MaxAttempts} 次，每次间隔 {retry.DelaySeconds} 秒";
        ProtectionText.Text = mail.Password is null ? "Password = null · 原样保留"
            : mail.Password.Length == 0 ? "Password = 空字符串 · 原样保留"
            : "Password 已还原 · 观察区隐藏内容";
        if (mail.Credentials is not null)
            ProtectionText.Text += "\nCredentials 已还原为对象 · 观察区隐藏内容";
        if ((editors & Editors.Mail) != 0)
        {
            HostInput.Text = mail.Host;
            PortInput.Text = mail.Port.ToString(PortInput.NumberFormat);
            TlsInput.IsChecked = mail.UseTls;
            NullPasswordInput.IsChecked = mail.Password is null;
            PasswordInput.Text = mail.Password ?? "";
            RecipientsInput.Text = string.Join("\n", mail.Recipients);
            NotesInput.Text = mail.Notes;
            CredentialsEnabledInput.IsChecked = mail.Credentials is not null;
            CredentialUserInput.Text = mail.Credentials?.UserName ?? "";
            CredentialTokenInput.Text = mail.Credentials?.AccessToken ?? "";
        }
        if ((editors & Editors.Retry) != 0)
        {
            AttemptsInput.Text = retry.MaxAttempts.ToString(AttemptsInput.NumberFormat);
            DelayInput.Text = retry.DelaySeconds.ToString(DelayInput.NumberFormat);
        }

        // Read 返回独立副本，脱敏仅影响观察区；不会改变配置快照或表单。
        mail.Password = MaskSecret(mail.Password);
        if (mail.Credentials is { } credentials)
        {
            credentials.UserName = MaskSecret(credentials.UserName)!;
            credentials.AccessToken = MaskSecret(credentials.AccessToken)!;
        }
        var snapshot = new JsonObject
        {
            ["Mail"] = JsonSerializer.SerializeToNode(mail, DisplayContext.MailSettings),
            ["Retry"] = JsonSerializer.SerializeToNode(retry, DisplayContext.RetrySettings)
        };
        SnapshotPreview.Text = snapshot.ToJsonString(DisplayContext.Options);
        await RefreshFilePreviewAsync(cancellationToken);
    }

    private async Task RefreshFilePreviewAsync(CancellationToken cancellationToken)
    {
        var exists = File.Exists(CurrentFile.Path);
        FileStateText.Text = exists ? "文件已存在 · 读取不会自动重载" : "文件尚未创建 · 当前使用模型默认值";
        CorruptFileButton.IsEnabled = exists && !_openedExisting && _fileBeforeDamage is null;
        RestoreFileButton.IsEnabled = _fileBeforeDamage is not null;
        try
        {
            FilePreview.Text = exists
                ? await File.ReadAllTextAsync(CurrentFile.Path, cancellationToken)
                : "文件尚未创建，这是正常的。\n\n程序先使用模型中声明的默认值，例如端口 587。\n\n保存配置后，这里才会出现实际文件内容；密码等受保护字段会自动加密。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileStateText.Text = "文件预览读取失败";
            FilePreview.Text = $"无法读取磁盘原文（{exception.GetType().Name}）。\n请检查文件权限或占用后刷新观察。";
        }
    }

    private static string? MaskSecret(string? value) => string.IsNullOrEmpty(value) ? value : "•••（已还原）";

    private void ShowStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(error ? "#B33632" : "#0B737B"));
        if (message != "正在执行操作…") Record(error ? "未完成" : "完成", message);
    }

    private void Record(string kind, string message)
    {
        _history.Enqueue($"{++_sequence:000}  {DateTime.Now:HH:mm:ss}  [{kind}] {message}");
        while (_history.Count > 100) _history.Dequeue();
        EventLog.Text = string.Join("\n", _history.Reverse());
    }

    [Flags]
    private enum Editors { None = 0, Mail = 1, Retry = 2, All = Mail | Retry }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Confio;

namespace ConfioSample;

public partial class MainWindow
{
    private string _verificationStep = "initial load";
    private string? _verificationFailure;

    // 同一桌面程序通过真实按钮验收，不另建界面或测试运行时。
    private async Task VerifyAndExitAsync()
    {
        var exitCode = 1;
        var outputPath = Path.GetFullPath(_initialPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            Require(_lastOperationSucceeded && _file is not null, "The initial path must be new and usable.");
            Require(!File.Exists(CurrentFile.Path) && CurrentFile.Read<MailSettings>().Port == 587,
                "Reading defaults must not create a file.");
            Require(_notifications == 0, "Subscribing and reading must not emit changes.");
            Require(GuideExpander.IsExpanded && !ModelDetails.IsExpanded && !SessionSettings.IsExpanded &&
                ReadPortText.Text == "587", "Beginners must see the guide and real readable values before advanced controls or raw model JSON.");
            // 截图是验收输出，不是配置文件；目录只在验收模式中提前创建。
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await CaptureAsync(outputPath + ".defaults.png");
            var originalWidth = Width;
            var originalHeight = Height;
            Width = MinWidth;
            Height = MinHeight;
            await CaptureAsync(outputPath + ".compact-guide.png");
            Require(SaveMailButton.IsEffectivelyVisible && ResetMailButton.IsEffectivelyVisible
                && ReadButton.IsEffectivelyVisible && RefreshButton.IsEffectivelyVisible
                && ExternalWriteButton.IsEffectivelyVisible && ReloadButton.IsEffectivelyVisible,
                "The open guide must keep ordinary save, reset, read and reload actions visible.");
            // 最小窗口同时保留引导和固定操作栏，内容区允许滚动。
            Require(MailEditorScroll.Bounds.Height >= 100 && SnapshotScroll.Bounds.Height >= 120
                && FilePreview.Bounds.Height >= 100,
                $"The open guide and fixed actions must leave usable scrolling areas at the minimum window size: editor={MailEditorScroll.Bounds.Height}, model={SnapshotScroll.Bounds.Height}, file={FilePreview.Bounds.Height}.");
            Width = originalWidth;
            Height = originalHeight;

            Workspace.SelectedIndex = 2;
            await ClickAsync(ContainerReadButton, deadline.Token);
            Require(!File.Exists(CurrentFile.Path) && _notifications == 0
                && IntegrationOutput.Text!.Contains("Mail 当前配置：localhost:587", StringComparison.Ordinal),
                "Reading defaults through DI must not create a file or emit changes.");
            Workspace.SelectedIndex = 0;
            await ClickAsync(FillExampleButton, deadline.Token);
            await ClickAsync(SaveMailButton, deadline.Token);
            Require(GuideExpander.IsExpanded && ReadNewFile().Port == 465,
                "Ordinary Save must persist the draft without completing or collapsing the guide.");

            foreach (var asynchronous in new[] { false, true })
            {
                UseAsyncInput.IsChecked = asynchronous;
                SessionSettings.IsExpanded = true;
                await ClickAsync(NewSessionButton, deadline.Token);
                SessionSettings.IsExpanded = false;
                GuideExpander.IsExpanded = true;
                Require(_guideStep == 0 && GuideCodeText.Text!.Contains(asynchronous ? "ReadAsync" : "Read<", StringComparison.Ordinal),
                    "The guide must reset with each new session and show the selected API mode.");
                await ClickAsync(GuideNextButton, deadline.Token);
                Require(_guideStep == 1 && PortInput.Value == 465 && ReadPortText.Text == "587" && !File.Exists(CurrentFile.Path),
                    "The first guide action must change the real draft without saving or changing the program's value.");
                if (asynchronous) await CaptureAsync(outputPath + ".guide-edited.png");
                HostInput.Text = " ";
                await ClickAsync(GuideNextButton, deadline.Token, succeeds: false);
                Require(_guideStep == 1 && ReadPortText.Text == "587" && !File.Exists(CurrentFile.Path),
                    "A failed guide action must stay on the same step and preserve the successful state.");
                HostInput.Text = "smtp.example.com";
                await ClickAsync(GuideNextButton, deadline.Token);
                Require(_guideStep == 2 && ReadPortText.Text == "465" && ReadNewFile().Password == ExamplePassword,
                    "Guided saving must use the same real file and protection path as the ordinary Save button.");
                RequireSafeObservations();
                ModelDetails.IsExpanded = true;
                ModelDetails.BringIntoView();
                if (asynchronous) await CaptureAsync(outputPath + ".model-details.png");
                ModelDetails.IsExpanded = false;
                SnapshotScroll.Offset = default;
                await ClickAsync(GuideNextButton, deadline.Token);
                Require(_guideStep == 3 && ReadPortText.Text == "465" && ReadNewFile().Port == 2525,
                    "The external-write guide action must visibly separate the program's old value from the actual file.");
                if (asynchronous) await CaptureAsync(outputPath + ".guide-external.png");
                await ClickAsync(GuideNextButton, deadline.Token);
                Require(_guideStep == 4 && ReadPortText.Text == "2525" && PortInput.Value == 2525,
                    "Guided reload must update the visible program value and form from the file.");
                if (asynchronous) await CaptureAsync(outputPath + ".guide-complete.png");
                await ClickAsync(GuideSourceButton, deadline.Token);
                Require(Workspace.SelectedIndex == 3 && SourceInput.SelectedIndex == 0 &&
                    SourcePreview.Text!.Contains("[JsonSerializable", StringComparison.Ordinal),
                    "The guide must lead directly to the compiled model and context declaration.");
                Workspace.SelectedIndex = 0;
                await ClickAsync(GuideNextButton, deadline.Token);
                Require(!GuideExpander.IsExpanded && SaveMailButton.IsEffectivelyVisible && ReloadButton.IsEffectivelyVisible,
                    "Finishing the guide must keep ordinary operations available without changing the session.");
                EditorTabs.SelectedIndex = 2;
                var previousPath = CurrentFile.Path;
                var previousBytes = File.Exists(previousPath) ? File.ReadAllBytes(previousPath) : null;
                await ClickAsync(PlaintextFileButton, deadline.Token);
                Require(CurrentFile.Path != previousPath && (previousBytes is null
                        ? !File.Exists(previousPath) : previousBytes.AsSpan().SequenceEqual(File.ReadAllBytes(previousPath))),
                    "The handwritten example must use a new file and preserve the previous session.");
                Require(FilePreview.Text!.Contains(ExamplePassword, StringComparison.Ordinal) &&
                    FilePreview.Text.Contains(ExampleToken, StringComparison.Ordinal) &&
                    CurrentFile.Read<MailSettings>().Password == "" && _notifications == 0,
                    "The handwritten example must show actual plaintext without silently reloading the default snapshot.");
                if (asynchronous) await CaptureAsync(outputPath + ".plaintext.png");
                await ClickAsync(ProtectPlaintextButton, deadline.Token);
                Require(ReadNewFile().Password == ExamplePassword && ReadNewFile().Credentials?.AccessToken == ExampleToken &&
                    _notifications == 1, "Reload must automatically protect the handwritten string and complete object before publishing.");
                RequireSafeObservations();
                var protectedBytes = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                await ClickAsync(ReloadButton, deadline.Token);
                Require(protectedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path)),
                    "Reloading the protected result must not rewrite valid ciphertext.");
                if (asynchronous) await CaptureAsync(outputPath + ".auto-protected.png");
                EditorTabs.SelectedIndex = 0;
                await ClickAsync(ResetMailButton, deadline.Token);
                await ClickAsync(FillExampleButton, deadline.Token);
                Require(CurrentFile.Read<MailSettings>().Port == 587, "Editing must not change the snapshot.");
                TlsInput.IsChecked = false;
                AttemptsInput.Value = 9;
                var notifications = _notifications;
                await ClickAsync(SaveMailButton, deadline.Token);
                var mail = ReadNewFile();
                Require(mail.Password == ExamplePassword && AttemptsInput.Value == 9,
                    "Saving Mail must round-trip protection and preserve the Retry draft.");
                Require(mail.Credentials?.UserName == ExampleUserName && mail.Credentials.AccessToken == ExampleToken,
                    "The protected object must round-trip through a new file instance.");
                Require(!mail.UseTls && mail.Recipients.SequenceEqual(new[] { "ops@example.com", "alerts@example.com" })
                    && mail.Notes.Contains("中文备注", StringComparison.Ordinal), "Boolean, collection and string values must round-trip.");
                Require(FilePreview.Text!.Contains("这是演示数据。", StringComparison.Ordinal),
                    "The file itself must keep Chinese readable without display-side decoding.");
                Require(_notifications == notifications + 1, "Saving Mail must notify the direct file subscription.");
                RequireSafeObservations();
                Require(SnapshotPreview.Text!.Contains("Recipients", StringComparison.Ordinal)
                    && SnapshotPreview.Text.Contains("Credentials", StringComparison.Ordinal)
                    && SnapshotPreview.Text.Contains("Retry", StringComparison.Ordinal), "The observation must show complete model structures.");

                PortInput.Value = 2526;
                NotesInput.Text = "尚未保存的 Mail 备注";
                EditorTabs.SelectedIndex = 1;
                DelayInput.Value = 7;
                await ClickAsync(SaveRetryButton, deadline.Token);
                Require(PortInput.Value == 2526 && NotesInput.Text == "尚未保存的 Mail 备注"
                    && CurrentFile.Read<MailSettings>().Port == 465,
                    "Saving Retry must preserve Mail, including its collection and text drafts.");
                AttemptsInput.Value = 99;
                await ClickAsync(UpdateRetryButton, deadline.Token);
                Require(CurrentFile.Read<RetrySettings>().MaxAttempts == 10 && AttemptsInput.Value == 10,
                    "Update must increment the stored value, not the form draft.");

                await ClickAsync(ExternalWriteButton, deadline.Token);
                Require(CurrentFile.Read<MailSettings>().Port == 465 && FilePreview.Text!.Contains("2525", StringComparison.Ordinal),
                    "External writes must leave the current snapshot unchanged.");
                notifications = _notifications;
                await ClickAsync(RefreshButton, deadline.Token);
                Require(PortInput.Value == 2526 && NotesInput.Text == "尚未保存的 Mail 备注"
                    && CurrentFile.Read<MailSettings>().Port == 465 && _notifications == notifications,
                    "Refreshing observations must preserve drafts and must not reload or notify.");
                await ClickAsync(ReadButton, deadline.Token);
                Require(PortInput.Value == 465, "Read must use the existing snapshot.");
                await ClickAsync(ReloadButton, deadline.Token);
                Require(PortInput.Value == 2525, "Reload must refresh the form from disk.");

                var before = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                notifications = _notifications;
                EditorTabs.SelectedIndex = 2;
                await ClickAsync(InvalidMailButton, deadline.Token, succeeds: false);
                var after = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                Require(before.AsSpan().SequenceEqual(after) && CurrentFile.Read<MailSettings>().Port == 2525,
                    "Invalid input must not change the file or snapshot.");
                Require(_notifications == notifications && StatusText.Text!.Contains("ValidationException", StringComparison.Ordinal),
                    "Model validation must reject saving without publishing a change.");

                // 使用各格式可表达的业务无效值，区分语法解析与模型校验失败。
                var invalidModel = CurrentExtension switch
                {
                    ".json" => "{\"Mail\":{\"Port\":0}}",
                    ".toml" => "[Mail]\nPort = 0\n",
                    ".ini" => "[Mail]\nPort=0\n",
                    _ => "Mail:\n  Port: 0\n"
                };
                try
                {
                    await File.WriteAllTextAsync(CurrentFile.Path, invalidModel, deadline.Token);
                    await ClickAsync(ReloadButton, deadline.Token, succeeds: false);
                    Require(CurrentFile.Read<MailSettings>().Recipients.Length == 2 && _notifications == notifications
                        && StatusText.Text!.Contains("ValidationException", StringComparison.Ordinal),
                        "Invalid model values must preserve the successful snapshot and show a validation error.");
                }
                finally
                {
                    await File.WriteAllBytesAsync(CurrentFile.Path, before, CancellationToken.None);
                }
                await ClickAsync(RefreshButton, deadline.Token);
                EditorTabs.SelectedIndex = 0;
                PortInput.Text = "invalid";
                await ClickAsync(SaveMailButton, deadline.Token, succeeds: false);
                after = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                Require(before.AsSpan().SequenceEqual(after), "Invalid numeric text must not save the control's previous value.");
                await ClickAsync(ReadButton, deadline.Token);
                Require(PortInput.Text == "2525", "Read must also clear invalid numeric input.");

                NotesInput.Text = "第一行：中文备注。\n第二行：保留换行。";
                await ClickAsync(SaveMailButton, deadline.Token, succeeds: CurrentExtension != ".ini");
                if (CurrentExtension == ".ini")
                {
                    Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path)),
                        "Unsupported INI multiline text must fail before writing, without cleaning the user's draft.");
                    Require(NotesInput.Text.Contains('\n'), "A format error must retain the original draft.");
                    Require(StatusText.Text!.Contains("INI 无法保存 /Mail/Notes", StringComparison.Ordinal)
                        && StatusText.Text.Contains("多行字符串", StringComparison.Ordinal),
                        "A multiline failure must identify the field and the actual format constraint.");
                }
                else Require(ReadNewFile().Notes.Contains('\n'), "Multiline text must round trip in JSON, YAML and TOML.");
                await ClickAsync(ReadButton, deadline.Token);
                if (CurrentExtension == ".ini")
                {
                    before = File.ReadAllBytes(CurrentFile.Path);
                    RecipientsInput.Text = "";
                    await ClickAsync(SaveMailButton, deadline.Token, succeeds: false);
                    Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path)),
                        "An empty INI collection must fail rather than restore the model default by omission.");
                    Require(RecipientsInput.Text == "" && StatusText.Text!.Contains("/Mail/Recipients", StringComparison.Ordinal)
                        && StatusText.Text.Contains("空集合", StringComparison.Ordinal),
                        "An empty collection failure must retain the draft and identify the field and reason.");
                    await ClickAsync(ReadButton, deadline.Token);
                }
                await ClickAsync(ResetMailButton, deadline.Token);
                Require(PortInput.Value == 587 && CurrentFile.Read<RetrySettings>().MaxAttempts == 10
                    && CurrentFile.Read<MailSettings>().Credentials?.AccessToken == "",
                    "Resetting Mail must restore its nested defaults and preserve Retry.");
                EditorTabs.SelectedIndex = 1;
                await ClickAsync(ResetRetryButton, deadline.Token);
                Require(AttemptsInput.Value == 3 && DelayInput.Value == 5, "Reset must restore record defaults.");

                EditorTabs.SelectedIndex = 2;
                before = File.ReadAllBytes(CurrentFile.Path);
                notifications = _notifications;
                Require(NullPasswordTitleText.Text!.StartsWith(SupportsNull ? "正常保存" : "预期失败", StringComparison.Ordinal),
                    "Null saving must be clearly labelled as a normal operation or an intentional failure for the current format.");
                await ClickAsync(NullPasswordButton, deadline.Token, succeeds: SupportsNull);
                if (SupportsNull)
                    Require(ReadNewFile().Password is null && NullPasswordInput.IsChecked == true && !PasswordInput.IsEnabled,
                        "Supported nulls must stay null on disk and remain visible as an explicit form state.");
                else
                {
                    Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path)) && ReadNewFile().Password == ""
                        && _notifications == notifications && NullPasswordInput.IsChecked == false,
                        "TOML and INI must reject null without overwriting the previous value.");
                    Require(StatusText.Text!.Contains("/Mail/Password", StringComparison.Ordinal)
                        && StatusText.Text.Contains("不支持 null", StringComparison.Ordinal),
                        "The null failure demonstration must report the exact field and reason.");
                    if (asynchronous) await CaptureAsync(outputPath + ".null-failure.png");
                }
                await ClickAsync(EmptyPasswordButton, deadline.Token);
                Require(ReadNewFile().Password == "" && NullPasswordInput.IsChecked == false && PasswordInput.IsEnabled,
                    "Empty strings must remain distinct from null.");
                if (!SupportsNull)
                {
                    EditorTabs.SelectedIndex = 0;
                    before = File.ReadAllBytes(CurrentFile.Path);
                    notifications = _notifications;
                    NullPasswordInput.IsChecked = true;
                    await ClickAsync(SaveMailButton, deadline.Token, succeeds: false);
                    Require(NullPasswordInput.IsChecked == true && !PasswordInput.IsEnabled
                        && StatusText.Text!.Contains("/Mail/Password", StringComparison.Ordinal),
                        "Ordinary Save must report the null field and retain the user's explicit null draft.");
                    await ClickAsync(ReadButton, deadline.Token);
                    CredentialsEnabledInput.IsChecked = false;
                    await ClickAsync(SaveMailButton, deadline.Token, succeeds: false);
                    Require(CredentialsEnabledInput.IsChecked == false
                        && StatusText.Text!.Contains("/Mail/Credentials", StringComparison.Ordinal)
                        && StatusText.Text.Contains("不支持 null", StringComparison.Ordinal)
                        && before.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path)) && _notifications == notifications
                        && CurrentFile.Read<MailSettings>().Credentials is not null,
                        "A null protected object must report its own path and preserve the file, snapshot, notifications and draft.");
                    await ClickAsync(ReadButton, deadline.Token);
                    EditorTabs.SelectedIndex = 2;
                }
                await ClickAsync(PrefixPasswordButton, deadline.Token);
                Require(ReadNewFile().Password == "enc:v1:plain" && !FilePreview.Text!.Contains("enc:v1:plain", StringComparison.Ordinal),
                    "Prefix-like plaintext must still be protected and restored.");
                EditorTabs.SelectedIndex = 0;
                RevealPasswordInput.IsChecked = true;
                Require(PasswordInput.PasswordChar == '\0' && PasswordInput.Text == "enc:v1:plain",
                    "Explicitly revealing the edit field must let beginners inspect the exact prefix-like password.");
                RevealPasswordInput.IsChecked = false;
                Require(PasswordInput.PasswordChar == '●', "The password must return to its masked state.");
                EditorTabs.SelectedIndex = 2;
                await ClickAsync(CredentialsExampleButton, deadline.Token);
                Require(ReadNewFile().Credentials?.AccessToken == ExampleToken,
                    "The object protection scenario must use actual decryption.");
                RequireSafeObservations();

                before = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                notifications = _notifications;
                await ClickAsync(CorruptFileButton, deadline.Token);
                Require(FilePreview.Text == "[" && RestoreFileButton.IsEnabled && !CorruptFileButton.IsEnabled,
                    "Damaged content and the explicit recovery action must be observable.");
                await ClickAsync(ReloadButton, deadline.Token, succeeds: false);
                Require(CurrentFile.Read<MailSettings>().Password == "enc:v1:plain" && _notifications == notifications,
                    "A failed reload must preserve the previous snapshot and not notify.");
                Require(await File.ReadAllTextAsync(CurrentFile.Path, deadline.Token) == "[" && FilePreview.Text == "[",
                    "A failed reload must show the actual damaged file without rewriting it.");
                if (asynchronous) await CaptureAsync(outputPath + ".scenarios.png");
                await ClickAsync(ReadButton, deadline.Token);
                Require(PasswordInput.Text == "enc:v1:plain", "The old snapshot must remain readable while the file is invalid.");
                await ClickAsync(RestoreFileButton, deadline.Token);
                after = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
                Require(before.AsSpan().SequenceEqual(after) && _notifications == notifications + 1 && !RestoreFileButton.IsEnabled,
                    "Recovery must restore the exact original file and then reload through Confio.");

                await VerifySaveUpdateComparisonAsync(outputPath, deadline.Token);
                Workspace.SelectedIndex = 0;
                EditorTabs.SelectedIndex = 0;
                await ClickAsync(ResetMailButton, deadline.Token);
            }

            await ClickAsync(FillExampleButton, deadline.Token);
            HostInput.Text = "configured.di.example.test";
            PortInput.Value = 2465;
            await ClickAsync(SaveMailButton, deadline.Token);
            await ClickAsync(ExternalWriteButton, deadline.Token);
            var beforeDiRead = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
            var notificationsBeforeDi = _notifications;
            var retryBeforeDi = CurrentFile.Read<RetrySettings>();
            HostInput.Text = "draft.di.example.test";
            PortInput.Value = 2466;
            TlsInput.IsChecked = false;
            NotesInput.Text = "DI 保存当前邮件草稿。";
            AttemptsInput.Value = 23;
            Workspace.SelectedIndex = 2;
            await ClickAsync(ContainerReadButton, deadline.Token);
            Require(CurrentFile.Read<MailSettings>().Port == 2465
                && IntegrationOutput.Text!.Contains("Mail 当前配置：configured.di.example.test:2465", StringComparison.Ordinal)
                && IntegrationOutput.Text!.Contains("共享文件：是", StringComparison.Ordinal)
                && IntegrationOutput.Text.Contains("Retry 类型服务已自动注册", StringComparison.Ordinal),
                "DI must read the shared snapshot instead of writing example values or reloading external changes.");
            Require(beforeDiRead.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path))
                && _notifications == notificationsBeforeDi && HostInput.Text == "draft.di.example.test"
                && PortInput.Value == 2466 && NotesInput.Text == "DI 保存当前邮件草稿。" && AttemptsInput.Value == 23,
                "Reading through DI must preserve the exact file, notifications and unsaved drafts.");
            await CaptureAsync(outputPath + ".di-read.png");

            await ClickAsync(ContainerSaveButton, deadline.Token);
            var diSaved = ReadNewFile();
            Require(diSaved.Host == "draft.di.example.test" && diSaved.Port == 2466 && !diSaved.UseTls
                && diSaved.Password == ExamplePassword && diSaved.Credentials?.AccessToken == ExampleToken
                && diSaved.Notes == "DI 保存当前邮件草稿。" && diSaved.Recipients.Length == 2
                && CurrentFile.Read<RetrySettings>() == retryBeforeDi && AttemptsInput.Value == 23,
                "Explicit DI saving must persist the complete Mail draft with protection and preserve Retry and its draft.");
            Require(_notifications == notificationsBeforeDi + 1
                && IntegrationOutput.Text!.Contains("Mail 保存结果：draft.di.example.test:2466", StringComparison.Ordinal)
                && IntegrationOutput.Text.Contains("类型服务直接通知：1 次", StringComparison.Ordinal),
                "DI saving must report its actual saved values and notify the shared file once.");
            RequireSafeObservations();
            await CaptureAsync(outputPath + ".di-save.png");

            var beforeInvalidDiSave = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
            HostInput.Text = " ";
            await ClickAsync(ContainerSaveButton, deadline.Token, succeeds: false);
            Require(beforeInvalidDiSave.AsSpan().SequenceEqual(File.ReadAllBytes(CurrentFile.Path))
                && _notifications == notificationsBeforeDi + 1 && CurrentFile.Read<MailSettings>().Host == diSaved.Host
                && HostInput.Text == " " && PortInput.Value == 2466 && AttemptsInput.Value == 23,
                "Failed DI validation must preserve the file, successful snapshot, notifications and drafts.");
            HostInput.Text = diSaved.Host;
            await ClickAsync(OptionsButton, deadline.Token);
            var rows = OptionsRows.ItemsSource!.Cast<OptionsObservation>().ToArray();
            Require(rows.Length == 4 && rows.All(row => row.Options == 465 && row.ExistingScope == 465),
                "Options and the existing scope must keep their initial cached values.");
            var ports = new[] { 465, 587, 587, 2525 };
            var changes = new[] { 0, 1, 1, 2 };
            for (var i = 0; i < rows.Length; i++)
            {
                Require(rows[i].Configuration == ports[i].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && rows[i].Monitor == ports[i] && rows[i].NewScope == ports[i]
                    && rows[i].Notifications == changes[i], "Each table row must reflect actual cache and notification behavior.");
            }
            Require(NativePreview.Text!.Contains("Mail:Port = 2525", StringComparison.Ordinal)
                && NativePreview.Text.Contains("Mail:Recipients:1 = alerts@example.com", StringComparison.Ordinal)
                && NativePreview.Text.Contains("Mail:Credentials:AccessToken", StringComparison.Ordinal),
                "The native view must show the real flattened model projection.");
            RequireSafeObservations();
            await CaptureAsync(outputPath + ".options.png");
            NativePreview.BringIntoView();
            await CaptureAsync(outputPath + ".native.png");
            await ClickAsync(HostButton, deadline.Token);
            Require(IntegrationOutput.Text!.Contains("模型校验已在加载时通过", StringComparison.Ordinal)
                && IntegrationOutput.Text.Contains("Host 已停止", StringComparison.Ordinal), "Host must start and stop its worker.");

            Workspace.SelectedIndex = 3;
            var sourceMarkers = new[] { "[JsonSerializable", "class ApiCredentials", "record RetrySettings", "SaveAsync(mail, token)",
                "IOptionsSnapshot<MailSettings>", "new ConfigurationFile(AppSettingsContext.Default" };
            for (var i = 0; i < SourceFiles.Length; i++)
            {
                SourceInput.SelectedIndex = i;
                Require(SourcePreview.Text!.Contains(sourceMarkers[i], StringComparison.Ordinal),
                    "The code viewer must load real compiled source resources.");
            }
            SourceInput.SelectedIndex = 0;
            await CaptureAsync(outputPath + ".source.png");
            Workspace.SelectedIndex = 4;
            Require(EventLog.Text!.Contains("[OnChange]", StringComparison.Ordinal)
                && EventLog.Text.Contains("[未完成]", StringComparison.Ordinal), "The journal must retain both notifications and failures.");
            await CaptureAsync(outputPath + ".journal.png");

            var saved = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
            // 用可控信号进入真实保存，验证取消按钮传递令牌且不改变文件。
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = RunOperationAsync("取消验收", async token =>
            {
                await release.Task;
                await CurrentFile.SaveAsync(new MailSettings { Port = 1025 }, token);
            });
            CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            release.SetResult();
            await canceled.WaitAsync(deadline.Token);
            Require(!_lastOperationSucceeded && !_busy && Workspace.IsEnabled, "Cancel must finish the operation and restore the controls.");
            var afterCancellation = await File.ReadAllBytesAsync(CurrentFile.Path, deadline.Token);
            Require(saved.AsSpan().SequenceEqual(afterCancellation), "Canceled saving must preserve the file.");

            Workspace.SelectedIndex = 0;
            EditorTabs.SelectedIndex = 0;
            await ClickAsync(FillExampleButton, deadline.Token);
            await ClickAsync(SaveMailButton, deadline.Token);
            EditorTabs.SelectedIndex = 1;
            AttemptsInput.Value = 5;
            await ClickAsync(SaveRetryButton, deadline.Token);
            await CaptureAsync(outputPath + ".retry.png");
            EditorTabs.SelectedIndex = 0;
            MailEditorScroll.Offset = default;
            await CaptureAsync(outputPath + ".png");
            CollectionExpander.IsExpanded = true;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            NotesInput.BringIntoView();
            await CaptureAsync(outputPath + ".collections.png");
            CollectionExpander.IsExpanded = false;
            CredentialsExpander.IsExpanded = true;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            CredentialTokenInput.BringIntoView();
            await CaptureAsync(outputPath + ".credentials.png");
            CredentialsExpander.IsExpanded = false;
            MailEditorScroll.Offset = default;

            Width = MinWidth;
            Height = MinHeight;
            await CaptureAsync(outputPath + ".compact.png");
            Require(MailEditorScroll.Bounds.Height >= 150 && SnapshotScroll.Bounds.Height >= 180
                && FilePreview.Bounds.Height >= 160,
                $"The minimum window must keep editors and observations usable: editor={MailEditorScroll.Bounds.Height}, model={SnapshotScroll.Bounds.Height}, file={FilePreview.Bounds.Height}.");
            Workspace.SelectedIndex = 2;
            await ClickAsync(OptionsButton, deadline.Token);
            await CaptureAsync(outputPath + ".compact-options.png");
            RequireSafeObservations();

            _verificationStep = "new-session isolation";
            SessionSettings.IsExpanded = true;
            var sessionLabel = SessionSummaryText.Text;
            var lastSessionPath = CurrentFile.Path;
            saved = await File.ReadAllBytesAsync(lastSessionPath, deadline.Token);
            FormatInput.SelectedIndex = (FormatInput.SelectedIndex + 1) % 4;
            ProtectionInput.SelectedIndex = 1 - ProtectionInput.SelectedIndex;
            Require(SessionSummaryText.Text == sessionLabel, "Selecting the next session must not relabel the current session.");
            await ClickAsync(NewSessionButton, deadline.Token);
            Require(CurrentFile.Path != lastSessionPath && !File.Exists(CurrentFile.Path) && _notifications == 0
                && CurrentFile.Read<MailSettings>().Port == 587 && SessionSummaryText.Text != sessionLabel
                && ComparisonDirectoryText.Text == "" && !OpenComparisonDirectoryButton.IsEnabled
                && SaveComparisonResult.Text == "尚未运行" && UpdateComparisonResult.Text == "尚未运行",
                "A new session must reset observations and load defaults at a new path.");
            var original = await File.ReadAllBytesAsync(lastSessionPath, deadline.Token);
            Require(saved.AsSpan().SequenceEqual(original), "Starting another session must preserve the previous file.");

            _verificationStep = "open existing configuration";
            var openedPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "opened" + CurrentExtension);
            using (var existing = new ConfigurationFile(AppSettingsContext.Default, openedPath, _fileOptions))
                existing.Save(new MailSettings { Host = "opened.example.com", Port = 1465, Password = ExamplePassword });
            var openedBytes = await File.ReadAllBytesAsync(openedPath, deadline.Token);
            await RunOperationAsync("已打开已有配置。", token => CreateFileAsync(openedPath, token), Editors.All);
            Require(_lastOperationSucceeded && _openedExisting && HostInput.Text == "opened.example.com"
                && PortInput.Value == 1465 && !GuideExpander.IsExpanded && !CorruptFileButton.IsEnabled
                && openedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(openedPath)),
                "Opening an existing file must load its data without rewriting it or enabling damage demonstrations.");
            Workspace.SelectedIndex = 0;
            await ClickAsync(ExternalWriteButton, deadline.Token, succeeds: false);
            Workspace.SelectedIndex = 2;
            await ClickAsync(OptionsButton, deadline.Token, succeeds: false);
            Require(openedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(openedPath)),
                "Teaching demonstrations must preserve an opened existing file.");
            Workspace.SelectedIndex = 0;
            PortInput.Value = 1466;
            await ClickAsync(SaveMailButton, deadline.Token);
            Require(ReadNewFile().Port == 1466 && ReadNewFile().Password == ExamplePassword,
                "An opened existing file must remain normally editable and protected on explicit save.");
            await CaptureAsync(outputPath + ".opened.png");

            var mode = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";
            await File.WriteAllTextAsync(outputPath + ".verification.txt",
                $"PASS: {mode}; {Path.GetExtension(outputPath)}; guided sync/async workflow and failure retention, readable model summaries, optional JSON details and password visibility, collections and format limits with property paths and reasons, normal saving versus intentional failure demonstrations, protected fields and objects, automatic protection of handwritten plaintext, null/empty and model prefix values, validation, immutable updates, notifications, isolated sections and drafts, observation versus reload, damage/recovery, isolated Save/Update comparison with current-format protection, DI, Options scopes and native view, Host, embedded source, cancellation, new-session isolation, opening existing files and protecting them from teaching demonstrations.\n",
                deadline.Token);
            exitCode = 0;
        }
        catch (Exception exception)
        {
            _operationCancellation?.Cancel();
            await _pendingOperation;
            var failure = $"FAIL: {_verificationStep} ({exception.GetType().Name}); async={UseAsync}; UI={StatusText.Text}\n{_verificationFailure}";
            Console.Error.WriteLine(failure);
            if (_file is not null)
                await File.WriteAllTextAsync(outputPath + ".verification.txt", failure + "\n");
        }
        finally
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(exitCode);
        }
    }

    private MailSettings ReadNewFile()
    {
        using var reopened = new ConfigurationFile(AppSettingsContext.Default, CurrentFile.Path, options: _fileOptions);
        return reopened.Read<MailSettings>();
    }

    private async Task VerifySaveUpdateComparisonAsync(string outputPath, CancellationToken cancellationToken)
    {
        var file = CurrentFile;
        var before = await File.ReadAllBytesAsync(file.Path, cancellationToken);
        var snapshot = file.Read<MailSettings>();
        var notifications = _notifications;
        HostInput.Text = "unsaved.comparison.example.test";
        PortInput.Value = 3465;
        NotesInput.Text = "对照运行期间保留这份草稿。";
        AttemptsInput.Value = 23;
        var selectedFormat = FormatInput.SelectedIndex;
        var selectedProtection = ProtectionInput.SelectedIndex;
        FormatInput.SelectedIndex = (selectedFormat + 1) % 4;
        ProtectionInput.SelectedIndex = 1 - selectedProtection;
        Workspace.SelectedItem = SaveUpdateTab;
        await ClickAsync(SaveUpdateComparisonButton, cancellationToken);
        Require(ComparisonAsyncInput.IsChecked == UseAsyncInput.IsChecked,
            "The comparison and ordinary editor must share the same sync/async selection.");

        var directory = ComparisonDirectoryText.Text!;
        foreach (var saveDraft in new[] { true, false })
        {
            var path = Path.Combine(directory, (saveDraft ? "save" : "update") + CurrentExtension);
            using var reopened = new ConfigurationFile(AppSettingsContext.Default, path, options: _fileOptions);
            var value = await reopened.ReadAsync<MailSettings>(cancellationToken);
            var expectedHost = saveDraft ? "draft.example.com" : "external.example.com";
            var result = saveDraft ? SaveComparisonResult : UpdateComparisonResult;
            var output = saveDraft ? SaveComparisonOutput : UpdateComparisonOutput;
            Require(value.Host == expectedHost && value.Port == 465 && value.Password == ExamplePassword
                && result.Text == $"{expectedHost}:465",
                "Comparison results must match real files: Save replaces the old draft, Update preserves the external host.");
            Require(output.Text!.Contains("external.example.com:587", StringComparison.Ordinal)
                && output.Text.Contains(UseAsync ? "await config." : "4. config.", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains(ExamplePassword, StringComparison.Ordinal),
                "The comparison must show actual intermediate values and the selected API while protecting stored secrets.");
        }
        Require(ReferenceEquals(CurrentFile, file) && before.AsSpan().SequenceEqual(File.ReadAllBytes(file.Path))
            && file.Read<MailSettings>().Host == snapshot.Host && file.Read<MailSettings>().Port == snapshot.Port
            && _notifications == notifications && HostInput.Text == "unsaved.comparison.example.test"
            && PortInput.Value == 3465 && NotesInput.Text == "对照运行期间保留这份草稿。" && AttemptsInput.Value == 23,
            "Running the comparison must preserve the active file, snapshot, notifications and both editor drafts.");
        Require(OpenComparisonDirectoryButton.IsEffectivelyEnabled && Directory.Exists(directory),
            "A successful comparison must expose its actual file directory.");
        FormatInput.SelectedIndex = selectedFormat;
        ProtectionInput.SelectedIndex = selectedProtection;
        RequireSafeObservations();
        if (UseAsync)
        {
            await CaptureAsync(outputPath + ".save-update.png");
            var width = Width;
            var height = Height;
            Width = MinWidth;
            Height = MinHeight;
            await CaptureAsync(outputPath + ".save-update-compact.png");
            Require(SaveComparisonOutput.Bounds.Height >= 140 && UpdateComparisonOutput.Bounds.Height >= 140,
                $"Both comparison traces must remain usable at the minimum window size: Save={SaveComparisonOutput.Bounds.Height}, Update={UpdateComparisonOutput.Bounds.Height}.");
            Width = width;
            Height = height;
        }
    }

    private void RequireSafeObservations()
    {
        var visible = SnapshotPreview.Text + ReadPasswordText.Text + ReadCredentialsText.Text +
            FilePreview.Text + NativePreview.Text + IntegrationOutput.Text + EventLog.Text +
            SaveComparisonOutput.Text + UpdateComparisonOutput.Text + SaveComparisonResult.Text + UpdateComparisonResult.Text;
        Require(new[] { ExamplePassword, ExampleUserName, ExampleToken }.All(secret => !visible.Contains(secret, StringComparison.Ordinal)),
            "Snapshots, file preview, native projection and logs must not expose protected values.");
    }

    private async Task ClickAsync(Button button, CancellationToken cancellationToken, bool succeeds = true)
    {
        _verificationStep = button.Name ?? "unnamed button";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        button.BringIntoView();
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, $"The button {button.Name} must be available to a user.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await _pendingOperation.WaitAsync(cancellationToken);
        if (!ReferenceEquals(button, FillExampleButton))
            Require(_lastOperationSucceeded == succeeds, $"Unexpected outcome from {button.Name}.");
    }

    private async Task CaptureAsync(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        bitmap.Render(this);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private void Require(bool condition, string behavior)
    {
        if (condition) return;
        _verificationStep = behavior;
        throw new InvalidOperationException("Sample verification failed.");
    }
}

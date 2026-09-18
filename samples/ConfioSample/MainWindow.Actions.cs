using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Confio;

namespace ConfioSample;

// 窗口按钮直接调用正式 API。RunOperationAsync 只管理界面忙碌状态、结果显示与取消。
public partial class MainWindow
{
    private async void OpenFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开配置文件",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("配置文件") { Patterns = new[] { "*.json", "*.yaml", "*.yml", "*.toml", "*.ini" } } }
        });
        if (selected.Count == 0 || selected[0].TryGetLocalPath() is not { } path) return;
        await RunOperationAsync("已打开现有配置；加载时不自动写回，修改后可显式保存。",
            token => CreateFileAsync(path, token), Editors.All);
    }

    private async void OpenDirectoryClick(object? sender, RoutedEventArgs e) =>
        await OpenDirectoryAsync(Path.GetDirectoryName(CurrentFile.Path)!);

    private async void OpenComparisonDirectoryClick(object? sender, RoutedEventArgs e) =>
        await OpenDirectoryAsync(ComparisonDirectoryText.Text!);

    private async Task OpenDirectoryAsync(string path)
    {
        try
        {
            var directory = Directory.CreateDirectory(path);
            var opened = await Launcher.LaunchDirectoryInfoAsync(directory);
            ShowStatus(opened ? "已打开配置文件所在目录。"
                : "系统未能打开目录，请复制路径手动打开。", !opened);
        }
        catch (Exception exception)
        {
            ShowStatus($"打开目录失败，请检查路径、权限和系统文件管理器。（{exception.GetType().Name}）", true);
        }
    }

    private async void NewSessionClick(object? sender, RoutedEventArgs e) =>
        await NewSessionAsync();

    private Task NewSessionAsync() =>
        RunOperationAsync("新的演示已就绪；之前的文件已保留。",
            token => CreateFileAsync(NewPath(SelectedExtension), token), Editors.All);

    private async void FillExampleClick(object? sender, RoutedEventArgs e) => await FillExampleAsync();

    private Task FillExampleAsync() =>
        RunOperationAsync("只改了左边的表单，尚未保存；中间程序读到的值和右边文件都没有改变。", _ =>
        {
            HostInput.Text = "smtp.example.com";
            PortInput.Text = "465";
            TlsInput.IsChecked = true;
            NullPasswordInput.IsChecked = false;
            PasswordInput.Text = ExamplePassword;
            RecipientsInput.Text = "ops@example.com\nalerts@example.com";
            NotesInput.Text = "这是演示数据。四种格式都能保存这条中文备注。";
            CredentialsEnabledInput.IsChecked = true;
            CredentialUserInput.Text = ExampleUserName;
            CredentialTokenInput.Text = ExampleToken;
            return Task.CompletedTask;
        });

    private async void ReadClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已用当前快照替换所有表单草稿；没有重读磁盘。", _ => Task.CompletedTask, Editors.All);

    private async void RefreshClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("观察区已刷新；保留表单草稿，没有调用 Reload。", _ => Task.CompletedTask);

    private async void SaveMailClick(object? sender, RoutedEventArgs e) =>
        await SaveMailAsync();

    private Task SaveMailAsync() =>
        RunOperationAsync("邮件配置已保存：程序开始使用新值，需要保护的内容已加密。重试配置及其草稿保留。", async token =>
        {
            var mail = ReadMailDraft();
            if (UseAsync) await CurrentFile.SaveAsync(mail, token);
            else CurrentFile.Save(mail);
        }, Editors.Mail);

    private async void SaveUpdateComparisonClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("对照已完成：Save 保存完整草稿，Update 只修改端口。可打开对照目录查看两个文件。", async token =>
        {
            var extension = CurrentExtension;
            var directory = Path.GetDirectoryName(NewPath(extension))!;
            ComparisonDirectoryText.Text = directory;
            OpenComparisonDirectoryButton.IsEnabled = false;
            SaveComparisonOutput.Text = UpdateComparisonOutput.Text = "等待运行…";
            SaveComparisonResult.Text = UpdateComparisonResult.Text = "尚未完成";
            foreach (var saveDraft in new[] { true, false })
            {
                var path = Path.Combine(directory, (saveDraft ? "save" : "update") + extension);
                var output = saveDraft ? SaveComparisonOutput : UpdateComparisonOutput;
                var result = saveDraft ? SaveComparisonResult : UpdateComparisonResult;
                using var config = new ConfigurationFile(AppSettingsContext.Default, path, options: _fileOptions);
                var initial = new MailSettings { Host = "draft.example.com", Port = 587, Password = ExamplePassword };
                if (UseAsync) await config.SaveAsync(initial, token);
                else config.Save(initial);
                OpenComparisonDirectoryButton.IsEnabled = true;

                var draft = UseAsync ? await config.ReadAsync<MailSettings>(token) : config.Read<MailSettings>();
                output.Text = $"1. 读取草稿：{draft.Host}:{draft.Port}\n";
                draft.Port = 465;
                output.Text += $"2. 草稿改端口：{draft.Host}:{draft.Port}\n";

                using var external = new ConfigurationFile(AppSettingsContext.Default, path, options: _fileOptions);
                static void ChangeHost(MailSettings mail) => mail.Host = "external.example.com";
                if (UseAsync) await external.UpdateAsync<MailSettings>(ChangeHost, token);
                else external.Update<MailSettings>(ChangeHost);
                var latest = UseAsync ? await external.ReadAsync<MailSettings>(token) : external.Read<MailSettings>();
                output.Text += $"3. 外部改主机：{latest.Host}:{latest.Port}\n";

                if (saveDraft)
                {
                    output.Text += UseAsync ? "\n4. await config.SaveAsync(draft);" : "\n4. config.Save(draft);";
                    if (UseAsync) await config.SaveAsync(draft, token);
                    else config.Save(draft);
                }
                else
                {
                    output.Text += UseAsync
                        ? "\n4. await config.UpdateAsync<MailSettings>(\n       mail => mail.Port = draft.Port);"
                        : "\n4. config.Update<MailSettings>(\n       mail => mail.Port = draft.Port);";
                    if (UseAsync) await config.UpdateAsync<MailSettings>(mail => mail.Port = draft.Port, token);
                    else config.Update<MailSettings>(mail => mail.Port = draft.Port);
                }

                using var reopened = new ConfigurationFile(AppSettingsContext.Default, path, options: _fileOptions);
                var saved = UseAsync ? await reopened.ReadAsync<MailSettings>(token) : reopened.Read<MailSettings>();
                result.Text = $"{saved.Host}:{saved.Port}";
            }
        });

    private MailSettings ReadMailDraft() => new()
    {
        Host = (HostInput.Text ?? "").Trim(),
        Port = ReadInteger(PortInput),
        UseTls = TlsInput.IsChecked == true,
        Password = NullPasswordInput.IsChecked == true ? null : PasswordInput.Text ?? "",
        Recipients = (RecipientsInput.Text ?? "").Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Notes = NotesInput.Text ?? "",
        Credentials = CredentialsEnabledInput.IsChecked == true ? new ApiCredentials
        {
            UserName = CredentialUserInput.Text ?? "",
            AccessToken = CredentialTokenInput.Text ?? ""
        } : null
    };

    private async void SaveRetryClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("重试配置已保存；邮件配置及其未保存的草稿保留。", async token =>
        {
            var retry = new RetrySettings { MaxAttempts = ReadInteger(AttemptsInput), DelaySeconds = ReadInteger(DelayInput) };
            if (UseAsync) await CurrentFile.SaveAsync(retry, token);
            else CurrentFile.Save(retry);
        }, Editors.Retry);

    private async void UpdateRetryClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已基于最新文件值通过 with 增加一次重试；未采用表单草稿。", async token =>
        {
            static RetrySettings Increment(RetrySettings retry) =>
                retry with { MaxAttempts = checked(retry.MaxAttempts + 1) };
            if (UseAsync) await CurrentFile.UpdateAsync<RetrySettings>(Increment, token);
            else CurrentFile.Update<RetrySettings>(Increment);
        }, Editors.Retry);

    private async void ResetMailClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("邮件配置已恢复模型默认值；重试配置及其草稿保留。", async token =>
        {
            if (UseAsync) await CurrentFile.ResetAsync<MailSettings>(token);
            else CurrentFile.Reset<MailSettings>();
        }, Editors.Mail);

    private async void ResetRetryClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("重试配置已恢复模型默认值；邮件配置及其草稿保留。", async token =>
        {
            if (UseAsync) await CurrentFile.ResetAsync<RetrySettings>(token);
            else CurrentFile.Reset<RetrySettings>();
        }, Editors.Retry);

    private async void ExternalWriteClick(object? sender, RoutedEventArgs e) =>
        await ExternalWriteAsync();

    private Task ExternalWriteAsync() =>
        RunDemoAsync("另一个程序已修改右边的文件；中间仍是旧值。点击“重新加载文件”后，当前程序才会采用这次修改。", async token =>
        {
            using var external = new ConfigurationFile(AppSettingsContext.Default, CurrentFile.Path, options: _fileOptions);
            static void ChangePort(MailSettings mail) => mail.Port = mail.Port == 2525 ? 465 : 2525;
            if (UseAsync) await external.UpdateAsync<MailSettings>(ChangePort, token);
            else external.Update<MailSettings>(ChangePort);
        });

    private async void ReloadClick(object? sender, RoutedEventArgs e) =>
        await ReloadAsync();

    private Task ReloadAsync() =>
        RunOperationAsync(_openedExisting
            ? "已重新加载已有文件：程序和表单已更新，文件内容保留；点击保存时会保护敏感字段。"
            : "已重新加载文件：程序和表单已更新；需要保护的明文会自动加密写回，已有密文无需重写。", async token =>
        {
            if (UseAsync) await CurrentFile.ReloadAsync(token);
            else CurrentFile.Reload();
        }, Editors.All);

    private async void PlaintextFileClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已新建手写明文文件；旧文件保留。程序仍读到默认值，点击“2 加载文件，观察自动加密”继续。", async token =>
        {
            await CreateFileAsync(NewPath(SelectedExtension), token);
            // 仅创建新的虚构样例，模拟用户手写文件；文件保护全部由后续 Reload 完成。
            var content = CurrentExtension switch
            {
                ".json" => $$"""
                    {
                      "Mail": {
                        "Host": "smtp.example.com",
                        "Port": 465,
                        "Password": "{{ExamplePassword}}",
                        "Credentials": { "UserName": "{{ExampleUserName}}", "AccessToken": "{{ExampleToken}}" }
                      }
                    }
                    """,
                ".toml" => $"""
                    [Mail]
                    Host = "smtp.example.com"
                    Port = 465
                    Password = "{ExamplePassword}"
                    [Mail.Credentials]
                    UserName = "{ExampleUserName}"
                    AccessToken = "{ExampleToken}"
                    """,
                ".ini" => $"""
                    [Mail]
                    Host=smtp.example.com
                    Port=465
                    Password={ExamplePassword}
                    Credentials:UserName={ExampleUserName}
                    Credentials:AccessToken={ExampleToken}
                    """,
                _ => $"""
                    Mail:
                      Host: smtp.example.com
                      Port: 465
                      Password: {ExamplePassword}
                      Credentials:
                        UserName: {ExampleUserName}
                        AccessToken: {ExampleToken}
                    """
            };
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentFile.Path)!);
            if (UseAsync) await File.WriteAllTextAsync(CurrentFile.Path, content, token);
            else File.WriteAllText(CurrentFile.Path, content);
        }, Editors.All);

    private async void NullPasswordClick(object? sender, RoutedEventArgs e) => await SavePasswordAsync(null);

    private async void EmptyPasswordClick(object? sender, RoutedEventArgs e) => await SavePasswordAsync("");

    private async void PrefixPasswordClick(object? sender, RoutedEventArgs e) => await SavePasswordAsync("enc:v1:plain");

    private Task SavePasswordAsync(string? password) =>
        RunDemoAsync(password is null ? "Password 已保存为 null；保留空值，不产生密码密文。"
            : password.Length == 0 ? "Password 已保存为空字符串；不会变成 null。"
            : "模型 API 已保存前缀文本并加密，读取恢复完整原值。", async token =>
        {
            if (UseAsync) await CurrentFile.UpdateAsync<MailSettings>(mail => mail.Password = password, token);
            else CurrentFile.Update<MailSettings>(mail => mail.Password = password);
        }, Editors.Mail);

    private async void CredentialsExampleClick(object? sender, RoutedEventArgs e) =>
        await RunDemoAsync("Credentials 已整体保护；快照中恢复为对象，文件中是一份密文。", async token =>
        {
            static void SetCredentials(MailSettings mail) =>
                mail.Credentials = new ApiCredentials { UserName = ExampleUserName, AccessToken = ExampleToken };
            if (UseAsync) await CurrentFile.UpdateAsync<MailSettings>(SetCredentials, token);
            else CurrentFile.Update<MailSettings>(SetCredentials);
        }, Editors.Mail);

    private async void InvalidMailClick(object? sender, RoutedEventArgs e) =>
        await RunDemoAsync("空白主机已保存。", async token =>
        {
            var candidate = UseAsync ? await CurrentFile.ReadAsync<MailSettings>(token) : CurrentFile.Read<MailSettings>();
            candidate.Host = " ";
            if (UseAsync) await CurrentFile.SaveAsync(candidate, token);
            else CurrentFile.Save(candidate);
        });

    private async void CorruptFileClick(object? sender, RoutedEventArgs e) =>
        await RunDemoAsync("已将演示文件写为损坏内容；旧配置仍可读取。请点击“重新加载文件”观察失败。", async token =>
        {
            // 仅操作本窗口新建的演示文件；保留原文供显式恢复，不是运行库的自动备份能力。
            _fileBeforeDamage = UseAsync
                ? await File.ReadAllBytesAsync(CurrentFile.Path, token)
                : File.ReadAllBytes(CurrentFile.Path);
            if (UseAsync) await File.WriteAllTextAsync(CurrentFile.Path, "[", token);
            else File.WriteAllText(CurrentFile.Path, "[");
        });

    private async void RestoreFileClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已恢复损坏前的演示原文并重载；文件与快照重新一致。", async token =>
        {
            var original = _fileBeforeDamage ?? throw new InvalidOperationException("There is no damaged sample file to restore.");
            if (UseAsync) await File.WriteAllBytesAsync(CurrentFile.Path, original, token);
            else File.WriteAllBytes(CurrentFile.Path, original);
            _fileBeforeDamage = null;
            if (UseAsync) await CurrentFile.ReloadAsync(token);
            else CurrentFile.Reload();
        }, Editors.All);

    private async void ContainerReadClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已通过 DI 读取当前配置；配置文件和表单草稿保留，没有重新加载磁盘。", async token =>
        {
            PrepareIntegration();
            IntegrationOutput.Text = await IntegrationExamples.ReadContainerAsync(CurrentFile, token);
        });

    private async void ContainerSaveClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("已通过 DI 保存邮件表单；程序和文件已更新，重试配置及其草稿保留。", async token =>
        {
            PrepareIntegration();
            IntegrationOutput.Text = await IntegrationExamples.SaveContainerAsync(CurrentFile, ReadMailDraft(), token);
        }, Editors.Mail);

    private async void OptionsClick(object? sender, RoutedEventArgs e) =>
        await RunDemoAsync("Options 对照已完成；每行均为实际操作后的读取值。", async token =>
        {
            PrepareIntegration();
            var result = await IntegrationExamples.RunOptionsAsync(CurrentFile, _fileOptions, token);
            OptionsRows.ItemsSource = result.Rows;
            OptionsTable.IsVisible = true;
            NativePreview.Text = result.NativeView;
            NativePanel.IsVisible = true;
            IntegrationOutput.Text =
                "IOptions 与原作用域 Snapshot 保留首次取值 465。\n" +
                "保存、重载使 Configuration / Monitor 更新；新作用域首次取值时使用当前快照。\n" +
                "外部写入本身不通知，也不刷新这些读取入口。\n\n" +
                "IConfigurationRoot.Reload() 已重新读取文件，下方展示更新后的读取投影。";
        }, Editors.Mail);

    private async void HostClick(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("Host 已完成加载、启动与停止。", async token =>
        {
            PrepareIntegration();
            IntegrationOutput.Text = await IntegrationExamples.RunHostAsync(CurrentFile, token);
        });

    private Task RunDemoAsync(string success, Func<CancellationToken, Task> action, Editors editors = Editors.None)
    {
        if (!_openedExisting) return RunOperationAsync(success, action, editors);
        _lastOperationSucceeded = false;
        ShowStatus("此演示会改写文件，请先点击“新建演示”。已有配置可继续读取、编辑和保存。", true);
        return Task.CompletedTask;
    }
}

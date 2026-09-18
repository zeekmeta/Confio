using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ConfioSample;

public partial class MainWindow
{
    private int _guideStep;

    // 引导和自由操作调用相同的窗口动作，不维护另一套配置状态或演示读写实现。
    private async void GuideNextClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _pendingOperation = AdvanceGuideAsync();
        await _pendingOperation;
    }

    private async Task AdvanceGuideAsync()
    {
        if (_guideStep == 4)
        {
            GuideExpander.IsExpanded = false;
            return;
        }
        var step = _guideStep;
        EditorTabs.SelectedIndex = 0;
        await (step switch
        {
            0 => FillExampleAsync(),
            1 => SaveMailAsync(),
            2 => ExternalWriteAsync(),
            _ => ReloadAsync()
        });
        if (_lastOperationSucceeded) _guideStep = step + 1;
        RefreshGuide();
    }

    private void GuideSourceClick(object? sender, RoutedEventArgs e)
    {
        Workspace.SelectedIndex = 3;
        SourceInput.SelectedIndex = 0;
    }

    private void ApiModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized) RefreshGuide();
    }

    private void EditorTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized && EditorTabs.SelectedIndex != 0) GuideExpander.IsExpanded = false;
    }

    private void RevealPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized) PasswordInput.PasswordChar = RevealPasswordInput.IsChecked == true ? '\0' : '●';
    }

    private void RefreshGuide()
    {
        var read = UseAsync ? "await config.ReadAsync<MailSettings>()" : "config.Read<MailSettings>()";
        var save = UseAsync ? "await config.SaveAsync(mail);" : "config.Save(mail);";
        var reload = UseAsync ? "await config.ReloadAsync();" : "config.Reload();";
        (string Title, string Explanation, string Code, string Button) content = _guideStep switch
        {
            0 => (
                "从这里开始 · 1 / 4  读取配置，再修改副本",
                "缺少文件时，程序会读到模型里的默认值。点击右边按钮填入示例，比较左边表单与中间的端口。",
                $"using var config = new ConfigurationFile(AppSettingsContext.Default, path: path);\nvar mail = {read};",
                "1  填入示例值"),
            1 => (
                "2 / 4  表单改了，还要保存才会生效",
                "左边是你准备保存的值，中间是程序正在用的值。保存后，程序采用新值，右边文件里的密码会变成密文。",
                $"mail.Port = 465;  // 示例修改\n{save}",
                "2  保存邮件配置"),
            2 => (
                "3 / 4  模拟另一个程序修改同一个文件",
                "接下来由另一个配置实例修改端口。观察右边文件会改变，而中间当前程序读到的端口暂时不变。",
                UseAsync ? "await other.UpdateAsync<MailSettings>(mail => mail.Port = newPort);"
                    : "other.Update<MailSettings>(mail => mail.Port = newPort);",
                "3  模拟外部修改"),
            3 => (
                "4 / 4  重新加载，才会采用外部修改",
                "右边文件已变，中间仍保留旧值。重新加载后，程序与表单会采用文件中的新值，未保存的编辑会被丢弃。",
                $"{reload}\nvar mail = {read};",
                "4  重新加载文件"),
            _ => (
                "已经走通：读取 → 编辑 → 保存；外部修改 → 重新加载",
                "点击右边按钮收起引导，再试试“重试”和“加密与失败演示”。完整模型可在中间展开；顶部“新建演示与设置”可重新开始。",
                $"{save}  // 保存本程序的编辑\n{reload}  // 采用外部文件的修改",
                "继续自由探索")
        };
        GuideTitleText.Text = content.Title;
        GuideExplanationText.Text = content.Explanation;
        GuideCodeText.Text = content.Code;
        GuideNextButton.Content = content.Button;
    }
}

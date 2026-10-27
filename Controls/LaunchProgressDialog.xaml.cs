using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>
/// 启动进度对话框。内容复用 <see cref="LaunchSteps"/>，观感和安装进度窗口一致。
/// 点「后台运行」只是关掉对话框，启动过程留在 GameSession 里继续跑。
/// </summary>
public sealed partial class LaunchProgressDialog : ContentDialog
{
    public LaunchProgressDialog(LaunchJob job)
    {
        InitializeComponent();

        Title = "启动游戏";
        PrimaryButtonText = "后台运行";
        DefaultButton = ContentDialogButton.Primary;

        Steps.Job = job;
    }
}

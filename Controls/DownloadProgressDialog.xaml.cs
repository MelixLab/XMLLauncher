using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>
/// 安装进度对话框。内容复用 <see cref="DownloadSteps"/>（固定五步 + 进度条 + 当前文件）。
/// 点「后台运行」关掉不会中断安装 —— 任务留在 AppState.Downloads 里继续跑。
/// </summary>
public sealed partial class DownloadProgressDialog : ContentDialog
{
    private readonly DownloadJob _job;
    private DispatcherQueueTimer? _timer;

    public DownloadProgressDialog(DownloadJob job)
    {
        InitializeComponent();
        _job = job;

        Title = "安装新游戏";
        PrimaryButtonText = "后台运行";
        SecondaryButtonText = "取消安装";
        DefaultButton = ContentDialogButton.Primary;

        Steps.Job = job;

        Opened += (_, _) =>
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(400);
            _timer.Tick += (_, _) =>
            {
                if (_job.IsFinished || _job.IsFailed)
                {
                    _timer?.Stop();
                    PrimaryButtonText = "关闭";
                }
            };
            _timer.Start();
        };

        Closed += (_, args) =>
        {
            _timer?.Stop();

            // 「取消安装」真的会把任务停掉；「后台运行」只是关掉窗口
            if (args.Result == ContentDialogResult.Secondary) _job.Cancel();
        };
    }
}

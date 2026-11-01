using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>
/// 下载页和安装弹窗共用的阶段显示。
///
/// 排版上刻意「少写一点」：状态和阶段合成一行，指标合成一行，
/// 单文件任务不显示「文件 1/1」，结束的任务不留进度条空白。
/// </summary>
public sealed partial class DownloadSteps : UserControl
{
    public static readonly DependencyProperty JobProperty =
        DependencyProperty.Register(nameof(Job), typeof(DownloadJob), typeof(DownloadSteps),
            new PropertyMetadata(null, OnJobChanged));

    private DownloadJob? _job;
    private bool _isLoaded;
    private int _refreshQueued;

    public DownloadSteps()
    {
        InitializeComponent();
        Loaded += Steps_Loaded;
        Unloaded += Steps_Unloaded;
        ActualThemeChanged += (_, _) => Refresh();
    }

    public DownloadJob? Job
    {
        get => (DownloadJob?)GetValue(JobProperty);
        set => SetValue(JobProperty, value);
    }

    private static void OnJobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DownloadSteps)d;
        view.Unsubscribe();
        view._job = e.NewValue as DownloadJob;
        if (view._isLoaded) view.Subscribe();
        view.Refresh();
    }

    private void Steps_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;
        Subscribe();
        Refresh();
        FontService.Apply(this);
    }

    private void Steps_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_job is not null) _job.PropertyChanged += OnJobPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_job is not null) _job.PropertyChanged -= OnJobPropertyChanged;
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 多个指标会连续通知，只为当前可见组件排一次 UI 刷新。
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_isLoaded) Refresh();
        })) Interlocked.Exchange(ref _refreshQueued, 0);
    }

    private void Refresh()
    {
        var job = _job;
        ContentRoot.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        if (job is null) return;

        bool canceled = job.IsCanceled;
        bool cancelling = job.IsCancelling;
        bool failed = job.IsFinished && job.IsFailed;
        bool running = job.IsRunning;
        bool indeterminate = running && job.IsIndeterminate;

        // ---- 状态：一个图标 + 一句话 ----
        StatusIcon.Glyph = canceled || cancelling ? "\uE711"
            : running ? "\uE896"
            : failed ? "\uE783"
            : "\uE73E";

        StatusIcon.Foreground = ThemeService.Brush(
            canceled || cancelling ? "SecondaryTextBrush"
            : running ? "BrandBrush"
            : failed ? "SecurityRedBrush"
            : "SecurityGreenBrush");

        var status = cancelling ? "正在取消"
            : running ? "下载中"
            : canceled ? "已取消"
            : failed ? "下载失败"
            : "已完成";

        // 阶段只在运行中有意义；结束后再说「下载」就是废话
        var stage = job.Stage?.Trim() ?? "";
        bool showStage = running && !cancelling && stage.Length > 0
                         && !string.Equals(stage, status, StringComparison.Ordinal);

        StatusText.Text = showStage ? $"{status} · {stage}" : status;
        StatusText.Foreground = ThemeService.Brush(
            canceled || cancelling ? "SecondaryTextBrush" : "PrimaryTextBrush");

        // ---- 进度条：只在跑的时候占位置 ----
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = indeterminate;
        Progress.Value = double.IsFinite(job.Fraction) ? Math.Clamp(job.Fraction * 100, 0, 100) : 0;

        // ---- 指标：能拼成一句就拼一句 ----
        var parts = new System.Collections.Generic.List<string>();

        if (running && !cancelling && !indeterminate)
        {
            if (job.Speed > 1) parts.Add(job.SpeedText);

            if (job.TotalBytes > 0)
            {
                parts.Add($"{GameInstaller.FormatSize(job.DoneBytes)} / {GameInstaller.FormatSize(job.TotalBytes)}");
                parts.Add(job.PercentText);

                // 剩余时间只在跑得动的时候给
                if (job.Speed >= 1024 && job.DoneBytes < job.TotalBytes)
                    parts.Add(job.EtaText);
            }

            // 单文件任务不必说「文件 1/1」
            if (job.TotalFiles > 1)
                parts.Add($"文件 {job.FilesText}");
        }
        else if (indeterminate && running)
        {
            parts.Add("正在准备…");
        }
        else if (!running && !canceled && !failed)
        {
            // 成功结束：报一下最终体积，比空着强
            if (job.TotalBytes > 0)
                parts.Add(GameInstaller.FormatSize(job.TotalBytes));
            if (job.TotalFiles > 1)
                parts.Add($"{job.TotalFiles} 个文件");
        }

        MetricsText.Text = string.Join(" · ", parts);
        MetricsText.Foreground = ThemeService.Brush("SecondaryTextBrush");
        MetricsText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 当前文件：和标题重复就不显示 ----
        var file = job.CurrentFile?.Trim() ?? "";
        var title = job.Title?.Trim() ?? "";
        bool showFile = file.Length > 0
                        && running
                        && !string.Equals(file, title, StringComparison.OrdinalIgnoreCase)
                        && !file.StartsWith(title, StringComparison.OrdinalIgnoreCase);

        FileText.Text = file;
        FileText.Visibility = showFile ? Visibility.Visible : Visibility.Collapsed;
        FileText.Foreground = ThemeService.Brush("TertiaryTextBrush");
        ToolTipService.SetToolTip(FileText, file);

        // ---- 失败原因 ----
        ErrorPanel.Visibility = failed && job.HasError ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = failed ? job.Error : "";
    }
}

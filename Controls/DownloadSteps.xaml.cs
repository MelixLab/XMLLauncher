using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>下载页和安装弹窗共用的真实阶段、进度指标与结束状态。</summary>
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
        bool indeterminate = job.IsRunning && job.IsIndeterminate;

        StatusIcon.Glyph = canceled || cancelling ? "\uE711" : job.IsRunning ? "\uE896" : failed ? "\uE783" : "\uE73E";
        StatusIcon.Foreground = ThemeService.Brush(canceled || cancelling ? "SecondaryTextBrush" :
            job.IsRunning ? "BrandBrush" : failed ? "SecurityRedBrush" : "SecurityGreenBrush");
        StatusText.Text = cancelling ? "正在取消" : job.IsRunning ? "正在进行" : canceled ? "已取消" : failed ? "下载失败" : "已完成";
        StageText.Text = string.IsNullOrWhiteSpace(job.Stage) ? "准备" : job.Stage;

        Progress.Visibility = job.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = indeterminate;
        Progress.Value = double.IsFinite(job.Fraction) ? Math.Clamp(job.Fraction * 100, 0, 100) : 0;
        SpeedText.Visibility = job.IsRunning && !cancelling && !indeterminate ? Visibility.Visible : Visibility.Collapsed;
        SpeedText.Text = $"速度 {job.SpeedText}";
        SizeText.Visibility = job.TotalBytes > 0 && !indeterminate ? Visibility.Visible : Visibility.Collapsed;
        SizeText.Text = job.SizeText;
        PercentText.Visibility = job.IsRunning && !cancelling ? Visibility.Visible : Visibility.Collapsed;
        PercentText.Text = indeterminate ? "正在处理…" : job.PercentText;
        FilesText.Visibility = job.TotalFiles > 0 && !indeterminate ? Visibility.Visible : Visibility.Collapsed;
        FilesText.Text = $"文件 {job.FilesText}";
        FileText.Text = job.CurrentFile;
        FileText.Visibility = !string.IsNullOrWhiteSpace(job.CurrentFile) ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(FileText, job.CurrentFile);
        ErrorPanel.Visibility = failed && job.HasError ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = failed ? job.Error : "";
    }
}

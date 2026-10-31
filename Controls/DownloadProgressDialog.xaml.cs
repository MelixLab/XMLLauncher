using System;
using System.ComponentModel;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>后台运行只关闭对话框；取消操作沿用任务现有的取消令牌。</summary>
public sealed partial class DownloadProgressDialog : ContentDialog
{
    private readonly DownloadJob _job;
    private XamlRoot? _layoutRoot;
    private bool _isOpen;
    private int _refreshQueued;

    public DownloadProgressDialog(DownloadJob job)
    {
        InitializeComponent();
        _job = job;

        Title = "下载与安装";
        PrimaryButtonText = "后台运行";
        SecondaryButtonText = "取消安装";
        DefaultButton = ContentDialogButton.Primary;
        JobTitle.Text = job.Title;
        Steps.Job = job;
        FontService.ApplyToDialog(this);

        Opened += Dialog_Opened;
        Closed += Dialog_Closed;
        Unloaded += (_, _) => Detach();
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Detach();
        _isOpen = true;
        _job.PropertyChanged += OnJobPropertyChanged;
        _layoutRoot = XamlRoot;
        if (_layoutRoot is not null) _layoutRoot.Changed += OnXamlRootChanged;
        UpdateBounds();
        RefreshButtons();
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Detach();
        if (args.Result == ContentDialogResult.Secondary && _job.IsRunning) _job.Cancel();
    }

    private void Detach()
    {
        _isOpen = false;
        _job.PropertyChanged -= OnJobPropertyChanged;
        if (_layoutRoot is not null) _layoutRoot.Changed -= OnXamlRootChanged;
        _layoutRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

    private void UpdateBounds()
    {
        if (_layoutRoot is null) return;
        ContentScroller.Width = Math.Max(0, Math.Min(470, _layoutRoot.Size.Width - 96));
        ContentScroller.MaxHeight = Math.Max(0, _layoutRoot.Size.Height - 200);
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DownloadJob.IsRunning) or nameof(DownloadJob.IsFinished))) return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_isOpen) RefreshButtons();
        })) Interlocked.Exchange(ref _refreshQueued, 0);
    }

    private void RefreshButtons()
    {
        PrimaryButtonText = _job.IsFinished ? "关闭" : "后台运行";
        IsSecondaryButtonEnabled = _job.IsRunning && !_job.Cts.IsCancellationRequested;
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>
/// 启动进度面板：固定五步 + 进度条 + 用时 / 状态 + 当前细节。
/// 骨架与 <see cref="DownloadSteps"/> 一致，保证两个进度窗口长得一样。
/// </summary>
public sealed partial class LaunchSteps : UserControl
{
    public static readonly DependencyProperty JobProperty =
        DependencyProperty.Register(nameof(Job), typeof(LaunchJob), typeof(LaunchSteps),
            new PropertyMetadata(null, OnJobChanged));

    private sealed class Row
    {
        public required FontIcon Icon { get; init; }
        public required TextBlock Label { get; init; }
    }

    private readonly List<Row> _rows = new();
    private LaunchJob? _job;
    private int _furthestStep; // 只增不减，避免阶段文案来回跳
    private DispatcherQueueTimer? _ticker;

    public LaunchSteps()
    {
        InitializeComponent();
        BuildRows();

        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(500);
        _ticker.Tick += (_, _) => RefreshElapsed();
        _ticker.Start();
    }

    /// <summary>当前要跟踪的启动任务。</summary>
    public LaunchJob? Job
    {
        get => (LaunchJob?)GetValue(JobProperty);
        set => SetValue(JobProperty, value);
    }

    private static void OnJobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LaunchSteps)d;

        if (view._job is not null) view._job.PropertyChanged -= view.OnJobPropertyChanged;

        view._job = e.NewValue as LaunchJob;
        view._furthestStep = 0;

        if (view._job is not null) view._job.PropertyChanged += view.OnJobPropertyChanged;

        view.Refresh();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 启动进程在后台线程上报，回 UI 线程再刷。
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void BuildRows()
    {
        var iconFont = (FontFamily)Application.Current.Resources["IconFontFamily"];
        var idle = (Brush)Application.Current.Resources["TertiaryTextBrush"];
        var text = (Brush)Application.Current.Resources["SecondaryTextBrush"];

        foreach (var caption in LaunchJob.Captions)
        {
            var icon = new FontIcon
            {
                FontFamily = iconFont,
                FontSize = 12,
                Glyph = "\uE72A",
                Foreground = idle,
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = caption,
                FontSize = 14,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = text
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);
            StepsHost.Children.Add(row);

            _rows.Add(new Row { Icon = icon, Label = label });
        }
    }

    private void RefreshElapsed()
    {
        var job = _job;
        if (job is null) return;

        ElapsedText.Text = job.ElapsedText;

        if (job.IsFinished)
        {
            _ticker?.Stop();
            _ticker = null;
        }
    }

    private void Refresh()
    {
        var job = _job;
        if (job is null || _rows.Count == 0) return;

        int current = Math.Clamp(Math.Max(_furthestStep, job.Step), 0, _rows.Count - 1);
        if (!job.IsFailed) _furthestStep = current;

        var doneBrush = (Brush)Application.Current.Resources["SecurityGreenBrush"];
        var activeBrush = (Brush)Application.Current.Resources["BrandBrush"];
        var failBrush = (Brush)Application.Current.Resources["SecurityRedBrush"];
        var idleBrush = (Brush)Application.Current.Resources["TertiaryTextBrush"];
        var primary = (Brush)Application.Current.Resources["PrimaryTextBrush"];
        var secondary = (Brush)Application.Current.Resources["SecondaryTextBrush"];

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            if (job.IsFailed && i == current)
            {
                row.Icon.Glyph = "\uE711";
                row.Icon.Foreground = failBrush;
                row.Label.Foreground = failBrush;
            }
            else if (i < current || (job.IsFinished && !job.IsFailed && i == current))
            {
                row.Icon.Glyph = "\uE73E";
                row.Icon.Foreground = doneBrush;
                row.Label.Foreground = secondary;
            }
            else if (i == current)
            {
                row.Icon.Glyph = "\uE72A";
                row.Icon.Foreground = activeBrush;
                row.Label.Foreground = primary;
            }
            else
            {
                row.Icon.Glyph = "\uE738";
                row.Icon.Foreground = idleBrush;
                row.Label.Foreground = secondary;
            }
        }

        Progress.Value = Math.Clamp(job.Fraction * 100, 0, 100);
        PercentText.Text = job.PercentText;
        StatusText.Text = job.StatusText;
        StatusText.Foreground = job.IsFailed ? failBrush : secondary;
        DetailText.Text = string.IsNullOrWhiteSpace(job.Detail) ? "—" : job.Detail;
        ElapsedText.Text = job.ElapsedText;
    }
}

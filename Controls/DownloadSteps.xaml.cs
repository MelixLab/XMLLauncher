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
/// 下载进度面板：固定五步 + 进度条 + 指标 + 当前文件。
/// 对话框和下载页共用一个，避免两处各写一套。
/// </summary>
public sealed partial class DownloadSteps : UserControl
{
    public static readonly DependencyProperty JobProperty =
        DependencyProperty.Register(nameof(Job), typeof(DownloadJob), typeof(DownloadSteps),
            new PropertyMetadata(null, OnJobChanged));

    private sealed class Row
    {
        public required FontIcon Icon { get; init; }
        public required TextBlock Label { get; init; }
    }

    private static readonly string[] Captions =
    {
        "准备",
        "下载客户端",
        "下载依赖库",
        "下载资源文件",
        "完成"
    };

    private readonly List<Row> _rows = new();
    private DownloadJob? _job;
    private int _furthestStep; // 已经走到第几步，只增不减，避免阶段文案来回跳

    public DownloadSteps()
    {
        InitializeComponent();
        BuildRows();
    }

    /// <summary>当前要跟踪的下载任务。</summary>
    public DownloadJob? Job
    {
        get => (DownloadJob?)GetValue(JobProperty);
        set => SetValue(JobProperty, value);
    }

    private static void OnJobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DownloadSteps)d;

        if (view._job is not null)
        {
            view._job.PropertyChanged -= view.OnJobPropertyChanged;
        }

        view._job = e.NewValue as DownloadJob;
        view._furthestStep = 0;

        if (view._job is not null)
        {
            view._job.PropertyChanged += view.OnJobPropertyChanged;
        }

        view.Refresh();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 下载进度是在后台线程改的，回到 UI 线程再刷。
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void BuildRows()
    {
        var iconFont = (FontFamily)Application.Current.Resources["IconFontFamily"];
        var idle = (Brush)Application.Current.Resources["TertiaryTextBrush"];
        var text = (Brush)Application.Current.Resources["SecondaryTextBrush"];

        foreach (var caption in Captions)
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

    /// <summary>把 Stage 文案映射到固定的五步之一。</summary>
    private static int StageIndex(string stage)
    {
        if (string.IsNullOrEmpty(stage)) return 0;
        if (stage.Contains("完成") || stage.Contains("校验")) return 4;
        if (stage.Contains("资源")) return 3;
        if (stage.Contains("依赖") || stage.Contains("库")) return 2;
        if (stage.Contains("客户端") || stage.Contains("下载")) return 1;
        return 0;
    }

    private void Refresh()
    {
        var job = _job;
        if (job is null || _rows.Count == 0) return;

        int current = job.IsFinished ? 4 : Math.Max(_furthestStep, StageIndex(job.Stage));
        if (!job.IsFailed) _furthestStep = current;

        var doneBrush = (Brush)Application.Current.Resources["SecurityGreenBrush"];
        var activeBrush = (Brush)Application.Current.Resources["BrandBrush"];
        var idleBrush = (Brush)Application.Current.Resources["TertiaryTextBrush"];
        var primary = (Brush)Application.Current.Resources["PrimaryTextBrush"];
        var secondary = (Brush)Application.Current.Resources["SecondaryTextBrush"];

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];

            if (i < current)
            {
                row.Icon.Glyph = "\uE73E";
                row.Icon.Foreground = doneBrush;
                row.Label.Foreground = secondary;
            }
            else if (i == current)
            {
                row.Icon.Glyph = job.IsFailed && i == 4 ? "\uE711" : "\uE72A";
                row.Icon.Foreground = job.IsFailed && i == 4 ? (Brush)Application.Current.Resources["SecurityRedBrush"] : activeBrush;
                row.Label.Foreground = primary;
            }
            else
            {
                row.Icon.Glyph = "\uE738";
                row.Icon.Foreground = idleBrush;
                row.Label.Foreground = secondary;
            }
        }

        PercentText.Text = job.PercentText;
        Progress.IsIndeterminate = false;
        Progress.Value = Math.Clamp(job.Fraction * 100, 0, 100);
        SpeedText.Text = job.SpeedText;
        SizeText.Text = job.SizeText;
        FileText.Text = string.IsNullOrEmpty(job.CurrentFile) ? "—" : job.CurrentFile;
    }
}

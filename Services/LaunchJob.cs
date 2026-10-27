using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinxLauncher.Services;

/// <summary>
/// 一次启动过程的进度。和 <see cref="DownloadJob"/> 是同一套思路：
/// 固定五步文案 + 当前细节 + 失败原因，界面（LaunchSteps）只负责画。
/// </summary>
public sealed class LaunchJob : INotifyPropertyChanged
{
    /// <summary>固定五步，和下载进度窗口一样不随阶段来回变。</summary>
    public static readonly string[] Captions =
    {
        "检查版本文件",
        "定位 Java 运行时",
        "整理启动参数",
        "启动游戏进程",
        "游戏运行中"
    };

    public LaunchJob(string versionId)
    {
        VersionId = versionId;
        StartedAt = DateTime.Now;
    }

    public string VersionId { get; }
    public DateTime StartedAt { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name ?? "");
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private int _step;
    public int Step
    {
        get => _step;
        set { Set(ref _step, Math.Clamp(value, 0, Captions.Length - 1)); OnPropertyChanged(nameof(Fraction)); }
    }

    private string _detail = "准备中…";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _statusText = "正在启动";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private double? _overrideFraction;

    /// <summary>用于「下载 Java」这类带百分比的阶段：直接指定进度条位置。</summary>
    public void SetProgress(string detail, double fraction)
    {
        Detail = detail;
        _overrideFraction = Math.Clamp(fraction, 0, 0.99);
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(PercentText));
    }

    public void ClearOverrideFraction()
    {
        if (_overrideFraction is null) return;
        _overrideFraction = null;
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(PercentText));
    }

    private string? _logFile;
    public string? LogFile { get => _logFile; set => Set(ref _logFile, value); }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        private set { Set(ref _isRunning, value); OnPropertyChanged(nameof(IsFinished)); OnPropertyChanged(nameof(Fraction)); }
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        private set { Set(ref _isFailed, value); OnPropertyChanged(nameof(IsFinished)); OnPropertyChanged(nameof(Fraction)); }
    }

    public bool IsFinished => !IsRunning;

    private string _error = "";
    public string Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>进度条取值：没跑完之前按「走到第几步」折算，跑完就是 100%。</summary>
    public double Fraction
    {
        get
        {
            if (IsFailed) return 0;
            if (!IsRunning) return 1;
            if (_overrideFraction is { } overridden) return overridden;
            return Math.Clamp((Step + 0.45) / Captions.Length, 0, 0.98);
        }
    }

    public string PercentText => $"{Fraction * 100:0}%";

    public string ElapsedText
    {
        get
        {
            var ts = DateTime.Now - StartedAt;
            if (ts.TotalMinutes >= 1) return $"已用时 {ts.Minutes} 分 {ts.Seconds} 秒";
            return $"已用时 {ts.TotalSeconds:0} 秒";
        }
    }

    /// <summary>启动过程上报一个阶段。</summary>
    public void Apply(LaunchProgress p)
    {
        Step = p.Step;
        if (!string.IsNullOrWhiteSpace(p.Detail)) Detail = p.Detail;
    }

    /// <summary>进程起来了，但游戏窗口还没出现 —— 这期间窗口要一直留着给用户看。</summary>
    public void WaitingForWindow()
    {
        Step = Captions.Length - 1;
        StatusText = "等待游戏启动";
        Detail = "游戏进程已启动，正在等待游戏窗口出现…";
    }

    /// <summary>更新等待时长提示。</summary>
    public void UpdateWaitDetail(string detail) => Detail = detail;

    public void Complete(string? logFile)
    {
        LogFile = logFile;
        Step = Captions.Length - 1;
        StatusText = "已启动";
        Detail = "游戏进程已运行，启动器已最小化到托盘。";
        IsRunning = false;
    }

    /// <summary>没跑起来就结束（Java 缺失、版本不完整等）。</summary>
    public void Fail(string error)
    {
        Error = error;
        StatusText = "启动失败";
        Detail = error;
        IsRunning = false;
        IsFailed = true;
    }
}

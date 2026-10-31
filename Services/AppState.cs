using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DownloadJob : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadJob(string title, string subtitle = "", string operationKey = "")
    {
        Title = title;
        Subtitle = subtitle;
        OperationKey = operationKey;
        StartedAt = DateTime.Now;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string OperationKey { get; }
    public DateTime StartedAt { get; }

    private readonly object _stateLock = new();
    private bool _cancelRequested;

    /// <summary>任务专用的取消令牌；网络超时不应取消此令牌。</summary>
    public CancellationTokenSource Cts { get; } = new();

    public void Cancel()
    {
        lock (_stateLock)
        {
            if (!CanCancel) return;
            _cancelRequested = true;
            StatusText = "正在取消";
            Speed = 0;
            IsIndeterminate = true;
            NotifyCancellationState();
        }

        try
        {
            Cts.Cancel();
        }
        catch
        {
            // 取消回调异常或令牌已释放时，仍等待任务实际结束，不能提前移除条目。
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _stage = "准备";
    public string Stage { get => _stage; set => Set(ref _stage, value); }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set => Set(ref _currentFile, value); }

    private double _fraction;
    public double Fraction
    {
        get => _fraction;
        set { Set(ref _fraction, value); OnPropertyChanged(nameof(PercentText)); }
    }

    public string PercentText => Fraction >= 1 ? "100%" : $"{Fraction * 100:0}%";

    // 仅供进度界面使用；不参与下载调度或任务完成判定。
    private bool _isIndeterminate = true;
    public bool IsIndeterminate { get => _isIndeterminate; set => Set(ref _isIndeterminate, value); }

    private long _doneBytes;
    private long _totalBytes;
    public long DoneBytes { get => _doneBytes; set { Set(ref _doneBytes, value); OnPropertyChanged(nameof(SizeText)); } }
    public long TotalBytes { get => _totalBytes; set { Set(ref _totalBytes, value); OnPropertyChanged(nameof(SizeText)); } }

    public string SizeText => TotalBytes > 0
        ? $"{GameInstaller.FormatSize(DoneBytes)} / {GameInstaller.FormatSize(TotalBytes)}"
        : "—";

    private int _doneFiles;
    private int _totalFiles;
    public int DoneFiles { get => _doneFiles; set { Set(ref _doneFiles, value); OnPropertyChanged(nameof(FilesText)); } }
    public int TotalFiles { get => _totalFiles; set { Set(ref _totalFiles, value); OnPropertyChanged(nameof(FilesText)); } }
    public string FilesText => TotalFiles > 0 ? $"{DoneFiles:N0} / {TotalFiles:N0}" : "—";

    private double _speed;
    public double Speed
    {
        get => _speed;
        set { Set(ref _speed, value); OnPropertyChanged(nameof(SpeedText)); OnPropertyChanged(nameof(EtaText)); }
    }
    public string SpeedText => Speed <= 1 ? "—" : GameInstaller.FormatSize((long)Speed) + "/s";

    public string EtaText
    {
        get
        {
            if (TotalBytes <= 0 || DoneBytes >= TotalBytes) return "—";
            if (Speed < 1024) return "计算中";
            var remain = (TotalBytes - DoneBytes) / Speed;
            if (remain > 3600) return $"约 {remain / 3600:0.#} 小时";
            var ts = TimeSpan.FromSeconds(remain);
            return ts.TotalMinutes >= 1 ? $"约 {ts.Minutes} 分 {ts.Seconds} 秒" : $"约 {ts.Seconds} 秒";
        }
    }

    private string _statusText = "准备中";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            Set(ref _isRunning, value);
            OnPropertyChanged(nameof(IsFinished));
            NotifyCancellationState();
        }
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        set { Set(ref _isFailed, value); OnPropertyChanged(nameof(IsFinished)); }
    }

    public bool IsFinished => !IsRunning;

    private bool _isCanceled;
    public bool IsCanceled { get => _isCanceled; private set => Set(ref _isCanceled, value); }
    public bool IsCancelling => IsRunning && (_cancelRequested || Cts.IsCancellationRequested);
    public bool CanCancel => IsRunning && !IsCancelling;
    public string CancelButtonText => IsCancelling ? "正在取消" : "取消";

    private string _error = "";
    public string Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    private DateTime _lastTick = DateTime.MinValue;
    private long _lastBytes;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyCancellationState()
    {
        OnPropertyChanged(nameof(IsCancelling));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelButtonText));
    }

    public void Apply(InstallProgress p)
    {
        lock (_stateLock)
        {
            // 排队中的进度可能晚于取消请求或最终结果到达，不能覆盖等待取消及终态。
            if (!IsRunning || IsCancelling) return;

            IsIndeterminate = p.Indeterminate || p.TotalBytes <= 0;
            if (!string.IsNullOrEmpty(p.Stage)) Stage = p.Stage;
            if (!string.IsNullOrEmpty(p.Message)) CurrentFile = p.Message;
            else if (!string.IsNullOrEmpty(p.CurrentFile)) CurrentFile = p.CurrentFile;

            if (p.TotalBytes > 0)
            {
                DoneBytes = Math.Min(p.DoneBytes, p.TotalBytes);
                TotalBytes = p.TotalBytes;
                Fraction = Math.Clamp((double)p.DoneBytes / p.TotalBytes, 0, 1);
            }

            DoneFiles = p.DoneFiles;
            TotalFiles = p.TotalFiles;

            var now = DateTime.Now;
            if (_lastTick != DateTime.MinValue)
            {
                var dt = (now - _lastTick).TotalSeconds;
                if (dt > 0.12 && p.DoneBytes >= _lastBytes)
                {
                    double inst = (p.DoneBytes - _lastBytes) / dt;
                    Speed = Speed <= 1 ? inst : Speed * 0.65 + inst * 0.35;
                    _lastTick = now;
                    _lastBytes = p.DoneBytes;
                }
            }
            else
            {
                _lastTick = now;
                _lastBytes = p.DoneBytes;
            }
        }
    }

    public void Complete() => Finish(true);

    public void Fail(string error) => Finish(false, error);

    private void Finish(bool success, string? error = null)
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;

            // 只依据用户请求、任务自身令牌或旧入口的明确取消标记判断取消。
            // HttpClient 超时也会抛 OperationCanceledException，不能据异常名称判为取消。
            IsCanceled = _cancelRequested || Cts.IsCancellationRequested || (!success && error == "已取消");
            IsFailed = !success && !IsCanceled;
            IsIndeterminate = false;
            StatusText = IsCanceled ? "已取消" : IsFailed ? "失败" : "已完成";
            Error = IsFailed ? error ?? "未知错误" : "";
            if (success && !IsCanceled)
            {
                Fraction = 1;
                DoneBytes = TotalBytes;
                DoneFiles = TotalFiles;
            }
            Speed = 0;
            IsRunning = false;
        }
    }
}

public sealed class DownloadCenter
{
    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    /// <summary>当前正在跑的任务（可以同时有多个）。</summary>
    public IReadOnlyList<DownloadJob> Running =>
        Jobs.Where(j => j.IsRunning).ToList();

    /// <summary>兼容旧用法：取第一个正在跑的任务。</summary>
    public DownloadJob? Active => Jobs.FirstOrDefault(j => j.IsRunning);

    public bool IsBusy => Jobs.Any(j => j.IsRunning);
    public bool HasAny => Jobs.Count > 0;

    public bool IsRunningFor(string operationKey) =>
        Jobs.Any(job => job.IsRunning && string.Equals(job.OperationKey, operationKey, StringComparison.Ordinal));

    public event Action? ActiveChanged;

    /// <summary>开一个新任务。允许在已有任务下载时再开一个。</summary>
    public DownloadJob Begin(string title, string subtitle = "", string operationKey = "")
    {
        var job = new DownloadJob(title, subtitle, operationKey);
        Jobs.Insert(0, job);
        ActiveChanged?.Invoke();
        return job;
    }

    public void Finish(DownloadJob job, bool success, string? error = null)
    {
        if (success) job.Complete();
        else job.Fail(error ?? "未知错误");

        ActiveChanged?.Invoke();
    }

    public void ClearFinished()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
            if (Jobs[i].IsFinished) Jobs.RemoveAt(i);
        ActiveChanged?.Invoke();
    }
}

public static class AppState
{
    private static bool _inited;

    public static AppSettings Settings { get; private set; } = new();
    public static VersionService Versions { get; } = new();
    public static GameInstaller Installer { get; } = new();
    public static LoaderService Loaders { get; } = new();
    public static LaunchService Launcher { get; } = new();
    public static DownloadCenter Downloads { get; } = new();
    public static ObservableCollection<LocalVersion> Installed { get; } = new();

    public static string SelectedVersion { get; set; } = "";

    /// <summary>最近一次从服务器拉到的公告（没有就是 null）。</summary>
    public static Announcement? Announcement { get; set; }

    public static event Action? InstalledChanged;

    public static void Init()
    {
        if (_inited) return;
        _inited = true;

        Settings = SettingsService.Load();
        Paths.EnsureCore();
        RefreshInstalled();

        // 首次运行就把默认设置落盘：以前 Save() 因为 _current 没赋值一直静默失败，
        // 用户改了账号/头像重启就回到默认；这里补一次，至少保证文件存在且可写。
        SettingsService.Save();

        if (string.IsNullOrEmpty(SelectedVersion))
            SelectedVersion = Installed.FirstOrDefault()?.Id ?? Settings.LastVersionId ?? "";
    }

    public static void RefreshInstalled()
    {
        var list = Versions.ScanInstalled();

        // 装了加载器之后，原版那条可以从界面里藏起来（文件保留，随时能恢复）
        var hidden = Settings.HiddenVersions ?? new List<string>();
        var visible = list.Where(v => !hidden.Contains(v.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (visible.Count == 0) visible = list;   // 别把人锁死在空列表

        Installed.Clear();
        foreach (var v in visible) Installed.Add(v);

        if (string.IsNullOrEmpty(SelectedVersion) || Installed.All(v => v.Id != SelectedVersion))
            SelectedVersion = Installed.FirstOrDefault()?.Id ?? "";

        InstalledChanged?.Invoke();
    }

    public static LocalVersion? Current =>
        Installed.FirstOrDefault(v => v.Id == SelectedVersion) ?? Installed.FirstOrDefault();
}

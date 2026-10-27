using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;
using LinxLauncher.Services;
using LinxLauncher.Views;
using LinxLauncher.Controls;
using Windows.Graphics;

namespace LinxLauncher;

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private bool _sized;

    /// <summary>
    /// 客户区宽度刚好放下概览页的六个卡片：48 图标栏 + 左右各 40 内边距 + 3×232 卡片 + 2×8 间距 = 840，
    /// 再留 ~20 余量给滚动条和取整。高度 780。
    /// ResizeClient 收物理像素，按 DPI 放大；再按显示器工作区夹一次，小屏也不会超出。
    /// </summary>
    private void ApplyWindowSize()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 缩放必须取 XamlRoot 的 RasterizationScale：窗口刚创建时 GetDpiForWindow 可能还是 96，
        // 拿它算会把 860 DIP 当成 860 物理像素，125% 屏上窗口就窄了一圈，右边那列卡片被裁。
        double scale = 0;
        try
        {
            if (Content?.XamlRoot is { } root && root.RasterizationScale > 0)
                scale = root.RasterizationScale;
        }
        catch { }

        if (scale <= 0.1)
        {
            var dpi = GetDpiForWindow(hwnd);
            scale = dpi > 0 ? dpi / 96.0 : 1.0;
        }

        int wantW = (int)Math.Round(860 * scale);
        int wantH = (int)Math.Round(780 * scale);

        Log.Info($"窗口尺寸 scale={scale} dpi={GetDpiForWindow(hwnd)} root={(Content?.XamlRoot is not null)} → {wantW}x{wantH}");

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(wantW, area.Width - 40);
        int h = Math.Min(wantH, area.Height - 40);

        AppWindow.ResizeClient(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - w) / 2,
            area.Y + (area.Height - h) / 2));

        GetClientRect(hwnd, out var rc);
        Log.Info($"ResizeClient({w}x{h}) → 实际客户区 {rc.Right - rc.Left}x{rc.Bottom - rc.Top}");
    }


    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        // 图标必须给绝对路径：单文件版里进程的工作目录是运行时解压目录，
        // 相对路径一旦解析不到，任务栏就会退回系统默认的空白图标。
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            else Log.Warn("任务栏图标缺失：" + iconPath);

            // 标题栏左上角的小图标也一起换掉（只隐藏不换的话，Alt+Tab 里还是旧壳）
            var smallIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-48_altform-lightunplated.png");
            if (File.Exists(smallIcon)) AppWindow.SetIcon(smallIcon);
        }
        catch (Exception ex)
        {
            Log.Warn("设置窗口图标失败：" + ex.Message);
        }

        // 标题栏保持系统默认（不染色、不隐藏），只去掉左上角的小图标和系统菜单，
        // 让标题栏这条和下面的侧边栏/内容区看起来是同一块 —— 参考项目的做法：
        // 不去改标题栏迁就内容，而是让内容底色去对齐标题栏的纯白。
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }
        catch { }
        // 尺寸必须等窗口真正激活后再设，否则会被 WinUI 的默认 1024×768 覆盖掉。
        Activated += (_, _) =>
        {
            if (_sized) return;
            _sized = true;
            ApplyWindowSize();

            // 第一次算尺寸时 XamlRoot 可能还没挂上缩放比例，200ms 后按真实 DPI 再校一次
            var fix = DispatcherQueue.CreateTimer();
            fix.Interval = TimeSpan.FromMilliseconds(220);
            fix.IsRepeating = false;
            fix.Tick += (_, _) => { fix.Stop(); ApplyWindowSize(); };
            fix.Start();

            // 联网检查（公告 + 强制更新）放后台，连不上服务器不影响正常使用
            if (AppState.Settings.OnboardingDone) _ = RunStartupChecksAsync();
        };

        AppState.InstalledChanged += () => DispatcherQueue.TryEnqueue(RefreshAccountUi);
        AppState.Downloads.ActiveChanged += () => DispatcherQueue.TryEnqueue(RefreshAccountUi);

        RefreshAccountUi();
        UpdateAccountLayout();

        // 上次自动更新留下的 .old 文件，能删就删
        UpdateService.CleanupOldFiles();
        NavView.PaneOpening += (_, _) => UpdateAccountLayout();
        NavView.PaneClosing += (_, _) => UpdateAccountLayout();
        NavView.DisplayModeChanged += (_, _) => UpdateAccountLayout();

        // 页面内容（卡片、列表项）是布局阶段才生成的，所以导航后补刷两次：
        // 一次 Low 优先级排队，一次 350ms 后兜底，否则模板里的文字会漏掉。
        ContentFrame.Navigated += (_, _) =>
        {
            FontService.RefreshAll();
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                FontService.RefreshAll);

            var fontTimer = DispatcherQueue.CreateTimer();
            fontTimer.Interval = TimeSpan.FromMilliseconds(350);
            fontTimer.IsRepeating = false;
            fontTimer.Tick += (_, _) => FontService.RefreshAll();
            fontTimer.Start();
        };
        ContentFrame.NavigationFailed += (_, e) => Log.Fatal("Frame", e.Exception);
        ContentFrame.Navigate(typeof(OverviewPage));
        SelectNavItem("overview");

        // 首次启动：直接把向导盖在页面上（不进任何页面），
        // 等这一帧布局完再铺，否则遮罩会在窗口第一次绘制时闪一下。
        if (!AppState.Settings.OnboardingDone)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ShowOnboarding());
        }

        // 关窗时清掉托盘图标，别在通知区留一个点不动的幽灵
        Closed += (_, _) => TrayService.Dispose();
    }

    public void NavigateTo(string tag, object? parameter = null)
    {
        Type? target = tag switch
        {
            "overview" => typeof(OverviewPage),
            "versions" => typeof(VersionsPage),
            "downloads" => typeof(DownloadsPage),
            "mods" => typeof(ModsPage),
            "relay" => typeof(RelayPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (target is null) return;
        Log.Info($"导航 → {tag}（当前 {ContentFrame.CurrentSourcePageType}）");

        if (ContentFrame.CurrentSourcePageType != target)
        {
            bool ok;
            try { ok = ContentFrame.Navigate(target, parameter, new DrillInNavigationTransitionInfo()); }
            catch (Exception ex) { Log.Fatal("Navigate", ex); return; }
            Log.Info($"导航结果 {ok} → {ContentFrame.CurrentSourcePageType} / {ContentFrame.Content?.GetType().Name}");
        }

        SelectNavItem(tag);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        NavigateTo(tag);
    }

    private void SelectNavItem(string tag)
    {
        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }

        foreach (var obj in NavView.FooterMenuItems)
        {
            if (obj is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    // ================= 新手向导 =================

    private OnboardingFlow? _wizard;

    /// <summary>
    /// 打开新手向导。首次启动自动调用，设置页里的「重新观看」也走这里。
    /// 向导自己不动手画对话框：每一步是 WinUI 的 ContentDialog，
    /// 页面介绍是 TeachingTip 气泡，所以和其它对话框、系统弹窗观感一致。
    /// </summary>
    public void ShowOnboarding(bool fromSettings = false)
    {
        if (_wizard is not null) return;

        _wizard = new OnboardingFlow(this);
        Log.Info(fromSettings ? "从设置页重新打开新手向导" : "首次启动，打开新手向导");
        _ = RunOnboardingAsync();
    }

    private async Task RunOnboardingAsync()
    {
        try
        {
            await _wizard!.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Onboarding", ex);
        }
        finally
        {
            // 走完 / 跳过都算完成，下次启动不再弹
            AppState.Settings.OnboardingDone = true;
            SettingsService.Save();

            _wizard = null;
            RefreshAccountUi();

            // 向导期间不弹公告/更新（对话框会被压在后面），结束后补跑一次
            _ = RunStartupChecksAsync();
        }
    }

    /// <summary>TeachingTip 这类浮层需要挂在窗口根节点上，向导结束后再摘掉。</summary>
    public void AddTransientLayer(FrameworkElement element) => RootGrid.Children.Add(element);

    public void RemoveTransientLayer(FrameworkElement element) => RootGrid.Children.Remove(element);

    /// <summary>巡览气泡要指向的侧栏项。取不到容器就退回到整个侧栏。</summary>
    public FrameworkElement? GetNavItemAnchor(string tag)
    {
        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem item && (item.Tag as string) == tag)
                return NavView.ContainerFromMenuItem(item) as FrameworkElement ?? NavView;
        }

        foreach (var obj in NavView.FooterMenuItems)
        {
            if (obj is NavigationViewItem item && (item.Tag as string) == tag)
                return NavView.ContainerFromMenuItem(item) as FrameworkElement ?? NavView;
        }

        return NavView;
    }

    // ================= 服务器：公告 / 强制更新 =================

    private bool _startupChecked;

    /// <summary>
    /// 启动检查。顺序很重要：
    /// 1) 先读本地 update-state.json —— 上次已经知道"必须更新"的话，
    ///    这次就算断网也要拦住（防止拔网线逃避强制更新）；
    /// 2) 再联网查一次，拿到最新状态；
    /// 3) 都不需要更新，才去拉公告。
    /// </summary>
    private async Task RunStartupChecksAsync()
    {
        if (_startupChecked) return;
        _startupChecked = true;

        try
        {
            await RunStartupChecksCoreAsync();
        }
        catch (Exception ex)
        {
            // 这条曾经的坑：从 Activated 里 _ = 丢出去的 Task，异常没人接，
            // 于是「更新对话框没弹出来」在日志里连个影都没有。现在至少留痕。
            Log.Error("StartupChecks", ex);
        }
    }

    /// <summary>等窗口的 XamlRoot 就绪再弹对话框，否则 ShowAsync 会直接抛。</summary>
    private async Task<bool> WaitForXamlRootAsync(int timeoutMs = 5000)
    {
        var waited = 0;
        while (Content?.XamlRoot is null && waited < timeoutMs)
        {
            await Task.Delay(100);
            waited += 100;
        }
        return Content?.XamlRoot is not null;
    }

    private async Task RunStartupChecksCoreAsync()
    {
        var cached = RemoteService.LoadState();
        var online = await RemoteService.CheckUpdateAsync();
        var info = online ?? cached;

        if (info is not null && info.Mandatory && RemoteService.IsNewer(info.Latest, RemoteService.ClientVersion))
        {
            if (!await WaitForXamlRootAsync())
            {
                Log.Warn("窗口还没就绪，这次不弹强制更新提示");
                return;
            }
            await ShowForcedUpdateAsync(info, offline: online is null);
            return;
        }

        // 非强制更新也要提示 —— 以前这条分支是漏的，服务器发了可选版本，
        // 客户端启动什么都不弹，等于白发布。同一版本只提醒一次。
        if (info is not null
            && RemoteService.IsNewer(info.Latest, RemoteService.ClientVersion)
            && !RemoteService.HasPrompted(info.Latest))
        {
            Log.Info($"发现新版本 {info.Latest}（当前 {RemoteService.ClientVersion}），弹出更新提示");
            if (!await WaitForXamlRootAsync())
            {
                Log.Warn("窗口还没就绪，这次不弹更新提示");
                return;
            }
            await ShowOptionalUpdateAsync(info);

            // 两个 ContentDialog 不能同时开，隔一下再弹公告
            await Task.Delay(250);
        }

        AppState.Announcement = await RemoteService.GetAnnouncementAsync();

        var ann = AppState.Announcement;
        if (ann is not null &&
            (!string.IsNullOrWhiteSpace(ann.Title) || !string.IsNullOrWhiteSpace(ann.Content)))
        {
            await ShowAnnouncementAsync(ann);
        }
    }

    /// <summary>
    /// 可选更新（非强制）：给用户一个「立即更新 / 稍后再说」的选择。
    /// 不更新也能继续用启动器，只是每次有新版本才提示一次。
    /// </summary>
    private async Task ShowOptionalUpdateAsync(UpdateInfo info)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 430 };

        panel.Children.Add(new TextBlock
        {
            Text = $"发现新版本 {info.Latest}（当前 {RemoteService.ClientVersion}）。",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(info.Changelog))
        {
            panel.Children.Add(new TextBlock
            {
                Text = info.Changelog,
                FontSize = 13,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "这次不是强制更新，不更新也能继续用。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "有新版本",
            Content = panel,
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后再说",
            DefaultButton = ContentDialogButton.Close
        };
        FontService.ApplyToDialog(dialog);

        // 无论用户选哪个，这个版本都算"已提示"，避免每次启动都弹
        RemoteService.MarkPrompted(info.Latest);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (await DownloadAndInstallAsync(info))
        {
            Application.Current.Exit();
            return;
        }

        OpenDownloadPage(info.DownloadUrl);
        Toast("下载失败，已打开下载页，装好后重新打开启动器即可");
    }

    /// <summary>
    /// 服务器公告：每次启动弹一次，不占主界面布局。
    /// 「重要」级别的公告用红色提示行，普通公告只做标题 + 正文。
    /// </summary>
    private async Task ShowAnnouncementAsync(Announcement ann)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };

        if (!string.IsNullOrWhiteSpace(ann.Content))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ann.Content,
                FontSize = 13.5,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (string.Equals(ann.Level, "critical", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "这是一条重要公告，请留意。",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["NoticeTextBrush"]
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = string.IsNullOrWhiteSpace(ann.Title) ? "公告" : ann.Title,
            Content = panel,
            PrimaryButtonText = "知道了",
            DefaultButton = ContentDialogButton.Primary
        };
        FontService.ApplyToDialog(dialog);

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 强制更新对话框：没有"取消"，Esc 和点击空白都关不掉。
    /// 只有「立即更新」（在线）或「重试」（离线）两个出口。
    /// </summary>
    private async Task ShowForcedUpdateAsync(UpdateInfo info, bool offline)
    {
        while (true)
        {
            var panel = new StackPanel { Spacing = 10, MinWidth = 430 };

            panel.Children.Add(new TextBlock
            {
                Text = offline
                    ? "检测到必须完成的更新，但现在连不上服务器。"
                    : $"发现新版本 {info.Latest}（当前 {RemoteService.ClientVersion}）。",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(info.Changelog))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = info.Changelog,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = offline
                    ? "请连上网络后点「重试」。这个提示会一直保留，断网也不会绕过。"
                    : "这一版必须更新后才能继续使用启动器。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["NoticeTextBrush"]
            });

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "需要更新",
                Content = panel,
                PrimaryButtonText = offline ? "重试" : "立即更新"
            };
            FontService.ApplyToDialog(dialog);

            // 强制更新：不允许用 Esc / 点空白关掉
            dialog.Closing += (_, e) =>
            {
                if (e.Result == ContentDialogResult.None) e.Cancel = true;
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && !offline)
            {
                // 直接把安装包下下来并拉起安装程序；下不动才退回"打开下载页"
                if (await DownloadAndInstallAsync(info))
                {
                    Application.Current.Exit();
                    return;
                }

                OpenDownloadPage(info.DownloadUrl);
                Toast("下载失败，已打开下载页，装好后重新打开启动器即可");
                continue;
            }

            // 「重试」或「重新检查」：再问一次服务器
            var retry = await RemoteService.CheckUpdateAsync();

            if (retry is not null && retry.Mandatory && RemoteService.IsNewer(retry.Latest, RemoteService.ClientVersion))
            {
                info = retry;
                offline = false;
                continue;
            }

            if (retry is null)
            {
                // 还是连不上：保持拦截，让用户继续重试
                continue;
            }

            // 服务器说不用强制更新了
            Toast("已经是最新版本了");
            return;
        }
    }

    /// <summary>
    /// 下载新版本并在退出后自动替换自己。
    /// 成功返回 true —— 调用方负责把程序退掉，替换脚本这时候已经在等了。
    /// </summary>
    private async Task<bool> DownloadAndInstallAsync(UpdateInfo info)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 400, IsIndeterminate = true };
        var head = new TextBlock
        {
            Text = $"正在下载 {info.Latest}…",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            Text = "正在连接服务器…",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "正在更新",
            Content = new StackPanel { Spacing = 12, Children = { head, bar, detail } }
        };
        FontService.ApplyToDialog(dialog);
        dialog.Closing += (_, e) => e.Cancel = true;   // 下载中不许关

        _ = dialog.ShowAsync();

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (p.Total > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Min(100, p.Done * 100.0 / p.Total);
                    var speed = p.BytesPerSecond > 0 ? " · " + FormatSpeed(p.BytesPerSecond) : "";
                    detail.Text = $"{GameInstaller.FormatSize(p.Done)} / {GameInstaller.FormatSize(p.Total)}{speed}";
                }
                else
                {
                    bar.IsIndeterminate = true;
                    detail.Text = GameInstaller.FormatSize(p.Done);
                }
            });

            var file = await UpdateService.DownloadAsync(info, progress, default);

            detail.Text = "正在校验文件…";
            var problem = UpdateService.Verify(file, info);
            if (problem is not null)
            {
                try { dialog.Hide(); } catch { }
                Toast(problem, true);
                return false;
            }

            bar.IsIndeterminate = false;
            bar.Value = 100;
            detail.Text = "校验通过，正在替换并重启…";
            head.Text = "马上就好";

            // 给界面一帧时间把「正在重启」显示出来
            await Task.Delay(300);

            if (!UpdateService.ApplyUpdateNow(file, out var message))
            {
                try { dialog.Hide(); } catch { }
                Toast("自动更新没成功：" + message, true);
                return false;
            }

            try { dialog.Hide(); } catch { }

            // 新版本已经拉起来了，这里直接走人（WinUI 的 Exit 偶尔退不干净，加一层兜底）
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Environment.Exit(0);
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            try { dialog.Hide(); } catch { }
            var firstLine = ex.Message.Split(Environment.NewLine)[0];
            Toast("下载更新失败：" + firstLine, true);
            return false;
        }
    }

    /// <summary>把字节/秒说成人话。</summary>
    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return (bytesPerSecond / 1024 / 1024).ToString("0.0") + " MB/s";
        if (bytesPerSecond >= 1024)
            return (bytesPerSecond / 1024).ToString("0") + " KB/s";
        return bytesPerSecond.ToString("0") + " B/s";
    }

    /// <summary>
    /// XML 账号登录。服务端开了邮箱二次验证（LINX_LOGIN_EMAIL）时，会再弹一个验证码框。
    /// allowPrompt=false 用于「已经有对话框开着」的场景（比如新手向导），只做提示不弹框。
    /// </summary>
    public async Task<bool> ServerLoginAsync(string user, string pass, TextBlock? status = null, bool allowPrompt = true)
    {
        var result = await LinxAccountService.LoginAsync(user, pass, null, default);
        if (!result.NeedCode) return true;

        if (!allowPrompt)
        {
            if (status is not null)
                status.Text = "这个账号开了邮箱二次验证，请先跳过向导，在「设置 → XML 账号」里登录。";
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var box = new TextBox { PlaceholderText = "邮件里的 6 位验证码", MaxLength = 6 };
            var tip = new TextBlock
            {
                Text = result.Hint,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
            };

            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "邮箱验证",
                Content = new StackPanel { Spacing = 10, Children = { tip, box } },
                PrimaryButtonText = "确认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            FontService.ApplyToDialog(dlg);

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

            var code = box.Text.Trim();
            if (code.Length < 4) return false;

            var again = await LinxAccountService.LoginAsync(user, pass, code, default);
            if (!again.NeedCode) return true;
            result = again;
        }

        return false;
    }

    /// <summary>登录后把本地头像补传到服务器（只在本地有图、且用户愿意的情况下）。</summary>
    private async Task SyncLocalAvatarAsync()
    {
        var path = AppState.Settings.ServerAvatarPath ?? AppState.Settings.AvatarPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        await LinxAccountService.UploadAvatarAsync(path, default);
    }

    /// <summary>打开官方服务器的网页端（账号管理、公告、版本发布都在那）。</summary>
    private void OpenServerWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RemoteService.BaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开服务器网页失败：" + ex.Message);
            Toast("打开浏览器失败，请手动访问 " + RemoteService.BaseUrl, true);
        }
    }

    private void OpenDownloadPage(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) url = RemoteService.BaseUrl;
            if (url.StartsWith('/')) url = RemoteService.BaseUrl + url;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开下载页失败：" + ex.Message);
        }
    }

    // ================= 账号 =================
    public void RefreshAccountUi()
    {
        // XML 账号（服务器）优先显示：它是人的账号，进游戏用的正版/离线是另一层
        bool serverLogged = LinxAccountService.IsLoggedIn;

        var name = serverLogged
            ? LinxAccountService.DisplayName
            : AccountService.DisplayName;

        AccountName.Text = name;
        AccountSubtitle.Text = serverLogged
            ? "XML 账号"
            : (AccountService.IsMicrosoft ? "正版账号" : "离线账号");

        AvatarText.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "S";
        ApplyAvatar();
        _ = LoadMicrosoftAvatarAsync();
    }

    /// <summary>正版角色头像：缓存里有就直接用，没有就后台拉一次（mc-heads）。</summary>
    private async Task LoadMicrosoftAvatarAsync()
    {
        try
        {
            if (!AccountService.IsMicrosoft) return;

            var file = await AccountService.EnsureAvatarAsync(default);
            if (file is not null) ApplyAvatarFile(file);
        }
        catch (Exception ex)
        {
            Log.Warn("头像加载失败：" + ex.Message);
        }
    }

    private void ApplyAvatar()
    {
        var settings = AppState.Settings;

        var candidates = new List<string?>
        {
            LinxAccountService.IsLoggedIn ? settings.ServerAvatarPath : null,
            AccountService.IsMicrosoft ? AccountService.AvatarCacheFile : null,
            settings.AvatarPath
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                ApplyAvatarFile(path!);
                return;
            }
        }

        AvatarBrush.ImageSource = null;
        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarText.Visibility = Visibility.Visible;
    }

    private void ApplyAvatarFile(string path)
    {
        try
        {
            var bmp = new BitmapImage
            {
                // 头像只有 30 DIP，直接喂原图缩小会有明显锯齿；按 2 倍逻辑尺寸解码
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 60,
                DecodePixelHeight = 60,
                UriSource = new Uri(path)
            };

            AvatarBrush.ImageSource = bmp;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("Avatar", ex);
        }
    }

    /// <summary>选一张本地图片当头（复制到配置目录，原图删了也不影响）。</summary>
    private async Task<bool> PickAvatarAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return false;

            Directory.CreateDirectory(Paths.AppData);
            var dest = Path.Combine(Paths.AppData, "avatar" + Path.GetExtension(file.Name).ToLowerInvariant());
            File.Copy(file.Path, dest, true);

            AppState.Settings.AvatarPath = dest;
            SettingsService.Save();
            ApplyAvatar();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Avatar", ex);
            Toast("选头像失败：" + ex.Message.Split('\n')[0], true);
            return false;
        }
    }

    private void ClearAvatar()
    {
        AppState.Settings.AvatarPath = null;
        SettingsService.Save();
        ApplyAvatar();
    }

    // ================= 正版登录 =================

    /// <summary>
    /// 微软设备码登录：弹出代码 → 用户去网页输入 → 后台轮询 → 串 Xbox / Minecraft 令牌。
    /// 链路跟 PCL2 一致（PCL 的 client_id 是私有的，这里用公开的 Xbox 客户端 ID）。
    /// </summary>
    public async Task<bool> MicrosoftLoginAsync()
    {
        var codeText = new TextBlock
        {
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "······"
        };

        var status = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
            Text = "正在向微软申请登录代码…"
        };

        var linkButton = new Button
        {
            Content = "打开登录网页",
            HorizontalAlignment = HorizontalAlignment.Center,
            IsEnabled = false
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children =
            {
                new TextBlock
                {
                    Text = "在浏览器里打开下面的网址，输入这串代码登录（登录完成后这个窗口会自动继续）：",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                },
                codeText,
                linkButton,
                status
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "使用 Microsoft 账号登录",
            Content = content,
            CloseButtonText = "取消"
        };

        FontService.ApplyToDialog(dialog);

        var cts = new CancellationTokenSource();
        dialog.Closed += (_, _) => cts.Cancel();

        try
        {
            _ = dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("MsLoginDialog", ex);
        }

        try
        {
            var info = await MsAuthService.StartDeviceCodeAsync(cts.Token);

            codeText.Text = info.UserCode;
            status.Text = "等待你在网页上完成登录…（代码 15 分钟内有效）";
            linkButton.IsEnabled = true;
            string url = info.VerificationUri;
            linkButton.Click += (_, _) => OpenUrl(url);
            OpenUrl(url);

            var progress = new Progress<string>(msg => status.Text = msg);
            var (msAccess, refresh) = await MsAuthService.PollDeviceCodeAsync(info, progress, cts.Token);

            status.Text = "登录成功，正在换取 Xbox / Minecraft 令牌…";
            var account = await MsAuthService.ChainAsync(msAccess, refresh, cts.Token);

            AccountService.Apply(account);
            SettingsService.Save();

            status.Text = "正在获取角色头像…";
            await AccountService.EnsureAvatarAsync(cts.Token);

            dialog.Hide();
            RefreshAccountUi();
            Toast($"已登录正版账号「{account.Name}」");
            await WaitDialogClosedAsync(dialog);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { dialog.Hide(); } catch { }
            await WaitDialogClosedAsync(dialog);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("MsLogin", ex);
            status.Text = "登录失败：" + ex.Message;
            codeText.Text = "";
            linkButton.IsEnabled = false;
            // ★ 失败时对话框要留着让用户读原因，但必须等它关掉再返回：
            //   否则下一步（向导）马上开新对话框会撞上「Only a single ContentDialog」。
            await WaitDialogClosedAsync(dialog);
            return false;
        }
    }

    /// <summary>等一个 ContentDialog 真正从可视树里消失。</summary>
    private static async Task WaitDialogClosedAsync(ContentDialog dialog)
    {
        for (int i = 0; i < 600 && dialog.IsLoaded; i++)
            await Task.Delay(50);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开网页失败：" + ex.Message);
        }
    }

    // ================= 账号对话框 =================

    /// <summary>外部（联机页等）也可以直接调起账号面板。</summary>
    public Task ShowAccountDialogAsync() => BuildAccountDialogAsync();

    public async void AccountButton_Click(object sender, RoutedEventArgs e) => await BuildAccountDialogAsync();

    /// <summary>
    /// 账号面板：三块内容
    ///   1. Microsoft 账号（正版）—— 决定进游戏时用谁的 UUID / 令牌
    ///   2. XML 账号—— 联机、流量额度都用它，名字和头像独立设置
    ///   3. 离线账号 —— 不登录任何账号时的游戏内名字
    /// </summary>
    private async Task BuildAccountDialogAsync()
    {
        // ---------- 离线账号 ----------
        var nameBox = new TextBox
        {
            Text = AppState.Settings.PlayerName,
            PlaceholderText = "1–16 个字符，支持中文",
            MaxLength = 16
        };

        var pickButton = new Button { Content = "选择图片…", FontSize = 13 };
        var resetButton = new HyperlinkButton { Content = "用首字母", FontSize = 13 };
        var avatarHint = new TextBlock
        {
            Text = "没设置图片时用名字首字母。正版账号会优先显示角色头像。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        // ---------- XML 账号 ----------
        bool serverLogged = LinxAccountService.IsLoggedIn;

        var serverUserBox = new TextBox { PlaceholderText = "用户名（3–24 位）", Width = 170 };
        var serverPassBox = new PasswordBox { PlaceholderText = "密码（至少 6 位）", Width = 170 };
        // 服务端注册强制邮箱验证：只支持国内邮箱，验证码 10 分钟内有效、60 秒才能再发一次
        var serverEmailBox = new TextBox { PlaceholderText = "邮箱（QQ / 163 / 126…）", Width = 200 };
        var serverCodeBox = new TextBox { PlaceholderText = "邮箱验证码", Width = 110, MaxLength = 6 };
        var serverSendCodeBtn = new Button { Content = "发送验证码" };
        var serverLoginBtn = new Button { Content = "登录" };
        var serverRegisterBtn = new Button { Content = "注册" };
        var serverLogoutBtn = new HyperlinkButton { Content = "退出 XML 账号" };
        var serverPickAvatarBtn = new Button { Content = "换头像…", FontSize = 13 };

        var serverStatus = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
            Text = serverLogged
                ? $"已登录：{LinxAccountService.DisplayName}"
                : "XML 账号用于登录官方服务器。注册后可以在这里直接登录。"
        };

        var serverGuestPanel = new StackPanel { Spacing = 8 };
        serverGuestPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { serverUserBox, serverPassBox, serverLoginBtn }
        });
        serverGuestPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { serverEmailBox, serverCodeBox, serverSendCodeBtn, serverRegisterBtn }
        });
        serverGuestPanel.Children.Add(new TextBlock
        {
            Text = "注册要邮箱验证（只支持国内邮箱）；老账号填完用户名密码直接登录。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        });

        var serverManageBtn = new HyperlinkButton { Content = "管理账号" };
        serverManageBtn.Click += (_, _) => OpenServerWeb();

        var serverMemberPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = serverLogged ? Visibility.Visible : Visibility.Collapsed,
            Children = { serverPickAvatarBtn, serverManageBtn, serverLogoutBtn }
        };

        if (serverLogged) serverGuestPanel.Visibility = Visibility.Collapsed;

        // ---------- 正版账号那一行 ----------
        var msLoginButton = new Button { Content = "使用 Microsoft 账号登录", MinWidth = 0 };
        var msSwitchButton = new HyperlinkButton
        {
            Content = "切回正版",
            Visibility = !AccountService.IsMicrosoft && !string.IsNullOrEmpty(AppState.Settings.MsRefreshToken)
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        var msLogoutButton = new HyperlinkButton
        {
            Content = "退出正版账号",
            Visibility = AccountService.IsMicrosoft ? Visibility.Visible : Visibility.Collapsed
        };
        var msRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { msLoginButton, msSwitchButton, msLogoutButton }
        };

        // ---------- 对话框 ----------
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "账号",
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Microsoft 账号（正版）",
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
                        Text = AccountService.IsMicrosoft
                            ? $"已登录正版账号：{AppState.Settings.MsName}"
                            : (string.IsNullOrEmpty(AppState.Settings.MsRefreshToken)
                                ? "还没有登录过微软账号。"
                                : $"上次登录过：{AppState.Settings.MsName}（已切回离线）")
                    },
                    msRow,
                    Divider(),
                    new TextBlock
                    {
                        Text = "XML 账号",
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    serverStatus,
                    serverGuestPanel,
                    serverMemberPanel,
                    Divider(),
                    new TextBlock
                    {
                        Text = "离线账号",
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new TextBlock { Text = "不登录任何账号时，游戏里用的名字。", FontSize = 13 },
                    nameBox,
                    avatarHint,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { pickButton, resetButton }
                    }
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        FontService.ApplyToDialog(dialog);

        // ---------- 事件 ----------
        msLoginButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await Task.Delay(250);
            await MicrosoftLoginAsync();
        };

        msSwitchButton.Click += (_, _) =>
        {
            AccountService.SwitchToMicrosoft();
            RefreshAccountUi();
            dialog.Hide();
            Toast("已切回正版账号");
        };

        msLogoutButton.Click += async (_, _) =>
        {
            // ★ 这个账号面板本身就是一只 ContentDialog。
            //   不先把它关掉就弹确认框，会直接撞上「Only a single ContentDialog can be open
            //   at any time」（COMException 0x80000019）—— 之前点「退出正版账号」报错就是这里。
            dialog.Hide();
            await Task.Delay(240);

            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "退出正版账号？",
                Content = "退出后会用离线账号启动，下次要重新走一遍微软登录。",
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            FontService.ApplyToDialog(confirm);

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                AccountService.Logout();
                RefreshAccountUi();
                Toast("已退出正版账号");
            }
            else
            {
                // 取消就回到账号面板，别把用户留在外面
                await Task.Delay(220);
                await BuildAccountDialogAsync();
            }
        };

        // 发送验证码：倒计时跟着服务端的 60 秒冷却走
        var codeTimer = DispatcherQueue.CreateTimer();
        codeTimer.Interval = TimeSpan.FromSeconds(1);
        codeTimer.IsRepeating = true;
        var cooldown = 0;
        codeTimer.Tick += (_, _) =>
        {
            cooldown--;
            if (cooldown <= 0)
            {
                cooldown = 0;
                codeTimer.Stop();
                serverSendCodeBtn.IsEnabled = true;
                serverSendCodeBtn.Content = "发送验证码";
            }
            else
            {
                serverSendCodeBtn.Content = $"重发({cooldown}s)";
            }
        };

        serverSendCodeBtn.Click += async (_, _) =>
        {
            var email = serverEmailBox.Text.Trim();
            if (!email.Contains('@'))
            {
                serverStatus.Text = "先填一个邮箱（只支持 QQ / 163 / 126 这类国内邮箱）。";
                return;
            }

            serverSendCodeBtn.IsEnabled = false;
            serverStatus.Text = "正在发送验证码…";
            try
            {
                var (message, debug) = await LinxAccountService.SendEmailCodeAsync(email, "register", default);
                if (!string.IsNullOrEmpty(debug)) serverCodeBox.Text = debug;
                serverStatus.Text = string.IsNullOrEmpty(debug)
                    ? message
                    : message + "（测试服验证码：" + debug + "）";
                cooldown = 60;
                codeTimer.Start();
            }
            catch (Exception ex)
            {
                serverSendCodeBtn.IsEnabled = true;
                serverStatus.Text = ex.Message;
            }
        };

        serverLoginBtn.Click += async (_, _) =>
        {
            var user = serverUserBox.Text.Trim();
            var pass = serverPassBox.Password;

            if (user.Length < 3 || pass.Length < 6)
            {
                serverStatus.Text = "用户名至少 3 位、密码至少 6 位。";
                return;
            }

            try
            {
                serverStatus.Text = "正在登录…";
                if (!await ServerLoginAsync(user, pass, serverStatus)) return;

                serverStatus.Text = $"已登录：{user}";
                serverGuestPanel.Visibility = Visibility.Collapsed;
                serverMemberPanel.Visibility = Visibility.Visible;
                serverPassBox.Password = "";
                serverCodeBox.Text = "";

                RefreshAccountUi();
                Toast($"已登录 XML 账号「{user}」");

                await SyncLocalAvatarAsync();
            }
            catch (Exception ex)
            {
                serverStatus.Text = ex.Message;
            }
        };

        serverRegisterBtn.Click += async (_, _) =>
        {
            var user = serverUserBox.Text.Trim();
            var pass = serverPassBox.Password;
            var email = serverEmailBox.Text.Trim();
            var code = serverCodeBox.Text.Trim();

            if (user.Length < 3 || pass.Length < 6)
            {
                serverStatus.Text = "用户名至少 3 位、密码至少 6 位。";
                return;
            }
            if (!email.Contains('@'))
            {
                serverStatus.Text = "注册要填邮箱（只支持国内邮箱）。";
                return;
            }
            if (code.Length < 4)
            {
                serverStatus.Text = "先点「发送验证码」，把邮件里的 6 位数字填进来。";
                return;
            }

            serverRegisterBtn.IsEnabled = false;
            try
            {
                serverStatus.Text = "正在注册…";
                var message = await LinxAccountService.RegisterAsync(user, pass, email, code, default);

                serverStatus.Text = "注册成功，正在登录…";
                var result = await LinxAccountService.LoginAsync(user, pass, null, default);
                if (result.NeedCode)
                {
                    serverStatus.Text = "账号已注册。这个账号开了邮箱二次验证，请点「登录」再输一次验证码。";
                    return;
                }

                serverStatus.Text = $"已登录：{user}";
                serverGuestPanel.Visibility = Visibility.Collapsed;
                serverMemberPanel.Visibility = Visibility.Visible;
                serverPassBox.Password = "";
                serverCodeBox.Text = "";

                RefreshAccountUi();
                Toast(message);
            }
            catch (Exception ex)
            {
                serverStatus.Text = ex.Message;
            }
            finally
            {
                serverRegisterBtn.IsEnabled = true;
            }
        };

        serverLogoutBtn.Click += (_, _) =>
        {
            LinxAccountService.Logout();
            serverStatus.Text = "已退出 XML 账号。";
            serverGuestPanel.Visibility = Visibility.Visible;
            serverMemberPanel.Visibility = Visibility.Collapsed;
            RefreshAccountUi();
        };

        serverPickAvatarBtn.Click += async (_, _) =>
        {
            var path = await PickImageAsync("server-avatar");
            if (path is null) return;

            AppState.Settings.ServerAvatarPath = path;
            SettingsService.Save();
            RefreshAccountUi();

            // 顺手同步到服务器，网页端和别的设备就能看到同一个头像
            var uploaded = await LinxAccountService.UploadAvatarAsync(path, default);
            Toast(uploaded ? "XML 账号头像已更新（已同步到服务器）" : "XML 账号头像已更新（同步到服务器失败，稍后会重试）");
        };

        pickButton.Click += async (_, _) => await PickAvatarAsync();
        resetButton.Click += (_, _) =>
        {
            ClearAvatar();
            resetButton.Content = "已恢复首字母";
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = "Steve";

            AppState.Settings.PlayerName = name;
            SettingsService.Save();
            RefreshAccountUi();
            Toast($"离线账号已改为「{name}」");
        }
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 2),
        Background = (Brush)Application.Current.Resources["DividerBrush"]
    };

    /// <summary>选一张图片复制到配置目录，返回新路径；取消返回 null。</summary>
    private async Task<string?> PickImageAsync(string fileNamePrefix)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return null;

            Directory.CreateDirectory(Paths.AppData);
            var dest = Path.Combine(Paths.AppData,
                fileNamePrefix + Path.GetExtension(file.Name).ToLowerInvariant());
            File.Copy(file.Path, dest, true);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Error("PickImage", ex);
            Toast("选择图片失败：" + ex.Message, true);
            return null;
        }
    }

    /// <summary>
    /// 侧边栏收起时只有 48 DIP 宽，名字文字会被裁得只剩半截。
    /// 收起 → 只留居中头像；展开 → 头像 + 名字齐全。
    /// </summary>
    private void UpdateAccountLayout()
    {
        bool compact = !NavView.IsPaneOpen;

        // 收起时右边那列文字要真正让出宽度：* 列会把头像挤到左边，看着就是「圆没居中」
        AccountTexts.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AccountTextColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        AccountGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        AvatarHost.HorizontalAlignment = HorizontalAlignment.Left;
        AccountButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        // 48 DIP 图标栏里放 30 的头像：左右各留 9 才是正中（不靠对齐猜，直接算出来）。
        // 展开时回到常规内边距，头像靠左和菜单图标对齐。
        AccountHost.Margin = compact ? new Thickness(0, 6, 0, 12) : new Thickness(4, 4, 4, 10);
        AccountButton.Padding = compact ? new Thickness(9, 7, 9, 7) : new Thickness(8, 6, 8, 6);
        AccountButton.HorizontalContentAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        AccountButton.MinHeight = 44;
    }

    // ================= 轻提示 =================
    private InfoBar? _toast;

    public void Toast(string message, bool error = false)
    {
        try
        {
            _toast?.IsOpen = false;
            var drift = new TranslateTransform { Y = 70 };
            _toast = new InfoBar
            {
                Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success,
                Message = message,
                IsOpen = true,
                IsClosable = false,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 28, 24),
                RenderTransform = drift,
                Opacity = 0,
                FontFamily = FontService.BodyFont
            };

            if (Content is Grid g)
            {
                Grid.SetRowSpan(_toast, 2);
                g.Children.Add(_toast);
                FontService.Apply(_toast);

                // Q 弹：从右下角下方 70 DIP 弹上来，过冲一点再回落
                var sb = new Storyboard();
                var slide = new DoubleAnimation
                {
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(620)),
                    EasingFunction = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slide, drift);
                Storyboard.SetTargetProperty(slide, "Y");
                sb.Children.Add(slide);

                var fade = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180))
                };
                Storyboard.SetTarget(fade, _toast);
                Storyboard.SetTargetProperty(fade, "Opacity");
                sb.Children.Add(fade);
                sb.Begin();
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(2.6);
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (_toast is null) return;

                    // 退场：向下滑 + 淡出，动画结束再真正关掉，不再瞬间消失
                    var exit = new Storyboard();
                    var fadeOut = new DoubleAnimation
                    {
                        To = 0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(260))
                    };
                    Storyboard.SetTarget(fadeOut, _toast);
                    Storyboard.SetTargetProperty(fadeOut, "Opacity");
                    exit.Children.Add(fadeOut);

                    var sink = new DoubleAnimation
                    {
                        To = 26,
                        Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    Storyboard.SetTarget(sink, drift);
                    Storyboard.SetTargetProperty(sink, "Y");
                    exit.Children.Add(sink);

                    exit.Completed += (_, _) =>
                    {
                        if (_toast is not null) _toast.IsOpen = false;
                    };
                    exit.Begin();
                };
                timer.Start();
            }
        }
        catch { }
    }
}

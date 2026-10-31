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
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
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
    // 概览/加载器三列卡片需要约 840 DIP 宽度，另留滚动条和取整余量。
    private const double MinimumClientWidth = 860;
    private const double MinimumClientHeight = 640;
    private XamlRoot? _sizeRoot;
    private bool _updatingMinimumSize;

    private double WindowScale()
    {
        if (Content?.XamlRoot is { RasterizationScale: > 0 } root)
            return root.RasterizationScale;
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private void ApplyWindowMinimumSize()
    {
        if (_updatingMinimumSize || AppWindow.Presenter is not OverlappedPresenter presenter ||
            presenter.State == OverlappedPresenterState.Minimized) return;

        _updatingMinimumSize = true;
        try
        {
            double scale = WindowScale();
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
            int frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            // Presenter 限制整个窗口的物理像素尺寸；逻辑客户区下限需计入缩放及非客户区。
            // 小屏/高缩放时，以工作区为限，保证窗口仍能放进屏幕。
            int width = Math.Min((int)Math.Ceiling(MinimumClientWidth * scale) + frameWidth, Math.Max(1, area.Width - 40));
            int height = Math.Min((int)Math.Ceiling(MinimumClientHeight * scale) + frameHeight, Math.Max(1, area.Height - 40));
            if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
            if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
        }
        finally { _updatingMinimumSize = false; }
    }

    private void SizeRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        ApplyWindowMinimumSize();
        SyncTitleBarHeight();
        QueueTitleBarLayout();
    }

    private void Window_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidPresenterChange) ApplyWindowMinimumSize();
        if (args.DidSizeChange || args.DidPresenterChange || args.DidPositionChange) QueueTitleBarLayout();
    }

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
        double scale = WindowScale();
        ApplyWindowMinimumSize();

        int wantW = (int)Math.Round(860 * scale);
        int wantH = (int)Math.Round(780 * scale);

        Log.Info($"窗口尺寸 scale={scale} dpi={GetDpiForWindow(hwnd)} root={(Content?.XamlRoot is not null)} → {wantW}x{wantH}");

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        int w = Math.Min(wantW, Math.Max(1, area.Width - 40 - frameWidth));
        int h = Math.Min(wantH, Math.Max(1, area.Height - 40 - frameHeight));

        AppWindow.ResizeClient(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));

        SyncTitleBarHeight();

        GetClientRect(hwnd, out var rc);
        Log.Info($"ResizeClient({w}x{h}) → 实际客户区 {rc.Right - rc.Left}x{rc.Bottom - rc.Top}");
    }


    /// <summary>
    /// 把内容延伸到标题栏：材料（云母 / 亚克力）才能一路铺到窗口最顶上，
    /// 不再有「系统标题栏一块实色 + 下面一块材质」的接缝。
    /// TitleBarStrip 就是拖拽区，按钮由系统按 AppWindowTitleBar 的配色画在它右边。
    /// 很老的系统不支持定制标题栏时直接放弃，退回系统标题栏。
    /// </summary>
    private bool? _newTitleBarActive;
    private bool _titleBarLayoutQueued;

    private void TitleBarLayout_SizeChanged(object sender, SizeChangedEventArgs e) => QueueTitleBarLayout();

    private void QueueTitleBarLayout()
    {
        if (_titleBarLayoutQueued || _newTitleBarActive != true || !RootGrid.IsLoaded) return;
        _titleBarLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_newTitleBarActive != true || !RootGrid.IsLoaded || RootGrid.XamlRoot is null) return;
                SyncTitleBarHeight();
                RootGrid.UpdateLayout();
                double available = TitleBarSearchHost.ActualWidth;
                if (available <= 0 || RootGrid.ActualWidth <= 0) return;

                // Canvas 的定位不参与内容期望宽度，避免旧窗口尺寸留下的外边距挤掉原生标题。
                double width = Math.Min(300, Math.Max(0, available - 16));
                double origin = TitleBarSearchHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point()).X;
                double left = Math.Clamp((RootGrid.ActualWidth - width) / 2 - origin, 0, available - width);
                TitleBarSearchBox.Width = width;
                Canvas.SetLeft(TitleBarSearchBox, left);
                TitleBarSearchHost.UpdateLayout();
                NativeTitleBar.RecomputeDragRegions();
            }
            finally { _titleBarLayoutQueued = false; }
        })) _titleBarLayoutQueued = false;
    }

    private void SetupTitleBar()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                TitleBarStrip.Visibility = NativeTitleBar.Visibility = Visibility.Collapsed;
                return;
            }

            bool useNew = AppState.Settings.UseNewTitleBar;
            if (_newTitleBarActive == useNew) return;
            _newTitleBarActive = useNew;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
            NativeTitleBar.Visibility = useNew ? Visibility.Visible : Visibility.Collapsed;
            TitleBarStrip.Visibility = useNew ? Visibility.Collapsed : Visibility.Visible;
            AppWindow.TitleBar.PreferredHeightOption = useNew ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;
            SetTitleBar(useNew ? NativeTitleBar : TitleBarStrip);
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            SyncTitleBarHeight();
            QueueTitleBarLayout();
        }
        catch (Exception ex)
        {
            Log.Warn("内容延伸标题栏失败：" + ex.Message);
        }
    }

    /// <summary>拖拽条高度 = 系统标题栏实际高度换算成 DIP（125% 缩放下是 40 而不是 32）。</summary>
    private void SyncTitleBarHeight()
    {
        try
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0.1) scale = 1.0;

            if (_newTitleBarActive == true)
            {
                // 原生窗口右侧保留三个系统按钮的位置，按实际 DPI 换算每个按钮的宽度。
                double width = AppWindow.TitleBar.RightInset / (3 * scale);
                if (width >= 24 && width <= 96) TitleBarAccountButton.Width = width;
                TitleBarAccountButton.Height = NativeTitleBar.Height;
                return;
            }

            double height = AppWindow.TitleBar.Height / scale;
            if (height >= 16 && height <= 96) TitleBarStrip.Height = height;
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        // 任务栏 / Alt-Tab 的图标走 WM_SETICON：AppWindow.SetIcon 只改标题栏，
        // 任务栏那个窗口按钮会一直是系统默认的白纸图标。
        WindowIcon.Apply(this);

        // 主题引擎：窗口材质（云母 / 亚克力 / 高透）、主题色、深浅模式。
        // 设置页每次改动都会调 ThemeService.Apply()，这里只负责首次应用。
        ThemeService.Initialize(this, RootGrid, NavView);
        NavigationPaneBackground.Attach(NavView);

        // 标题栏：内容延伸到标题栏，材质才铺得到最顶上。
        // 以前用系统标题栏，它由系统单独画一条实色，云母 / 亚克力下会和窗口之间出现一道缝。
        SetupTitleBar();
        ThemeService.AppearanceChanged += SetupTitleBar;
        ApplyWindowMinimumSize();
        RootGrid.Loaded += (_, _) =>
        {
            _sizeRoot = RootGrid.XamlRoot;
            _sizeRoot.Changed += SizeRoot_Changed;
            ApplyWindowMinimumSize();
            QueueTitleBarLayout();
        };
        AppWindow.Changed += Window_Changed;
        Closed += (_, _) =>
        {
            AppWindow.Changed -= Window_Changed;
            ThemeService.AppearanceChanged -= SetupTitleBar;
            if (_sizeRoot is not null) _sizeRoot.Changed -= SizeRoot_Changed;
        };
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

            // 主题要在窗口真正激活后再刷一次：RequestedTheme 在 Activate 之前设的
            // 深浅色会被 XamlRoot 初始化时覆盖掉，表现就是「明明存的是深色，开出来还是浅色」。
            ThemeService.Apply();

            // 联网检查（公告 + 强制更新）放后台，连不上服务器不影响正常使用
            if (AppState.Settings.OnboardingDone) _ = RunStartupChecksAsync();
        };

        AppState.InstalledChanged += () => DispatcherQueue.TryEnqueue(RefreshAccountUi);
        AppState.Downloads.ActiveChanged += () => DispatcherQueue.TryEnqueue(RefreshAccountUi);
        AccountStore.Instance.Changed += () => DispatcherQueue.TryEnqueue(RefreshAccountUi);

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
            "community" => typeof(CommunityPage),
            "tools" => typeof(ToolsPage),
            "accounts" => typeof(AccountsPage),
            "skins" => typeof(SkinsPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (target is null) return;
        Log.Info($"导航 → {tag}（当前 {ContentFrame.CurrentSourcePageType}）");

        if (ContentFrame.CurrentSourcePageType != target)
        {
            bool ok;
            try
            {
                NavigationTransitionInfo transition = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled
                    ? new EntranceNavigationTransitionInfo()
                    : new SuppressNavigationTransitionInfo();
                ok = ContentFrame.Navigate(target, parameter, transition);
            }
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

        // 账户 / 皮肤没有侧栏入口（从左下角头像进去）：清掉高亮，
        // 否则点了头像之后侧栏还停在「概览」上，看起来像没切走。
        NavView.SelectedItem = null;
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
        // 本地开发也跳过旧的强制更新缓存，保留缓存文件供正常模式继续使用。
        if (_startupChecked || RemoteService.IsLocalDevelopment) return;
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
                Foreground = ThemeService.Brush("SecondaryTextBrush")
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "这次不是强制更新，不更新也能继续用。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("SecondaryTextBrush")
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
                Foreground = ThemeService.Brush("NoticeTextBrush")
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
                    Foreground = ThemeService.Brush("SecondaryTextBrush")
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = offline
                    ? "请连上网络后点「重试」。这个提示会一直保留，断网也不会绕过。"
                    : "这一版必须更新后才能继续使用启动器。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeService.Brush("NoticeTextBrush")
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
            Foreground = ThemeService.Brush("SecondaryTextBrush")
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
                status.Text = "这个账号开了邮箱二次验证，请先跳过向导，到「设置 → XML 账号」里登录。";
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
                Foreground = ThemeService.Brush("SecondaryTextBrush")
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
    public void OpenServerWeb()
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

    private async void ApplyAvatar()
    {
        try
        {
            var settings = AppState.Settings;

            // 1. 服务器（XML）账号有自己的头像：它是独立身份，优先显示
            if (LinxAccountService.IsLoggedIn)
            {
                var serverPath = settings.ServerAvatarPath;
                if (!string.IsNullOrWhiteSpace(serverPath) && File.Exists(serverPath))
                {
                    ApplyAvatarFile(serverPath!);
                    return;
                }
            }
            else
            {
                // 2. 当前游戏账户的皮肤：本地渲染出头像（正版 / 离线都走这条路）
                //    这样换皮肤、换账户，角上的头像会立刻跟着变，也不再依赖 mc-heads。
                var skin = await AccountService.GetAvatarAsync(60);
                if (skin is not null)
                {
                    AvatarBrush.ImageSource = skin;
                    AvatarImage.Visibility = Visibility.Visible;
                    AvatarText.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            // 3. 用户自己挑的图片
            if (!string.IsNullOrWhiteSpace(settings.AvatarPath) && File.Exists(settings.AvatarPath))
            {
                ApplyAvatarFile(settings.AvatarPath!);
                return;
            }

            // 4. 什么都没有：回到首字母
            AvatarBrush.ImageSource = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Avatar", ex);
        }
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
            Foreground = ThemeService.Brush("SecondaryTextBrush"),
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
    public Task ShowAccountDialogAsync()
    {
        NavigateTo("accounts");
        return Task.CompletedTask;
    }

    public void AccountButton_Click(object sender, RoutedEventArgs e) => NavigateTo("accounts");

    /// <summary>
    /// 账号面板：三块内容
    ///   1. Microsoft 账号（正版）—— 决定进游戏时用谁的 UUID / 令牌
    ///   2. XML 账号—— 联机、流量额度都用它，名字和头像独立设置
    ///   3. 离线账号 —— 不登录任何账号时的游戏内名字
    /// </summary>
    private async Task BuildAccountDialogAsync()
    {
        var s = AppState.Settings;
        var hasMs = !string.IsNullOrEmpty(s.MsRefreshToken);
        var serverLogged = LinxAccountService.IsLoggedIn;

        // 当前用哪个身份进游戏
        var mode = AccountService.IsMicrosoft ? "ms" : "offline";

        // ---------- 离线账号 ----------
        var nameBox = new TextBox
        {
            Text = s.PlayerName,
            PlaceholderText = "1–16 个字符，支持中文",
            MaxLength = 16
        };

        var pickButton = new Button { Content = "选择图片…", FontSize = 13 };
        var resetButton = new HyperlinkButton { Content = "用首字母", FontSize = 13 };
        var avatarHint = new TextBlock
        {
            Text = "没设置图片时用名字首字母。正版账号会优先显示角色头像。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("SecondaryTextBrush")
        };

        // ---------- XML 账号 ----------
        var serverUserBox = new TextBox { PlaceholderText = "用户名（3–24 位）", Width = 170 };
        var serverPassBox = new PasswordBox { PlaceholderText = "密码（至少 6 位）", Width = 170 };
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
            Foreground = ThemeService.Brush("SecondaryTextBrush"),
            Text = serverLogged ? $"已登录：{LinxAccountService.DisplayName}" : "未登录。联机大厅和云端存档要用它。"
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
            Foreground = ThemeService.Brush("SecondaryTextBrush")
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

        // ---------- 正版 ----------
        var msLoginButton = new Button { Content = "使用 Microsoft 账号登录", MinWidth = 0 };
        var msLogoutButton = new HyperlinkButton
        {
            Content = "退出正版账号",
            Visibility = AccountService.IsMicrosoft ? Visibility.Visible : Visibility.Collapsed
        };

        var msStatus = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("SecondaryTextBrush"),
            Text = AccountService.IsMicrosoft
                ? $"已登录：{s.MsName}"
                : (hasMs ? $"上次登录过 {s.MsName}，点保存就切回正版。" : "还没登录过微软账号。")
        };

        // ---------- 两个选择卡 ----------
        var brand = ThemeService.Brush("BrandBrush");
        var divider = ThemeService.Brush("DividerBrush");
        var secondary = (SolidColorBrush)ThemeService.Brush("SecondaryTextBrush");
        var brandWash = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(18, 0, 168, 142));

        // 先声明后赋值：MakeChoice 里的 lambda 会引用它们，
        // 直接 var 声明在下面会撞 CS0165「使用了未赋值的局部变量」。
        StackPanel msPanel = null!, offlinePanel = null!;
        var dots = new Dictionary<string, (Border Card, Ellipse Core)>();
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        (Border card, TextBlock sub) MakeChoice(string tag, string title, string subtitle)
        {
            var ring = new Ellipse
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 1.5,
                Stroke = divider
            };
            var core = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = brand,
                Visibility = Visibility.Collapsed
            };
            var dotHost = new Grid { Width = 16, Height = 16 };
            dotHost.Children.Add(ring);
            dotHost.Children.Add(core);

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            var subText = new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                MaxWidth = 148,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = secondary
            };

            var col = new StackPanel { Spacing = 3, Children = { titleText, subText } };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { dotHost, col }
            };

            var card = new Border
            {
                Width = 198,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = divider,
                Child = row
            };
            CardAppearance.SetIsCard(card, true);

            card.Tapped += (_, _) => SelectMode(tag);
            dots[tag] = (card, core);
            return (card, subText);
        }

        var (msCard, msSub) = MakeChoice("ms", "Microsoft 正版",
            AccountService.IsMicrosoft ? s.MsName : (hasMs ? s.MsName + "（未启用）" : "未登录"));
        var (offlineCard, offlineSub) = MakeChoice("offline", "离线账号", s.PlayerName);

        choices.Children.Add(msCard);
        choices.Children.Add(offlineCard);

        // ---------- 详情区 ----------
        msPanel = new StackPanel
        {
            Spacing = 8,
            Children = { msStatus, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { msLoginButton, msLogoutButton } } }
        };

        offlinePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "游戏里用的名字", FontSize = 13 },
                nameBox,
                avatarHint,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pickButton, resetButton } }
            }
        };

        void SelectMode(string tag)
        {
            mode = tag;

            foreach (var pair in dots)
            {
                var on = pair.Key == tag;
                pair.Value.Card.BorderBrush = on ? brand : divider;
                pair.Value.Card.BorderThickness = new Thickness(on ? 1.5 : 1);
                pair.Value.Card.Background = on ? brandWash : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                pair.Value.Core.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }

            msPanel.Visibility = tag == "ms" ? Visibility.Visible : Visibility.Collapsed;
            offlinePanel.Visibility = tag == "offline" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- 对话框 ----------
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "账号",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "用哪个身份进游戏",
                        FontSize = 12.5,
                        Foreground = secondary
                    },
                    choices,
                    msPanel,
                    offlinePanel,
                    Divider(),
                    new Expander
                    {
                        Header = serverLogged
                            ? $"XML 账号（联机服务）· 已登录 {LinxAccountService.DisplayName}"
                            : "XML 账号（联机服务）· 未登录",
                        IsExpanded = false,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = new StackPanel
                        {
                            Spacing = 8,
                            Children = { serverStatus, serverGuestPanel, serverMemberPanel }
                        }
                    }
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        FontService.ApplyToDialog(dialog);

        // 主按钮跟品牌色走：ContentDialog 的按钮吃的是这几支 Accent 资源，
        // 直接改 PrimaryButtonStyle 会被模板里的 ThemeResource 盖掉。
        var brandBrush = ThemeService.Brush("BrandBrush");
        var brandHover = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 150, 128));
        var brandPress = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 132, 112));
        var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

        dialog.Resources["AccentButtonBackground"] = brandBrush;
        dialog.Resources["AccentButtonBackgroundPointerOver"] = brandHover;
        dialog.Resources["AccentButtonBackgroundPressed"] = brandPress;
        dialog.Resources["AccentButtonForeground"] = whiteBrush;
        dialog.Resources["AccentButtonForegroundPointerOver"] = whiteBrush;
        dialog.Resources["AccentButtonForegroundPressed"] = whiteBrush;

        SelectMode(mode);

        // ---------- 事件 ----------
        msLoginButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await Task.Delay(250);
            await MicrosoftLoginAsync();
        };

        msLogoutButton.Click += async (_, _) =>
        {
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
                var (message, _) = await LinxAccountService.SendEmailCodeAsync(email, "register", default);
                if (message.Contains("已发送") || message.Contains("发送成功"))
                {
                    serverStatus.Text = $"验证码已发到 {email}，10 分钟内有效。";
                    cooldown = 60;
                    codeTimer.Start();
                }
                else
                {
                    serverStatus.Text = message;
                    serverSendCodeBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                serverStatus.Text = "发送失败：" + ex.Message;
                serverSendCodeBtn.IsEnabled = true;
            }
        };

        serverLoginBtn.Click += async (_, _) =>
        {
            var user = serverUserBox.Text.Trim();
            var pass = serverPassBox.Password;

            if (user.Length < 3 || pass.Length < 6)
            {
                serverStatus.Text = "用户名至少 3 位，密码至少 6 位。";
                return;
            }

            serverStatus.Text = "正在登录…";
            try
            {
                var result = await LinxAccountService.LoginAsync(user, pass, null, default);
                if (result.LoggedIn)
                {
                    serverStatus.Text = "登录成功：" + LinxAccountService.DisplayName;
                    serverGuestPanel.Visibility = Visibility.Collapsed;
                    serverMemberPanel.Visibility = Visibility.Visible;
                    RefreshAccountUi();
                    return;
                }

                if (result.NeedCode)
                {
                    // 服务端开了邮箱二次验证：这轮补验证码再登一次
                    var code = serverCodeBox.Text.Trim();
                    if (code.Length < 4)
                    {
                        serverStatus.Text = result.Hint ?? "这个账号要先过邮箱验证，把验证码填上再点登录。";
                        return;
                    }

                    var again = await LinxAccountService.LoginAsync(user, pass, code, default);
                    if (again.LoggedIn)
                    {
                        serverStatus.Text = "登录成功：" + LinxAccountService.DisplayName;
                        serverGuestPanel.Visibility = Visibility.Collapsed;
                        serverMemberPanel.Visibility = Visibility.Visible;
                        RefreshAccountUi();
                        return;
                    }

                    serverStatus.Text = again.Hint ?? "验证码不对。";
                    return;
                }

                serverStatus.Text = result.Hint ?? "登录失败。";
            }
            catch (Exception ex)
            {
                serverStatus.Text = "登录失败：" + ex.Message;
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
                serverStatus.Text = "用户名至少 3 位，密码至少 6 位。";
                return;
            }

            if (!email.Contains('@'))
            {
                serverStatus.Text = "注册需要邮箱（只支持国内邮箱）。";
                return;
            }

            if (code.Length < 4)
            {
                serverStatus.Text = "先点「发送验证码」，把收到的 6 位验证码填上。";
                return;
            }

            serverRegisterBtn.IsEnabled = false;
            serverStatus.Text = "正在注册…";
            try
            {
                var message = await LinxAccountService.RegisterAsync(user, pass, email, code, default);
                if (string.IsNullOrWhiteSpace(message))
                {
                    serverStatus.Text = "注册成功，已自动登录：" + LinxAccountService.DisplayName;
                    serverGuestPanel.Visibility = Visibility.Collapsed;
                    serverMemberPanel.Visibility = Visibility.Visible;
                    RefreshAccountUi();
                }
                else
                {
                    serverStatus.Text = message;
                }
            }
            catch (Exception ex)
            {
                serverStatus.Text = "注册失败：" + ex.Message;
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

            s.ServerAvatarPath = path;
            SettingsService.Save();
            RefreshAccountUi();

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

            s.PlayerName = name;

            if (mode == "ms")
            {
                if (hasMs)
                {
                    AccountService.SwitchToMicrosoft();
                    Toast($"已切换到正版账号 {s.MsName}");
                }
                else
                {
                    AccountService.UseOffline();
                    Toast("还没登录过微软账号，先用离线账号启动。");
                }
            }
            else
            {
                AccountService.UseOffline();
                Toast($"离线账号已改为「{name}」");
            }

            SettingsService.Save();
            RefreshAccountUi();
            offlineSub.Text = name;
            msSub.Text = AccountService.IsMicrosoft ? s.MsName : (hasMs ? s.MsName + "（未启用）" : "未登录");
        }
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 2),
        Background = ThemeService.Brush("DividerBrush")
    };

    /// <summary>选一张图片复制到配置目录，返回新路径；取消返回 null。</summary>
    public async Task<string?> PickImageAsync(string fileNamePrefix)
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

        // 展开、收起时头像始终位于同一坐标，只切换右侧文字。
        AccountHost.Margin = new Thickness(0, 6, 0, 12);
        AccountButton.Padding = new Thickness(9, 7, 9, 7);
        AccountButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
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

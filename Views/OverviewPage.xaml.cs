using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using LinxLauncher.Controls;
using LinxLauncher.Models;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class OverviewPage : Page
{
    private FeatureCard[] _cards = Array.Empty<FeatureCard>();

    public OverviewPage()
    {
        InitializeComponent();
        _cards = new[] { VersionCard, LoaderCard, SpeedCard, InstalledCard, JavaCard, TaskCard };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    public void Refresh()
    {
        AppState.Init();
        AppState.RefreshInstalled();

        var cur = AppState.Current;

        // ---- 当前版本 ----
        VersionCard.Header = "当前版本";
        if (cur is null)
        {
            VersionCard.Status = "还没有安装任何版本。前往版本库下载一个即可开始。";
        }
        else
        {
            VersionCard.Status = cur.IsComplete
                ? $"{cur.Id} · {cur.DisplayKind} · 可以直接启动。"
                : $"{cur.Id} · 安装未完成，启动可能失败。";
        }

        // ---- 模组加载器 ----
        var loaders = AppState.Installed
            .Where(v => v.DisplayKind is not "原版")
            .Select(v => v.DisplayKind)
            .Distinct()
            .ToList();

        LoaderCard.Header = "模组加载器";
        LoaderCard.Status = loaders.Count == 0
            ? "支持 Forge、Fabric、NeoForge 与 Quilt，可在版本库里叠加安装。"
            : $"已安装 {string.Join(" / ", loaders)}，共 {AppState.Installed.Count(v => v.DisplayKind != "原版")} 个实例。";

        // ---- 下载加速 ----
        SpeedCard.Header = "下载加速";
        SpeedCard.Status = Endpoints.UseMirror
            ? "BMCLAPI 国内镜像已启用，客户端、依赖库与资源文件都走镜像。"
            : "当前直连 Mojang 官方源，国内可能较慢。建议打开镜像。";

        // ---- 已安装版本 ----
        var totalBytes = AppState.Installed.Sum(v => v.SizeBytes);
        InstalledCard.Header = "已安装版本";
        InstalledCard.Status = AppState.Installed.Count == 0
            ? "本机还没有版本，磁盘占用为 0。"
            : $"共 {AppState.Installed.Count} 个版本，占用 {GameInstaller.FormatSize(totalBytes)}。";

        // ---- Java ----
        var javas = JavaService.Detect();
        JavaCard.Header = "Java 运行时";
        JavaCard.Status = javas.Count == 0
            ? "未检测到 Java 运行时。安装 Forge 与启动游戏都需要它。"
            : $"检测到 {javas.Count} 个运行时，最高 Java {javas.Max(j => j.Major)}（{javas[0].Arch}）。";

        // ---- 下载任务 ----
        var active = AppState.Downloads.Active;
        TaskCard.Header = "下载任务";
        TaskCard.Status = active is null
            ? "当前没有进行中的任务，历史记录保留在下载页。"
            : $"正在下载 {active.Title}，已完成 {active.PercentText}。";
    }

    // ================= Reveal 驱动 =================
    private void CardHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        foreach (var card in _cards)
        {
            var p = e.GetCurrentPoint(card);
            card.UpdateReveal(p.Position.X, p.Position.Y);
        }
    }

    private void CardHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        foreach (var card in _cards) card.ResetReveal();
    }

    // ================= 按钮 =================
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        var cur = AppState.Current;
        if (cur is null)
        {
            MainWindow.Instance?.NavigateTo("versions");
            return;
        }

        // 细节都在 GameSession 里：进度窗口 → 等游戏窗口 → 缩到托盘 → 盯进程 → 异常退出弹报错窗口
        await StartVersionAsync(cur.Id);
    }

    /// <summary>
    /// 「选择版本」：在当前版本卡片上弹出一个小浮窗，列出所有已安装实例，
    /// 点哪个就切到哪个并直接启动（和启动器里选版本再开游戏的动线一致）。
    /// </summary>
    private void VersionPick_Click(object sender, RoutedEventArgs e)
    {
        AppState.Init();

        // 每次打开都重新扫一遍 versions 目录：
        // 刚装完的 Fabric / Forge 实例是独立目录，别因为内存里的列表是旧的而看不到
        AppState.RefreshInstalled();

        // 直接用磁盘扫描结果，不依赖内存里的集合（避免任何缓存导致的"少一个版本"）
        var installed = AppState.Versions.ScanInstalled();
        Log.Info($"版本选择器：扫描到 {installed.Count} 个 → " + string.Join(", ", installed.Select(v => v.Id)));

        if (installed.Count == 0)
        {
            MainWindow.Instance?.NavigateTo("versions");
            return;
        }

        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        var panel = new StackPanel { Spacing = 2, MinWidth = 232 };

        panel.Children.Add(new TextBlock
        {
            Text = "选择要启动的版本",
            FontSize = 12,
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = ThemeService.Brush("SecondaryTextBrush")
        });

        foreach (var v in installed)
        {
            var row = new Button
            {
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4)
            };

            var isCurrent = v.Id == AppState.SelectedVersion;
            row.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = isCurrent ? v.Id + "  ·  当前" : v.Id,
                        FontSize = 13,
                        Foreground = ThemeService.Brush(isCurrent ? "PrimaryTextBrush" : "TertiaryTextBrush")
                    },
                    new TextBlock
                    {
                        Text = v.Subtitle,
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush("SecondaryTextBrush")
                    }
                }
            };

            row.Click += async (_, _) =>
            {
                flyout.Hide();
                await StartVersionAsync(v.Id);
            };

            panel.Children.Add(row);
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 6, 6, 6),
            Background = ThemeService.Brush("DividerBrush")
        });

        var manage = new HyperlinkButton { Content = "管理版本…", FontSize = 12.5 };
        manage.Click += (_, _) =>
        {
            flyout.Hide();
            MainWindow.Instance?.NavigateTo("versions");
        };
        panel.Children.Add(manage);

        // 浮窗默认不滚动，版本一多就会超出可用高度被裁掉（看起来像"少了一个版本"），
        // 所以内容套一层 ScrollViewer 并限制最大高度。
        flyout.Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto
        };

        flyout.Opened += (_, _) => FontService.Apply(panel);
        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>切到某个版本并启动它。</summary>
    private async Task StartVersionAsync(string versionId)
    {
        AppState.SelectedVersion = versionId;
        AppState.Settings.LastVersionId = versionId;
        SettingsService.Save();
        Refresh();

        await GameSession.StartAsync(versionId, XamlRoot);
    }

    private void GoVersions_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("versions");

    private void GoInstalled_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("versions", "installed");

    private void GoSettings_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("settings");

    private void GoDownloads_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("downloads");
}

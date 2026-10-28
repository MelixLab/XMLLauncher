using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Controls;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

/// <summary>
/// 联机页：三张入口卡片（和首页同一套样式），点按钮进二级对话框做事。
/// 开房 / 浏览大厅 / 加入房间分别是三个 ContentDialog。
/// </summary>
public sealed partial class RelayPage : Page
{
    private P2PSession? _host;
    private int _hostPort;
    private P2PSession? _guest;
    private NatInfo _nat = new(NatKind.Unknown, null, "");
    private DispatcherQueueTimer? _tick;
    private bool _busy;

    public RelayPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    private FeatureCard[] _cards = Array.Empty<FeatureCard>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 首页那套「鼠标跟随描边」是页面统一驱动的：这里也要把三张卡片收集起来
        _cards = new[] { HostCard, LobbyCard, JoinCard };

        RefreshHostCard();

        if (_nat.Kind == NatKind.Unknown) _ = DetectNatAsync();
        else ApplyNat(_nat);

        _tick ??= CreateTick();
        _tick.Start();

        // 自动化测试入口：设了 XML_TEST_JOIN=<房间号> 就直接加入，不用点界面
        var autoJoin = Environment.GetEnvironmentVariable("XML_TEST_JOIN");
        if (!string.IsNullOrWhiteSpace(autoJoin))
            _ = DispatcherQueue.TryEnqueue(async () => await JoinFlowAsync(autoJoin.Trim()));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _tick?.Stop();
    }

    // ==================== 卡片悬停描边 ====================

    private void CardHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in _cards)
        {
            var p = e.GetCurrentPoint(card);
            card.UpdateReveal(p.Position.X, p.Position.Y);
        }
    }

    private void CardHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in _cards) card.ResetReveal();
    }

    private DispatcherQueueTimer CreateTick()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => RefreshHostCard();
        return timer;
    }

    // ==================== 网络 ====================

    private async void Redetect_Click(object sender, RoutedEventArgs e)
    {
        _nat = new NatInfo(NatKind.Unknown, null, "");
        await DetectNatAsync();
    }

    private async Task DetectNatAsync()
    {
        NatText.Text = "网络检测中…";
        PublicAddrText.Text = "";

        try
        {
            var nat = await P2P.ProbeAsync(CancellationToken.None);
            _nat = nat;
            ApplyNat(nat);
        }
        catch (Exception ex)
        {
            NatText.Text = "检测失败：" + ex.Message;
        }
    }

    private void ApplyNat(NatInfo nat)
    {
        NatText.Text = nat.Kind == NatKind.Symmetric
            ? "对称型网络 —— 打洞大概率失败"
            : nat.Describe();
        PublicAddrText.Text = nat.Public is null ? "" : "公网 " + nat.Public;
    }

    // ==================== 卡片 1：开启新世界 ====================

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_host is not null)
        {
            await StopHostAsync();
            return;
        }

        if (!LinxAccountService.IsLoggedIn)
        {
            Toast("先在左下角登录 XML 账号，联机要用它认人。");
            return;
        }

        var lanPort = MultiplayerService.DetectLanPort();
        if (lanPort <= 0)
        {
            Toast("没找到游戏局域网端口：先在游戏里点「对局域网开放」，再回来开房。");
            return;
        }

        var world = new TextBox { Header = "世界名称（可留空）", MaxLength = 32, PlaceholderText = "例如：和朋友开荒" };
        var pass = new PasswordBox { Header = "房间密码（可留空）", MaxLength = 16, PlaceholderText = "留空则谁都能进" };
        var tip = new TextBlock
        {
            Text = $"{_nat.Describe()} · 游戏端口 {lanPort}",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "开启新世界",
            Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { tip, world, pass } },
            PrimaryButtonText = "开房间",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        HostCard.Status = "正在开房…";
        HostButton.IsEnabled = false;

        try
        {
            _host = await P2P.HostAsync(world.Text.Trim(), lanPort, pass.Password, CancellationToken.None);
            _hostPort = lanPort;
            RefreshHostCard();
            Toast($"房间已开：{_host.Code}");
        }
        catch (Exception ex)
        {
            HostCard.Status = ex.Message;
            HostButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StopHostAsync()
    {
        if (_host is null) return;

        var code = _host.Code;
        _host.Stop();
        _host = null;

        HostCard.Status = "把你的局域网世界挂到大厅，朋友点进来就能连。";
        HostButton.Content = "开房间";
        HostButton.IsEnabled = true;

        try
        {
            await LinxAccountService.CallAsync("/api/lobby/unpublish",
                new System.Text.Json.Nodes.JsonObject { ["code"] = code }, CancellationToken.None);
        }
        catch { }

        Toast("房间已关闭。");
    }

    private void RefreshHostCard()
    {
        if (_host is null) return;

        HostCard.Status = $"房间 {_host.Code} · {_host.GuestCount} 人在线 · 游戏端口 {_hostPort}";
        HostButton.Content = "关闭房间";
        HostButton.IsEnabled = true;   // 开房过程中禁用过，这里要恢复，否则按钮一直是灰的
    }

    // ==================== 卡片 2：公开房间 ====================

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel { Spacing = 6, MinWidth = 420 };
        var hint = new TextBlock
        {
            Text = "正在读取大厅…",
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "公开房间",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { hint, new ScrollViewer { Height = 320, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }
            },
            CloseButtonText = "关闭"
        };
        FontService.ApplyToDialog(dialog);

        var rooms = await FetchRoomsAsync(hint);

        foreach (var room in rooms)
        {
            list.Children.Add(BuildRoomRow(room, dialog));
        }

        await dialog.ShowAsync();

        // 关闭后把人数/状态刷一下
        LobbyCard.Status = rooms.Count == 0
            ? "大厅里现在没人开房。"
            : $"大厅里有 {rooms.Count} 个房间。";
    }

    private async Task<System.Collections.Generic.List<JsonElement>> FetchRoomsAsync(TextBlock hint)
    {
        var result = new System.Collections.Generic.List<JsonElement>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var text = await Http.Client.GetStringAsync(RemoteService.BaseUrl + "/api/lobby/rooms", cts.Token);
            using var doc = JsonDocument.Parse(text);

            foreach (var room in doc.RootElement.GetProperty("items").EnumerateArray())
                result.Add(room.Clone());

            hint.Text = result.Count == 0
                ? "还没有人开房。你先开一个，朋友就能看到。"
                : $"共 {result.Count} 个房间";
        }
        catch (Exception ex)
        {
            hint.Text = "大厅读取失败：" + ex.Message.Split('\n')[0];
        }

        return result;
    }

    private UIElement BuildRoomRow(JsonElement room, ContentDialog dialog)
    {
        string Str(string name) => room.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

        var code = Str("code");
        var world = Str("world");
        var owner = Str("owner");
        var players = room.TryGetProperty("players", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
        var maxPlayers = room.TryGetProperty("max_players", out var mp) && mp.TryGetInt32(out var mv) ? mv : 8;
        var hasPassword = room.TryGetProperty("has_password", out var hp) && hp.ValueKind == JsonValueKind.True;
        var natType = Str("nat_type");

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(world) ? "未命名世界" : world,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var subtitle = new TextBlock
        {
            Text = $"{owner} · {players}/{maxPlayers} 人"
                   + (hasPassword ? " · 有密码" : "")
                   + (natType == "symmetric" ? " · 对称型（可能连不上）" : ""),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var join = new Button { Content = "加入", FontSize = 12, Padding = new Thickness(12, 4, 12, 4) };
        join.Click += async (_, _) =>
        {
            dialog.Hide();
            await JoinFlowAsync(code);
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 2, Children = { title, subtitle } });
        Grid.SetColumn(join, 1);
        grid.Children.Add(join);

        return new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["DividerBrush"],
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    // ==================== 卡片 3：加入房间 ====================

    private async void Join_Click(object sender, RoutedEventArgs e) => await JoinFlowAsync("");

    private async Task JoinFlowAsync(string presetCode)
    {
        if (_busy) return;

        if (!LinxAccountService.IsLoggedIn)
        {
            Toast("先在左下角登录 XML 账号。");
            return;
        }

        var codeBox = new TextBox
        {
            Header = "房间号",
            MaxLength = 6,
            CharacterSpacing = 120,
            PlaceholderText = "6 位",
            Text = presetCode ?? ""
        };
        var passBox = new PasswordBox { Header = "密码（没有就留空）", MaxLength = 16 };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "加入房间",
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { codeBox, passBox } },
            PrimaryButtonText = "加入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var code = codeBox.Text.Trim().ToUpperInvariant();
        if (code.Length != 6)
        {
            Toast("房间号是 6 位。");
            return;
        }

        _busy = true;
        JoinCard.Status = "正在打洞…";
        JoinButton.IsEnabled = false;

        try
        {
            _guest?.Stop();
            _guest = await P2P.JoinAsync(code, passBox.Password, P2P.DefaultGuestPort,
                msg => DispatcherQueue.TryEnqueue(() => JoinCard.Status = msg),
                CancellationToken.None);

            var address = $"127.0.0.1:{_guest.LocalPort}";
            JoinCard.Status = $"已连接。游戏里添加服务器：{address}";
            JoinButton.Content = "退出房间";
            JoinButton.IsEnabled = true;
            JoinButton.Click -= Join_Click;
            JoinButton.Click += Leave_Click;

            var versionId = AppState.Current?.Id ?? "";
            if (!string.IsNullOrEmpty(versionId))
                _ = GameSession.StartAsync(versionId, XamlRoot, address);
        }
        catch (Exception ex)
        {
            // 文案别铺太满，卡片显示不开；路径用 %APPDATA%，短且能直接粘到资源管理器
            JoinCard.Status = ex.Message + "\n日志：%APPDATA%\\LinxLauncher\\launcher.log";
            JoinButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        var code = _guest?.Code ?? "";

        _guest?.Stop();
        _guest = null;

        JoinCard.Status = "有房间号就直接输，走 P2P 直连。";
        JoinButton.Content = "输入房间号";
        JoinButton.Click -= Leave_Click;
        JoinButton.Click += Join_Click;

        try
        {
            await LinxAccountService.CallAsync("/api/lobby/leave",
                new System.Text.Json.Nodes.JsonObject { ["code"] = code }, CancellationToken.None);
        }
        catch { }
    }

    private static void Toast(string message) => MainWindow.Instance?.Toast(message);
}

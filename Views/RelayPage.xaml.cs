using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

/// <summary>
/// 联机页：三张卡片 —— 我开房间 / 公开房间 / 加入房间。
/// 走 P2P：服务器只做大厅与地址交换，游戏数据点对点直连。
/// </summary>
public sealed partial class RelayPage : Page
{
    private P2PSession? _host;
    private P2PSession? _guest;
    private NatInfo _nat = new(NatKind.Unknown, null, "");
    private DispatcherQueueTimer? _tick;
    private bool _busy;

    public RelayPage()
    {
        InitializeComponent();
        // 页面常驻：切页再回来时不该把房间/隧道丢掉
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        UpdateAccountText();
        RefreshHostPanel();

        if (_nat.Kind == NatKind.Unknown) _ = DetectNatAsync();
        else ApplyNat(_nat);

        _ = LoadLobbyAsync();

        _tick ??= CreateTick();
        _tick.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _tick?.Stop();
    }

    private DispatcherQueueTimer CreateTick()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => RefreshHostPanel();
        return timer;
    }

    // ==================== 网络检测 ====================

    private async void Redetect_Click(object sender, RoutedEventArgs e)
    {
        _nat = new NatInfo(NatKind.Unknown, null, "");
        await DetectNatAsync();
    }

    private async Task DetectNatAsync()
    {
        NatText.Text = "检测中…";
        PublicAddrText.Text = "—";

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
        NatText.Text = nat.Describe();
        PublicAddrText.Text = nat.Public?.ToString() ?? "—";

        if (nat.Kind == NatKind.Symmetric)
            NatText.Text = "对称型网络 —— 打洞大概率失败";
    }

    private void UpdateAccountText()
    {
        // 账号状态放在卡片的状态行里：没登录就直说，登录了就显示是谁
        HostCard.Status = LinxAccountService.IsLoggedIn
            ? $"{LinxAccountService.DisplayName} · 开好局域网世界后点下面开房"
            : "未登录：先在左下角登录 XML 账号";
    }

    // ==================== 房主 ====================

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!LinxAccountService.IsLoggedIn)
        {
            HostStatusText.Text = "先在左下角登录 XML 账号，联机要用它来认人。";
            return;
        }

        var lanPort = MultiplayerService.DetectLanPort();
        if (lanPort <= 0)
        {
            HostStatusText.Text = "没找到游戏局域网端口。先在游戏里点「对局域网开放」，再回这里开房。";
            return;
        }

        _busy = true;
        HostButton.IsEnabled = false;
        HostStatusText.Text = "正在检测网络并开房…";

        try
        {
            var worldName = WorldNameBox.Text.Trim();
            _host = await P2P.HostAsync(worldName, lanPort, HostPasswordBox.Password, CancellationToken.None);

            HostPanel.Visibility = Visibility.Visible;
            HostCodeText.Text = _host.Code;
            HostStatusText.Text = $"房间已开，游戏端口 {lanPort}。把房间号发给朋友，或者让他在大厅里点进来。";
            HostInfoText.Text = "";
            _ = LoadLobbyAsync();
        }
        catch (Exception ex)
        {
            HostStatusText.Text = ex.Message;
            HostButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void StopHost_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;

        var code = _host.Code;
        _host.Stop();
        _host = null;

        HostPanel.Visibility = Visibility.Collapsed;
        HostButton.IsEnabled = true;
        HostStatusText.Text = "房间已关闭。";

        try
        {
            await LinxAccountService.CallAsync("/api/lobby/unpublish",
                new System.Text.Json.Nodes.JsonObject { ["code"] = code }, CancellationToken.None);
        }
        catch { }

        _ = LoadLobbyAsync();
    }

    private void RefreshHostPanel()
    {
        if (_host is null) return;

        var nat = _host.Nat.Kind == NatKind.Cone ? "锥形网络" : "对称型网络";
        HostInfoText.Text = $"已接入 {_host.GuestCount} 人 · {nat} · {_host.Nat.Public}";
    }

    // ==================== 大厅 ====================

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadLobbyAsync();

    private async Task LoadLobbyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var text = await Http.Client.GetStringAsync(RemoteService.BaseUrl + "/api/lobby/rooms", cts.Token);
            using var doc = JsonDocument.Parse(text);

            var items = doc.RootElement.GetProperty("items");
            LobbyList.Children.Clear();

            var count = 0;
            foreach (var room in items.EnumerateArray())
            {
                LobbyList.Children.Add(BuildRoomRow(room));
                count++;
            }

            LobbyHintText.Text = count == 0
                ? "大厅里还没有房间。开一个，或者让朋友开。"
                : $"共 {count} 个房间 · 点\"加入\"直接进去";
        }
        catch (Exception ex)
        {
            LobbyHintText.Text = "大厅读取失败：" + ex.Message.Split('\n')[0];
        }
    }

    private UIElement BuildRoomRow(JsonElement room)
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        var subtitle = new TextBlock
        {
            Text = $"{owner} · {players}/{maxPlayers} 人"
                   + (hasPassword ? " · 有密码" : "")
                   + (natType == "symmetric" ? " · 对称型" : ""),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var join = new Button { Content = "加入", FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
        join.Click += (_, _) =>
        {
            JoinCodeBox.Text = code;
            JoinPasswordBox.Password = "";
            JoinButton.Focus(FocusState.Programmatic);
            GuestStatusText.Text = $"已填入房间号 {code}，点「加入」开始打洞。";
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
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DividerBrush"],
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    // ==================== 访客 ====================

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var code = JoinCodeBox.Text.Trim().ToUpperInvariant();
        if (code.Length != 6)
        {
            GuestStatusText.Text = "房间号是 6 位。";
            return;
        }

        if (!LinxAccountService.IsLoggedIn)
        {
            GuestStatusText.Text = "先在左下角登录 XML 账号。";
            return;
        }

        _busy = true;
        JoinButton.IsEnabled = false;
        GuestStatusText.Text = "正在加入…";

        try
        {
            _guest?.Stop();
            _guest = await P2P.JoinAsync(code, JoinPasswordBox.Password, P2P.DefaultGuestPort,
                msg => DispatcherQueue.TryEnqueue(() => GuestStatusText.Text = msg),
                CancellationToken.None);

            GuestPanel.Visibility = Visibility.Visible;
            LeaveButton.Visibility = Visibility.Visible;
            GuestAddressText.Text = $"127.0.0.1:{_guest.LocalPort}";
            GuestStatusText.Text = "隧道已打通。在游戏里添加服务器并用上面的地址，或者在启动器里启动游戏直接进。";

            // 1.20+ 支持 --quickPlayMultiplayer，直接帮玩家进服
            var versionId = AppState.Current?.Id ?? "";
            if (!string.IsNullOrEmpty(versionId))
            {
                _ = GameSession.StartAsync(versionId, XamlRoot, $"127.0.0.1:{_guest.LocalPort}");
            }
        }
        catch (Exception ex)
        {
            GuestStatusText.Text = ex.Message;
            JoinButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        var code = _guest?.Code ?? JoinCodeBox.Text.Trim().ToUpperInvariant();

        _guest?.Stop();
        _guest = null;

        GuestPanel.Visibility = Visibility.Collapsed;
        JoinButton.IsEnabled = true;
        GuestStatusText.Text = "已退出房间。";

        try
        {
            await LinxAccountService.CallAsync("/api/lobby/leave",
                new System.Text.Json.Nodes.JsonObject { ["code"] = code }, CancellationToken.None);
        }
        catch { }
    }
}

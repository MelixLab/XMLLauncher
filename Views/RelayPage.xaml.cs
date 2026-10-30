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

    /// <summary>大厅里动态建出来的卡片，鼠标描边和字体都要单独管。</summary>
    private readonly System.Collections.Generic.List<FeatureCard> _lobbyCards = new();

    // ---------- 延迟指示 ----------

    private System.Threading.Timer? _pingTimer;

    /// <summary>
    /// 延迟文案与颜色。
    ///
    /// 之前用 ▁▂▃▄ 这些方块字符画信号条，但 Noto Sans SC 里没有这些字形，
    /// 界面上直接渲染成横杠 —— 所以改成「数字 + 颜色」表达。
    /// </summary>
    private static (string Text, string Color) DescribeLatency(int ping) =>
        ping < 0 ? ("延迟未知", "#6B7280")
        : ping < 60 ? ($"{ping} ms · 流畅", "#00A88E")
        : ping < 120 ? ($"{ping} ms · 一般", "#E8A33D")
        : ($"{ping} ms · 偏卡", "#D9534F");

    private static string Describe(int ping) =>
        ping < 0 ? "测延迟中…"
        : ping < 60 ? $"{ping}ms 流畅"
        : ping < 120 ? $"{ping}ms 一般"
        : $"{ping}ms 偏卡";

    /// <summary>连上之后每 3 秒刷一次延迟；退出房间要停掉。</summary>
    private void StartPingWatch()
    {
        StopPingWatch();

        _pingTimer = new System.Threading.Timer(_ =>
        {
            var guest = _guest;
            if (guest is null) return;

            var ping = guest.Ping;

            var text = $"已连接 · {Describe(ping)}\n多人游戏 → 等几秒 → 点局域网列表里房主的房间";

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_guest is not null) JoinCard.Status = text;
            });
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void StopPingWatch()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    /// <summary>
    /// 联机前的前置检查：
    /// 1. EasyTier 要建虚拟网卡，没有管理员权限起不来 —— 提示一键提权重启；
    /// 2. 第一次联机要下载 32 MB 组件，之后就一直复用。
    /// </summary>
    private async Task<bool> EnsureLinkReadyAsync(Action<string> status)
    {
        if (!EasyTierLink.IsElevated)
        {
            var dialog = new ContentDialog
            {
                Title = "联机需要管理员权限",
                Content = "联机要创建一块虚拟网卡，Windows 只允许管理员做这件事。\n" +
                          "点「以管理员身份重启」后就能正常联机，平时不联机不受影响。",
                PrimaryButtonText = "以管理员身份重启",
                CloseButtonText = "算了",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            FontService.ApplyToDialog(dialog);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (EasyTierLink.RelaunchAsAdmin())
                {
                    Application.Current.Exit();
                }
                else
                {
                    Toast("提权失败，可以右键启动器选「以管理员身份运行」。");
                }
            }

            return false;
        }

        if (!EasyTierLink.IsInstalled)
        {
            status("正在下载联机组件（约 32 MB，只有第一次需要）…");

            try
            {
                await EasyTierLink.EnsureInstalledAsync(
                    new Progress<string>(msg => DispatcherQueue.TryEnqueue(() => status(msg))),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                status("下载联机组件失败：" + ex.Message.Split('\n')[0]);
                Toast("联机组件下载失败，检查一下网络再试。");
                return false;
            }
        }

        return true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 字体要等布局生成完再扫，直接在 OnNavigatedTo 里扫会漏掉整棵子树
        DispatcherQueue.TryEnqueue(() => FontService.Apply(this));

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

    /// <summary>大厅卡片的描边跟随：首页那套是页面统一驱动的，这里同样处理。</summary>
    private void LobbyCards_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in _lobbyCards)
        {
            var p = e.GetCurrentPoint(card);
            card.UpdateReveal(p.Position.X, p.Position.Y);
        }
    }

    private void LobbyCards_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in _lobbyCards) card.ResetReveal();
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
        // EasyTier 兜底会走中转，所以这里不再说「打不通」，只说快慢
        NatText.Text = nat.Kind switch
        {
            NatKind.Symmetric => "对称型网络 · 会走中转，能连但慢一些",
            NatKind.Cone => "锥形网络 · 可以直连",
            _ => nat.Public is null ? "网络检测失败（不影响联机）" : "网络就绪"
        };
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

        var detected = MultiplayerService.DetectLanPort();

        var world = new TextBox { Header = "世界名称（可留空）", MaxLength = 32, PlaceholderText = "例如：和朋友开荒" };
        var pass = new PasswordBox { Header = "房间密码（可留空）", MaxLength = 16, PlaceholderText = "留空则谁都能进" };

        // 端口可以手改：不一定非要映射 MC，自己开的别的服务（面板、私服）也能挂上来
        var portBox = new TextBox
        {
            Header = "要被映射的端口",
            MaxLength = 5,
            PlaceholderText = "例如 25565",
            Text = detected > 0 ? detected.ToString() : "",
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var tip = new TextBlock
        {
            Text = detected > 0
                ? $"{_nat.Describe()} · 已自动检测到局域网端口 {detected}，不是这个可以直接改"
                : $"{_nat.Describe()} · 没检测到游戏端口：先在游戏里「对局域网开放」，或者手动填一个",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "开启新世界",
            Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { tip, world, portBox, pass } },
            PrimaryButtonText = "开房间",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (!int.TryParse(portBox.Text.Trim(), out var lanPort) || lanPort <= 0 || lanPort > 65535)
        {
            Toast("端口要填 1-65535 之间的数字。");
            return;
        }

        _busy = true;
        HostCard.Status = "正在开房…";
        HostButton.IsEnabled = false;

        try
        {
            if (!await EnsureLinkReadyAsync(msg => HostCard.Status = msg)) return;

            HostCard.Status = "正在开房…";

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

        HostCard.Status = $"房间 {_host.Code} · {_host.GuestCount} 人在线\n端口 {_hostPort} · 游戏里记得点「对局域网开放」";
        HostButton.Content = "关闭房间";
        HostButton.IsEnabled = true;   // 开房过程中禁用过，这里要恢复，否则按钮一直是灰的
    }

    // ==================== 卡片 2：公开房间 ====================

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        ShowLobby();
        await LoadLobbyAsync();
    }

    /// <summary>切到大厅视图（整页切换，不是弹窗）。</summary>
    private void ShowLobby()
    {
        MainView.Visibility = Visibility.Collapsed;
        LobbyView.Visibility = Visibility.Visible;

        // 直接点名设字体：Style 里是 StaticResource，遍历扫不到 Collapsed 时就会漏
        LobbyHint.FontFamily = FontService.BodyFont;
        BackButton.FontFamily = FontService.BodyFont;
        LobbyTitle.FontFamily = FontService.DisplayFont;

        DispatcherQueue.TryEnqueue(() => FontService.Apply(LobbyView));
    }

    private void ShowMain()
    {
        LobbyView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowMain();

    private async void RefreshLobby_Click(object sender, RoutedEventArgs e) => await LoadLobbyAsync();

    /// <summary>读一遍大厅，把房间排成卡片墙（和主页三张卡片一个样式）。</summary>
    private async Task LoadLobbyAsync()
    {
        LobbyList.Children.Clear();
        _lobbyCards.Clear();
        LobbyHint.Text = "正在读取大厅…";

        var rooms = await FetchRoomsAsync(LobbyHint);

        if (rooms.Count == 0)
        {
            LobbyHint.Text = "现在还没人开房。回上一页开一个，朋友就能在这里看到。";
            return;
        }

        LobbyHint.Text = $"{rooms.Count} 个房间 · 点「加入」直接进";

        // 一行三张，卡片宽度和主页的 232 略宽一点，能放下房间号
        const int columns = 3;
        const double cardWidth = 300;

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cardWidth) });

        var row = -1;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (i % columns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row++;
            }

            var card = BuildRoomCard(rooms[i]);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, i % columns);
            grid.Children.Add(card);
            _lobbyCards.Add(card);
        }

        LobbyList.Children.Add(grid);
        LobbyCard.Status = $"大厅里有 {rooms.Count} 个房间。";
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

    /// <summary>
    /// 大厅里的一行：世界名 / 房间号 / 谁在里面 / 信号条 / 加入。
    ///
    /// 原来这里挂着一句「对称型 NAT（可能连不上）」，用户看不懂也帮不上忙，
    /// 现在直接把「能不能连、快不快」画成四格信号条。
    /// </summary>
    /// <summary>
    /// 一个房间就是一张 FeatureCard —— 和主页那三张完全同一套观感：
    /// 白底直角、悬停浅灰 + 鼠标跟随描边、底部按钮贴齐。
    /// </summary>
    private Controls.FeatureCard BuildRoomCard(JsonElement room)
    {
        string Str(string name) => room.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

        int Num(string name, int fallback) =>
            room.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

        var code = Str("code");
        var world = string.IsNullOrWhiteSpace(Str("world")) ? "未命名世界" : Str("world");
        var owner = Str("owner");
        var players = Num("players", 1);
        var maxPlayers = Num("max_players", 8);
        var hasPassword = room.TryGetProperty("has_password", out var hp) && hp.ValueKind == JsonValueKind.True;

        var guests = new List<string>();
        if (room.TryGetProperty("guests", out var gs) && gs.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gs.EnumerateArray())
            {
                var name = g.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !guests.Contains(name)) guests.Add(name);
            }
        }

        // 状态只写人和事，延迟单独用彩色文字放在按钮旁边
        var status = $"{owner} · {players}/{maxPlayers} 人"
                     + (hasPassword ? " · 有密码" : "");

        if (guests.Count > 0)
            status += "\n在线：" + string.Join("、", guests.Take(3)) + (guests.Count > 3 ? " 等" : "");

        var card = new Controls.FeatureCard
        {
            Header = world,
            Status = status,
            IconTemplate = (DataTemplate)Application.Current.Resources["IconCardLibrary"],
        };

        var join = new Button
        {
            Content = "加入",
            Style = (Style)Application.Current.Resources["CardActionButtonStyle"],
            FontFamily = FontService.BodyFont,
        };
        join.Click += async (_, _) => await JoinFlowAsync(code);

        // 大厅里不显示延迟：还没连上，量到的只是「到中转节点」的距离，
        // 跟「我到房主那台机器」不是一回事，写出来反而误导。
        // 真实 P2P 延迟等加入之后在联机页显示。
        card.Actions = join;

        // FeatureCard 里面的标题/状态是 XAML 写死的 TextBlock，
        // 动态 new 出来的这份不会被页面级 Apply 扫到，得自己补一次
        card.Loaded += (_, _) => FontService.Apply(card);

        return card;
    }

    /// <summary>
    /// 四格信号条。延迟越低格子越满：
    ///   < 60ms  四格全亮（#00A88E）
    ///   60~120  三格（#E8A33D）
    ///   > 120   两格（#D9534F）
    ///   未知    全灰
    /// </summary>
    private static FrameworkElement BuildSignalBar(int ping)
    {
        int lit = ping < 0 ? 0 : ping < 60 ? 4 : ping < 120 ? 3 : 2;
        var color = ping < 0 ? "#6B7280"
            : ping < 60 ? "#00A88E"
            : ping < 120 ? "#E8A33D"
            : "#D9534F";

        var brush = ToBrush(color);
        var dim = ToBrush("#000000", 0.18);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (var i = 0; i < 4; i++)
        {
            row.Children.Add(new Border
            {
                Width = 3,
                Height = 4 + i * 3,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = i < lit ? brush : dim
            });
        }

        var label = new TextBlock
        {
            Text = ping < 0 ? "未知" : $"{ping}ms",
            FontSize = 11,
            FontFamily = FontService.BodyFont,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
        };

        var wrap = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        wrap.Children.Add(row);
        wrap.Children.Add(label);
        return wrap;
    }

    /// <summary>#RRGGBB → 画笔，alpha 可调。</summary>
    private static SolidColorBrush ToBrush(string hex, double alpha = 1)
    {
        var s = hex.TrimStart('#');
        var r = Convert.ToByte(s.Substring(0, 2), 16);
        var g = Convert.ToByte(s.Substring(2, 2), 16);
        var b = Convert.ToByte(s.Substring(4, 2), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(alpha * 255), r, g, b));
    }

    // ==================== 卡片 3：加入房间 ====================

    private async void Join_Click(object sender, RoutedEventArgs e) => await JoinFlowAsync("");

    /// <summary>
    /// 加入房间。
    ///
    /// 以前连接状态全写在主页那张「加入房间」卡片上，从大厅点加入就完全看不见，
    /// 表现就是「点了没反应」。现在连接过程放进一个进度窗，成功失败都在里面说清楚，
    /// 大厅顶部那行提示也同步显示。
    /// </summary>
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

        // 组件与权限先就位：这里可能再弹一个「需要管理员」的确认框。
        // ContentDialog 同一时间只能显示一个，所以必须放在进度窗之前。
        if (!await EnsureLinkReadyAsync(_ => { })) return;

        // ---- 进度窗 ----
        var statusText = new TextBlock
        {
            Text = "正在连接…",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            FontFamily = FontService.BodyFont,
        };

        var ring = new ProgressRing
        {
            IsActive = true,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var progress = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"加入房间 {code}",
            Content = new StackPanel { Spacing = 14, MinWidth = 400, Children = { ring, statusText } },
            CloseButtonText = "后台运行",
        };
        FontService.ApplyToDialog(progress);

        _ = progress.ShowAsync();

        void PutStatus(string msg)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                statusText.Text = msg;
                JoinCard.Status = msg;
                if (LobbyView.Visibility == Visibility.Visible) LobbyHint.Text = msg;
            });
        }

        _busy = true;
        JoinButton.IsEnabled = false;
        PutStatus("正在连接…");

        try
        {
            _guest?.Stop();
            _guest = await P2P.JoinAsync(code, passBox.Password, P2P.DefaultGuestPort, PutStatus, CancellationToken.None);

            PutStatus($"已连接 · {Describe(_guest.Ping)}\n\n等游戏启动完之后：\n1. 点「多人游戏」\n2. 等几秒，局域网列表里会出现房主的房间\n3. 直接点进去，不用输地址");
            Toast($"已连接房间 {code}");
            StartPingWatch();
            JoinButton.Content = "退出房间";
            JoinButton.IsEnabled = true;
            JoinButton.Click -= Join_Click;
            JoinButton.Click += Leave_Click;

            // 让用户看清「已连接 + 怎么进」再让出对话框
            await Task.Delay(3000);
            try { progress.Hide(); } catch { }
            await Task.Delay(150);

            // ---- 版本核验 ----
            // 房主用的是某个版本，本地没装的话连上也进不去（进去就是版本不符被踢）。
            var required = _guest.RequiredVersion;
            var installed = string.IsNullOrWhiteSpace(required)
                            || AppState.Versions.ScanInstalled()
                                .Any(v => string.Equals(v.Id, required, StringComparison.OrdinalIgnoreCase));

            if (!installed)
            {
                var missing = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "版本对不上",
                    Content = $"房主用的是 {required}，你本地还没有。\n\n"
                              + "先去「版本管理」把它装好，再回来加入，不然进去了也会被踢。",
                    PrimaryButtonText = "知道了",
                    CloseButtonText = "稍后",
                };
                FontService.ApplyToDialog(missing);
                _ = missing.ShowAsync();
                return;
            }

            // ---- 问用户要不要现在启动 ----
            // 以前是连上就直接拉游戏，用户可能只是想先连上看看，
            // 或者根本不想用启动器开游戏（自己用别的客户端进）。
            var versionId = AppState.Current?.Id ?? "";
            var hasLocal = AppState.Versions.ScanInstalled()
                .Any(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase)
                          && v.IsComplete);

            if (string.IsNullOrEmpty(versionId) || !hasLocal)
            {
                var tip = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "已经连上了",
                    Content = string.IsNullOrEmpty(versionId)
                        ? "打开游戏，进「多人游戏」→ 等几秒 → 点局域网列表里房主的房间。"
                        : $"本地还没有 {versionId}，去「版本管理」装好后再启动。\n\n"
                          + "进游戏后用「多人游戏」→ 局域网列表，点房主的房间即可。",
                    PrimaryButtonText = "好",
                };
                FontService.ApplyToDialog(tip);
                _ = tip.ShowAsync();
                return;
            }

            var ask = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "要现在启动游戏吗",
                Content = $"{versionId} 已经就绪。\n\n"
                          + "启动后进「多人游戏」→ 等几秒 → 点局域网列表里房主的房间（不用输地址）。",
                PrimaryButtonText = "启动游戏",
                CloseButtonText = "我自己开",
                DefaultButton = ContentDialogButton.Primary,
            };
            FontService.ApplyToDialog(ask);

            if (await ask.ShowAsync() == ContentDialogResult.Primary)
            {
                _ = GameSession.StartAsync(versionId, XamlRoot, null);
            }
        }
        catch (Exception ex)
        {
            // 文案别铺太满，卡片显示不开；路径用 %APPDATA%，短且能直接粘到资源管理器
            var message = ex.Message + "\n日志：%APPDATA%\\LinxLauncher\\launcher.log";
            PutStatus("连接失败：" + message);
            Toast("加入失败：" + ex.Message);
            JoinButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
            ring.IsActive = false;
            progress.CloseButtonText = "关闭";
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        var code = _guest?.Code ?? "";

        _guest?.Stop();
        _guest = null;
        StopPingWatch();

        JoinCard.Status = "有房间号就直接输，输完就能进。";
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

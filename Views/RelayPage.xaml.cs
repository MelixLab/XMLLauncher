using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using LinxLauncher.Models;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

/// <summary>
/// 联机页。
///
/// 设计取向：用户不用点任何"创建房间"——进页面就自动分配一个联机码，
/// 每 5 分钟自动换一次；游戏没开局域网时码照常显示，只是状态写「游戏未启动」。
/// 朋友拿着码加入时，如果房主还没开局域网，会直接提示"对方游戏未启动"。
/// </summary>
public sealed partial class RelayPage : Page
{
    private const int RefreshSeconds = 5 * 60;   // 码的刷新周期
    private const int PollSeconds = 5;           // 检测局域网端口的周期

    private string _code = "";
    private string _roomHost = "";
    private int _roomPort;
    private bool _ready;
    private bool _busy;

    private MultiplayerSession? _hostSession;
    private int _hostPort;
    private MultiplayerSession? _guestSession;

    private DispatcherQueueTimer? _refreshTimer;
    private DispatcherQueueTimer? _pollTimer;

    public RelayPage()
    {
        InitializeComponent();

        // 页面常驻：切到别的页面再回来不会重建实例，
        // 否则 _code 会被清空 → 重新建房 → 服务端删旧房 → 之前发出去的码直接失效。
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        UiList.Fill(VersionBox, AppState.Installed);
        VersionBox.SelectedItem = AppState.Current;

        if (!LinxAccountService.IsLoggedIn)
        {
            HostCode.Text = "";
            HostReadyText.Text = "先在左下角账号面板登录 XML 账号，登录后这里会自动分配联机码。";
            return;
        }

        await EnsureRoomAsync();
        StartTimers();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 故意不停定时器：页面实例是常驻的，切到别的页面后仍要继续盯着端口，
        // 否则"不在联机页就收不到可联机通知"。
    }

    // ================= 房间与定时器 =================

    private async Task EnsureRoomAsync()
    {
        // 已经有码就直接用（切页面、重进页面都不换码）
        if (!string.IsNullOrEmpty(_code))
        {
            HostCode.Text = _code;
            return;
        }

        try
        {
            var version = (VersionBox.SelectedItem as LocalVersion)?.Id ?? "";
            var room = await MultiplayerService.CreateRoomAsync(version, default);
            _code = room.Code;
            _roomHost = room.Host;
            _roomPort = room.Port;

            HostCode.Text = room.Code;
            HostReadyText.Text = "游戏未启动：进游戏后打开「对局域网开放」即可。";
        }
        catch (Exception ex)
        {
            HostCode.Text = "";
            HostReadyText.Text = "获取联机码失败：" + ex.Message;
        }
    }

    private void StartTimers()
    {
        _pollTimer ??= CreateTimer(PollSeconds, PollLanStateAsync);

        _pollTimer.Start();
        _ = PollLanStateAsync();
    }

    private DispatcherQueueTimer CreateTimer(int seconds, Func<Task> action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = true;
        timer.Tick += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try { await action(); }
            catch (Exception ex) { Log.Warn("联机定时任务失败：" + ex.Message); }
            finally { _busy = false; }
        };
        return timer;
    }

    /// <summary>定期看看本机有没有开着局域网世界，有就自动接上隧道并上报状态。</summary>
    private async Task PollLanStateAsync()
    {
        if (string.IsNullOrEmpty(_code)) return;

        var port = MultiplayerService.DetectLanPort();
        var ready = port > 0;

        if (ready != _ready)
        {
            _ready = ready;
            await MultiplayerService.ReportStatusAsync(ready, default);

            // 只在「变为就绪」的瞬间提示一次；轮询每 5 秒跑一次，
            // 放在下面那段里会变成每 5 秒弹一遍。
            if (ready)
            {
                TrayService.Notify("联机已就绪",
                    $"抓到游戏端口 {port}，联机码 {_code}。现在可以发给朋友了。");
            }
        }

        if (ready)
        {
            HostReadyText.Text = $"游戏已启动，正在转发本地端口 {port}。把上面的码发给朋友即可。";

            if (_hostSession is null || _hostPort != port)
            {
                _hostSession?.Stop();
                _hostPort = port;

                try
                {
                    // 注意：这里绝不能重新 CreateRoomAsync —— 服务端会删掉旧房间并生成新码，
                    // 结果就是"通知里一个码、页面上又一个码"，朋友不知道该用哪个。
                    var room = new RelayRoom
                    {
                        Code = _code,
                        Host = _roomHost,
                        Port = _roomPort
                    };

                    _hostSession = MultiplayerService.StartHost(room, port);
                    StopHostButton.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    HostReadyText.Text = "连中继失败：" + ex.Message;
                }
            }

            // 有人进来就报一声，免得不知道朋友到底连上没
            if (_hostSession is not null)
            {
                var joined = _hostSession.GuestCount;
                HostGuestText.Text = joined > 0 ? $"已经接入 {joined} 位朋友" : "还没有人进来";
            }
        }
        else
        {
            HostReadyText.Text = "游戏未启动：进游戏后打开「对局域网开放」即可，码会一直在这里。";
            HostGuestText.Text = "";

            if (_hostSession is not null)
            {
                _hostSession.Stop();
                _hostSession = null;
                _hostPort = 0;
            }

            StopHostButton.Visibility = Visibility.Collapsed;
        }
    }

    // ================= 访客 =================

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim().ToUpperInvariant();
        if (code.Length != 6)
        {
            GuestStatus.Text = "联机码是 6 位。";
            return;
        }

        JoinButton.IsEnabled = false;
        GuestStatus.Text = "正在加入…";

        try
        {
            var room = await MultiplayerService.JoinRoomAsync(code, default);

            // 房主那边的"已开局域网"状态是每 5 秒上报一次的，刚点完开放可能还没同步过来，
            // 所以这里再等 1.5 秒复查一次，别让用户误以为对方没开。
            if (!room.Ready)
            {
                await Task.Delay(1500);
                room = await MultiplayerService.JoinRoomAsync(code, default);
            }

            if (!room.Ready)
            {
                GuestStatus.Text = "对方的游戏还没启动（没有开局域网），等对方进游戏再试。";
                return;
            }

            int? hostPort = null;
            var local = (VersionBox.SelectedItem as LocalVersion)?.Id ?? "";
            var localMods = MultiplayerService.ModsHash(local);

            if (!string.IsNullOrEmpty(room.ModsHash) && room.ModsHash != localMods)
                GuestStatus.Text = "注意：两端模组不完全一致，可能被踢出。\n";

            _guestSession?.Stop();
            _guestSession = MultiplayerService.StartGuest(room);

            var address = $"127.0.0.1:{_guestSession.LocalPort}";

            LeaveButton.Visibility = Visibility.Visible;
            GuestAddress.Text = address;
            GuestAddress.Visibility = Visibility.Visible;
            GuestSteps.Visibility = Visibility.Visible;

            GuestStatus.Text += $"隧道已开好（本地监听 {address}）。";

            // 1.20 起支持 --quickPlayMultiplayer：直接帮他启动游戏并进服，
            // 低版本没有这个参数，只能提示手动添加服务器。
            var versionId = (VersionBox.SelectedItem as LocalVersion)?.Id ?? "";
            var baseVersion = (VersionBox.SelectedItem as LocalVersion)?.BaseVersion ?? versionId;

            if (!string.IsNullOrEmpty(versionId) && SupportsQuickPlay(baseVersion))
            {
                GuestStatus.Text += $"正在启动 {versionId} 并直接进入房间…";

                _ = GameSession.StartAsync(versionId, XamlRoot, address);
            }
            else
            {
                GuestStatus.Text += "这个版本低于 1.20，不支持自动进服，请按上面的步骤手动添加服务器。";
            }
        }
        catch (Exception ex)
        {
            GuestStatus.Text = ex.Message;
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    /// <summary>1.20 起才有 --quickPlayMultiplayer 这个启动参数。</summary>
    private static bool SupportsQuickPlay(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;

        return major > 1 || (major == 1 && minor >= 20);
    }

    // ================= 通用 =================

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _hostSession?.Stop();
        _hostSession = null;
        _hostPort = 0;

        _guestSession?.Stop();
        _guestSession = null;

        StopHostButton.Visibility = Visibility.Collapsed;
        LeaveButton.Visibility = Visibility.Collapsed;
        GuestAddress.Visibility = Visibility.Collapsed;
        GuestAddress.Text = "";
        GuestSteps.Visibility = Visibility.Collapsed;
        GuestStatus.Text = "";
        HostReadyText.Text = _ready ? "已结束联机。" : "游戏未启动：进游戏后打开「对局域网开放」即可。";
    }
}

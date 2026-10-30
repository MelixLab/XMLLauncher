using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 用户体验改进计划。
///
/// 上报的是「启动器开了 / 开了哪个版本的游戏 / 崩了」这类事件，加上设备号、版本号和系统版本，
/// 不含存档内容、路径、账号密码。后台据此看有多少人在用、卡在哪些环节。
/// 默认开启，用户可以随时在设置里关掉 —— 关掉之后一个字节都不会发。
///
/// 服务端对这个接口开了多层限制（单次大小 / 设备频率 / 全站配额），
/// 客户端这边也自己节流：一次运行最多发 <see cref="MaxReportsPerRun"/> 条。
/// </summary>
public static class Telemetry
{
    private const int MaxReportsPerRun = 20;
    private const int TimeoutSeconds = 6;

    private static Timer? _heartbeat;
    private static int _sent;
    private static int _started;

    /// <summary>设备号：第一次用时生成并存盘，之后一直复用。</summary>
    public static string DeviceId
    {
        get
        {
            var id = AppState.Settings.DeviceId;
            if (!string.IsNullOrWhiteSpace(id)) return id;

            id = Guid.NewGuid().ToString("N");
            AppState.Settings.DeviceId = id;
            SettingsService.Save();
            return id;
        }
    }

    public static bool Enabled => !RemoteService.IsLocalDevelopment && AppState.Settings.TelemetryEnabled;

    /// <summary>启动时调用：发一条 start，然后开始定时心跳。</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (!Enabled) return;

        Report("start", $"启动 · {DescribeEnvironment()}");

        // 心跳：先等 20 秒（别和启动抢网络），之后每 3 分钟一次
        _heartbeat = new Timer(_ => Beat(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(3));
    }

    /// <summary>设置里关掉开关时调用，停掉心跳。</summary>
    public static void Stop()
    {
        try { _heartbeat?.Dispose(); } catch { }
        _heartbeat = null;
    }

    /// <summary>开游戏 / 退游戏 / 报错时调用。</summary>
    public static void Report(string kind, string payload)
    {
        if (!Enabled) return;
        if (Interlocked.Increment(ref _sent) > MaxReportsPerRun) return;

        var device = DeviceId;
        var body = new JsonObject
        {
            ["device_id"] = device,
            ["kind"] = kind,
            ["client_version"] = RemoteService.ClientVersion,
            ["os"] = RuntimeInformation.OSDescription,
            ["username"] = AppState.Settings.ServerUser ?? "",
            ["payload"] = Truncate(payload, 8000)
        }.ToJsonString();

        _ = PostAsync("/api/telemetry/report", body);
    }

    private static void Beat()
    {
        if (!Enabled) return;

        var body = new JsonObject
        {
            ["device_id"] = DeviceId,
            ["client_version"] = RemoteService.ClientVersion,
            ["os"] = RuntimeInformation.OSDescription,
            ["username"] = AppState.Settings.ServerUser ?? ""
        }.ToJsonString();

        _ = PostAsync("/api/telemetry/heartbeat", body);
    }

    private static async Task PostAsync(string path, string json)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.Client.PostAsync(RemoteService.BaseUrl + path, content, cts.Token);
            // 失败就算了：这是个后台统计，不该影响用户
        }
        catch (Exception ex)
        {
            Log.Info("遥测上报失败（不影响使用）：" + ex.Message);
        }
    }

    private static string DescribeEnvironment()
    {
        try
        {
            return $"v{RemoteService.ClientVersion} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
        }
        catch
        {
            return "v" + RemoteService.ClientVersion;
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max];
    }
}

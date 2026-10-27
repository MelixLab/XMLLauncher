using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

/// <summary>
/// 游戏异常退出窗口。是**独立窗口**：游戏跑起来之后启动器已经最小化到托盘了，
/// 挂在主窗口上的 ContentDialog 根本看不见。
/// 同一时间只留一个实例，新的报错会替换掉旧的。
/// </summary>
public sealed partial class LaunchErrorWindow : Window
{
    private static LaunchErrorWindow? _open;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly LaunchErrorInfo _info;
    private bool _sized;

    public LaunchErrorWindow(LaunchErrorInfo info)
    {
        InitializeComponent();
        _info = info;

        Title = "游戏异常退出";
        SummaryText.Text = string.IsNullOrWhiteSpace(info.Summary)
            ? "游戏进程没有正常结束。"
            : info.Summary;

        // 崩溃原因：能从 crash-report / 日志里解析出来就展示，解析不出来就整块不出现
        var crash = info.CrashDetail?.Trim();
        var advice = info.Advice?.Trim();

        if (!string.IsNullOrEmpty(crash) || !string.IsNullOrEmpty(advice))
        {
            CrashBlock.Visibility = Visibility.Visible;
            CrashText.Text = string.IsNullOrEmpty(crash) ? "没能从日志里解析出具体原因。" : crash!;

            if (!string.IsNullOrEmpty(advice))
            {
                AdviceText.Text = "建议：" + advice;
                AdviceText.Visibility = Visibility.Visible;
            }
        }

        VersionValue.Text = string.IsNullOrWhiteSpace(info.VersionId) ? "—" : info.VersionId!;
        ExitValue.Text = info.ExitCode is { } code ? $"{code}  ({DescribeExitCode(code)})" : "—";
        LogPathValue.Text = string.IsNullOrWhiteSpace(info.LogFile) ? "—" : info.LogFile!;

        LogText.Text = info.LogTail?.Trim() ?? "";
        OpenLogButton.IsEnabled = !string.IsNullOrWhiteSpace(info.LogFile);

        // 尺寸要等窗口激活后再设，否则会被 WinUI 的默认 1024×768 盖掉。
        Activated += (_, _) =>
        {
            if (_sized) return;
            _sized = true;
            ApplyWindowSize();

            // 这是独立窗口，主窗口的字体遍历扫不到它，自己套一遍
            FontService.Apply(Content as DependencyObject);

            var fix = DispatcherQueue.CreateTimer();
            fix.Interval = TimeSpan.FromMilliseconds(220);
            fix.IsRepeating = false;
            fix.Tick += (_, _) => { fix.Stop(); ApplyWindowSize(); };
            fix.Start();
        };

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    private void ApplyWindowSize()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 和主窗口一样：优先用 XamlRoot 的缩放比，GetDpiForWindow 在窗口刚建好时可能是 96
            double scale = 0;
            if (Content?.XamlRoot is { } root && root.RasterizationScale > 0)
                scale = root.RasterizationScale;
            if (scale <= 0.1)
            {
                var dpi = GetDpiForWindow(hwnd);
                scale = dpi > 0 ? dpi / 96.0 : 1.0;
            }

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

            int w = Math.Min((int)Math.Round(660 * scale), area.Width - 60);
            int h = Math.Min((int)Math.Round(640 * scale), area.Height - 60);

            AppWindow.ResizeClient(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(
                area.X + (area.Width - w) / 2,
                area.Y + (area.Height - h) / 2));
        }
        catch (Exception ex)
        {
            Log.Error("LaunchErrorWindow", ex);
        }
    }

    /// <summary>弹出报错窗口（UI 线程调用）。</summary>
    public static void ShowError(LaunchErrorInfo info)
    {
        try
        {
            _open?.Close();

            var win = new LaunchErrorWindow(info);
            _open = win;
            win.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("LaunchErrorWindow", ex);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = _info.LogFile;
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.Combine(Paths.AppData, "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("LaunchErrorWindow", ex);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("游戏异常退出");
            sb.AppendLine(_info.Summary);
            if (!string.IsNullOrWhiteSpace(_info.CrashDetail)) sb.AppendLine("崩溃信息：" + _info.CrashDetail);
            if (!string.IsNullOrWhiteSpace(_info.Advice)) sb.AppendLine("建议：" + _info.Advice);
            sb.AppendLine("版本：" + (string.IsNullOrWhiteSpace(_info.VersionId) ? "—" : _info.VersionId));
            sb.AppendLine("退出码：" + (_info.ExitCode is { } c ? $"{c} ({DescribeExitCode(c)})" : "—"));
            sb.AppendLine("日志：" + (_info.LogFile ?? "—"));
            if (!string.IsNullOrWhiteSpace(_info.LogTail))
            {
                sb.AppendLine();
                sb.AppendLine(_info.LogTail);
            }
            sb.AppendLine();
            sb.AppendLine("（提示：请不要截图界面，把上面的日志文件发给信任的人。）");

            var package = new DataPackage();
            package.SetText(sb.ToString());
            Clipboard.SetContent(package);

            CopyButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            Log.Error("LaunchErrorWindow", ex);
            CopyButton.Content = "复制失败";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>退出码翻成人话（只认常见的几个，认不出来就报十六进制）。</summary>
    private static string DescribeExitCode(int code) => code switch
    {
        -1073741819 => "0xC0000005 访问冲突",
        -1073740791 => "0xC0000409 栈溢出 / 安全异常",
        -1073740940 => "0xC0000374 堆损坏",
        -1073741571 => "0xC00000FD 栈溢出",
        -1073741515 => "0xC0000135 缺少 DLL",
        -1 => "进程被强制结束",
        1 => "一般错误",
        _ => $"0x{code:X}"
    };
}

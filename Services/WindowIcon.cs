using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace LinxLauncher.Services;

/// <summary>
/// 窗口图标。
///
/// 为什么不用 AppWindow.SetIcon：在未打包的 WinUI 3 应用里它只改得动标题栏那一条，
/// 任务栏上那个「窗口按钮」还是系统默认的 Win32 白纸图标（exe 是好的、进程也是好的，
/// 只有窗口不对劲，就是这里漏的一层）。
/// 任务栏读的是窗口的 WM_SETICON 大图标，所以这里直接跟 user32 打交道。
///
/// 另外先给进程设一个固定的 AppUserModelID —— 不设的话任务栏分组身份跟着 exe 路径跑，
/// 换个目录放启动器就可能变成另一个「应用」，图标也会一起飘。
/// </summary>
public static class WindowIcon
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// 必须在创建任何窗口之前调用（放在 App 构造函数第一行）。
    /// 除了告诉系统「我是谁」，还要在注册表里给这个 AppUserModelID 登记
    /// 显示名和图标 —— 否则 Windows 11 把托盘气泡升级成系统通知时，
    /// 标题就是一串 XiguaStudio.XMLLauncher，图标是默认白纸。
    /// </summary>
    public static void SetAppUserModelId(string id, string displayName)
    {
        try { SetCurrentProcessExplicitAppUserModelID(id); }
        catch (Exception ex) { Log.Warn("设置 AppUserModelID 失败：" + ex.Message); }

        try
        {
            var exe = Environment.ProcessPath ?? "";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + id);

            key.SetValue("DisplayName", displayName);
            if (!string.IsNullOrEmpty(exe)) key.SetValue("IconUri", exe);
        }
        catch (Exception ex)
        {
            Log.Warn("登记通知显示名失败：" + ex.Message);
        }
    }

    /// <summary>给已经创建好的窗口装上大/小图标（任务栏 + Alt-Tab 都看这个）。</summary>
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            if (hwnd == IntPtr.Zero) return;

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(path))
            {
                Log.Warn("窗口图标文件不存在：" + path);
                return;
            }

            var bigW = GetSystemMetrics(SM_CXICON);
            var bigH = GetSystemMetrics(SM_CYICON);
            var smallW = GetSystemMetrics(SM_CXSMICON);
            var smallH = GetSystemMetrics(SM_CYSMICON);

            var big = LoadImage(IntPtr.Zero, path, IMAGE_ICON, bigW, bigH, LR_LOADFROMFILE);
            var small = LoadImage(IntPtr.Zero, path, IMAGE_ICON, smallW, smallH, LR_LOADFROMFILE);

            // ico 里不一定有 16x16 那一档，取不到就用大图标顶上，
            // 否则标题栏 / Alt-Tab 的小图标是空的
            if (small == IntPtr.Zero) small = big;

            if (big != IntPtr.Zero)
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, big);
            if (small != IntPtr.Zero)
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, small);

            if (big == IntPtr.Zero && small == IntPtr.Zero)
                Log.Warn("LoadImage 没加载出图标：" + path);
        }
        catch (Exception ex)
        {
            Log.Warn("设置窗口图标失败：" + ex.Message);
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LinxLauncher.Services;

/// <summary>
/// 托盘图标（Win32 Shell_NotifyIcon，零第三方依赖）。
/// 启动游戏后启动器隐藏到托盘，点图标恢复主窗口，右键菜单可显示 / 退出。
/// 消息窗口用 HWND_MESSAGE 建，消息走 WinUI 的主线程消息泵，不需要自己开线程。
/// </summary>
public static class TrayService
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 17;
    private const int WM_NULL = 0x0000;

    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static IntPtr _hwnd;
    private static IntPtr _icon;
    private static bool _added;
    private static uint _taskbarCreated; // explorer 重启后要重新把图标加回去
    private static WndProcDelegate? _wndProc; // 必须持有委托，否则被 GC 掉会直接崩

    // ================= 对外接口 =================

    /// <summary>显示托盘图标（重复调用安全）。</summary>
    public static void Show(string tip = "XML Minecraft Launcher")
    {
        try
        {
            if (!EnsureWindow()) return;

            var data = NewData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = (uint)WM_TRAY;
            data.hIcon = LoadAppIcon();
            data.szTip = tip.Length > 100 ? tip[..100] : tip;

            if (!_added)
            {
                if (Shell_NotifyIcon(NIM_ADD, ref data))
                {
                    _added = true;
                    return;
                }
            }

            if (Shell_NotifyIcon(NIM_MODIFY, ref data)) _added = true;
            else Log.Error("Tray", new InvalidOperationException("托盘图标添加失败"));
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    /// <summary>
    /// 弹一条托盘气泡通知（Windows 10/11 会显示成右下角通知）。
    /// 用来告诉用户"游戏起来了 / 联机端口抓到了"，否则窗口一藏，用户以为启动器挂了。
    /// </summary>
    public static void Notify(string title, string message)
    {
        try
        {
            if (!_added) Show();

            var data = NewData();
            data.uFlags = NIF_INFO | NIF_ICON;
            data.hIcon = LoadAppIcon();
            data.szInfoTitle = title.Length > 60 ? title[..60] : title;
            data.szInfo = message.Length > 250 ? message[..250] : message;
            data.dwInfoFlags = NIIF_INFO;

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    /// <summary>移除托盘图标。</summary>
    public static void Hide()
    {
        try
        {
            if (!_added || _hwnd == IntPtr.Zero) return;
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    /// <summary>把启动器藏起来，只留托盘图标。</summary>
    public static void HideMainWindow()
    {
        try
        {
            var hwnd = MainHwnd();
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_HIDE);
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    /// <summary>从托盘把启动器叫回来。</summary>
    public static void ShowMainWindow()
    {
        try
        {
            Hide();

            var hwnd = MainHwnd();
            if (hwnd == IntPtr.Zero) return;

            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    /// <summary>退出整个程序（托盘菜单用）。</summary>
    public static void ExitApp()
    {
        try
        {
            Hide();
            MainWindow.Instance?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }

        Application.Current?.Exit();
    }

    /// <summary>程序结束时清掉图标和窗口，别在托盘里留尸体。</summary>
    public static void Dispose()
    {
        Hide();

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }
        catch { }
    }

    // ================= 内部实现 =================

    private static IntPtr MainHwnd()
    {
        var win = MainWindow.Instance;
        if (win is null) return IntPtr.Zero;
        return WindowNative.GetWindowHandle(win);
    }

    private static bool EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return true;

        var hInstance = GetModuleHandle(null);
        if (_taskbarCreated == 0) _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _wndProc = WndProc;

        const string className = "LinxLauncherTrayWindow";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() is not 1410) // 1410 = 类已注册
            Log.Error("Tray", new InvalidOperationException("托盘消息窗口注册失败"));

        _hwnd = CreateWindowEx(0, className, "LinxTray", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("Tray", new InvalidOperationException("托盘消息窗口创建失败"));
            return false;
        }

        return true;
    }

    private static NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1
    };

    private static IntPtr LoadAppIcon()
    {
        if (_icon != IntPtr.Zero) return _icon;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                {
                    _icon = h;
                    return _icon;
                }
            }
        }
        catch { }

        _icon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        return _icon;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            // explorer 重启：图标没了，重新挂上
            _added = false;
            Show();
            return IntPtr.Zero;
        }

        if (msg == WM_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowMainWindow();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowMenu()
    {
        try
        {
            GetCursorPos(out var pt);

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            AppendMenu(menu, MF_STRING, new IntPtr(1), "显示启动器");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, new IntPtr(2), "退出");

            // 这两步是托盘菜单的标准姿势：先抢前台，再补一个 WM_NULL，
            // 否则点菜单外面菜单不会消失。
            SetForegroundWindow(_hwnd);
            var cmd = (int)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);

            if (cmd == 1) ShowMainWindow();
            else if (cmd == 2) ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
    }

    // ================= Win32 =================

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

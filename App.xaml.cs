using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using LinxLauncher.Services;

namespace LinxLauncher;

public partial class App : Application
{
    public static Window? MainWin { get; private set; }
    public static DispatcherQueue? UI { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Fatal("App", e.Exception);

            // 以前只写日志：用户只看到「报错了」，界面上一点信息都没有。
            // Toast 内部自己吞异常，不会在异常处理里再抛一次。
            try
            {
                var message = e.Exception?.Message ?? "未知错误";
                MainWindow.Instance?.Toast("启动器内部出错：" + message.Split((char)10)[0] + "（详情见日志）", true);
            }
            catch { }

            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UI = DispatcherQueue.GetForCurrentThread();

        try
        {
            AppState.Init();
            // 全局控件字体：Toast / 对话框 / 浮窗这些「事后创建」的控件也一起跟上
            FontService.ApplyGlobal(AppState.Settings.UseNotoSans);
            Log.Info("XML Launcher 启动（WinUI 3）");
        }
        catch (Exception ex)
        {
            Log.Fatal("Init", ex);
        }

        MainWin = new MainWindow();
        MainWin.Activate();
    }
}

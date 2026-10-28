using System;
using System.Threading.Tasks;
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
        // 必须在任何窗口创建之前：任务栏靠它认出「这是哪个应用」
        WindowIcon.SetAppUserModelId("XiguaStudio.XMLLauncher", "XML Minecraft Launcher");

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

        // 用户体验改进计划：默认开启，设置里能关；关掉后这里什么都不做
        try { Telemetry.Start(); }
        catch (Exception ex) { Log.Warn("遥测启动失败：" + ex.Message); }

        // 调试用：设置 XML_TEST_TOAST=1 启动，3 秒后弹一条托盘通知，
        // 方便检查通知的标题和图标记没记对（正常使用不会触发）。
        if (Environment.GetEnvironmentVariable("XML_TEST_TOAST") == "1")
        {
            _ = Task.Delay(3000).ContinueWith(_ => TrayService.Notify(
                "这是一条测试通知",
                "如果标题显示 XML Minecraft Launcher、左边是我们的图标，就说明注册好了。"),
                TaskScheduler.Default);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private sealed class JavaOption
    {
        public JavaInfo? Info { get; init; }
        public override string ToString() => Info is null
            ? "自动选择（按版本需求）"
            : $"Java {Info.Major} · {Info.Arch} · {Info.Home}";
    }

    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    /// <summary>设置页里的「重新开始向导」：不关窗口，直接盖一层向导上去。</summary>
    private void RestartWizard_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance?.ShowOnboarding(fromSettings: true);
    }

    private void Telemetry_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;   // 初始化时设 IsOn 也会触发，别把设置写乱

        AppState.Settings.TelemetryEnabled = TelemetrySwitch.IsOn;
        SettingsService.Save();

        if (TelemetrySwitch.IsOn) Telemetry.Start();
        else Telemetry.Stop();
    }

    private void Font_Toggled(object sender, RoutedEventArgs e)
    {
        AppState.Settings.UseNotoSans = FontSwitch.IsOn;
        SettingsService.Save();

        // 先换全局资源（管以后新建的控件），再把已有的刷一遍
        FontService.ApplyGlobal(FontSwitch.IsOn);
        FontService.RefreshAll();
    }

    private void LoadSettings()
    {
        _loading = true;
        var s = AppState.Settings;

        GameDirBox.Text = Paths.GameDir;
        PlayerNameBox.Text = s.PlayerName;

        MemorySlider.Value = Math.Clamp(s.MemoryMb, 1024, 16384);
        MemoryValue.Text = $"{MemorySlider.Value:0} MB";

        ThreadSlider.Value = Math.Clamp(s.DownloadThreads, 2, 24);
        ThreadValue.Text = $"{ThreadSlider.Value:0}";


        MirrorSwitch.IsOn = s.UseMirror;
        FontSwitch.IsOn = s.UseNotoSans;
        TelemetrySwitch.IsOn = s.TelemetryEnabled;

        WidthBox.Value = s.GameWidth;
        HeightBox.Value = s.GameHeight;
        FullscreenSwitch.IsOn = s.Fullscreen;

        JvmArgsBox.Text = s.ExtraJvmArgs ?? "";
        GameArgsBox.Text = s.ExtraGameArgs ?? "";

        RefreshJavaList();
        RefreshImportList();

        AboutText.Text = $"XML Launcher 1.0.0 · WinUI 3 · 配置目录 {Paths.AppData}";

        _loading = false;

        // 「检测到 X 个 Java 运行时」这类文字是数据填完才出现的，
        // 刷新字体要放在最后，否则它们还停在默认字体上。
        FontService.RefreshAll();
    }

    // ================= 从其它启动器导入 =================

    private void RefreshImportList()
    {
        ImportHost.Children.Clear();

        var candidates = ImportService.DetectCandidates();
        if (candidates.Count == 0)
        {
            ImportHost.Children.Add(new TextBlock
            {
                Text = "没找到常见的 .minecraft 目录。用下面的「选择其它文件夹」手动指定。",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"]
            });
            return;
        }

        foreach (var dir in candidates)
        {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(460) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = dir,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
            };

            var button = new Button { Content = "导入" };
            string captured = dir;
            button.Click += async (_, _) => await RunImportAsync(captured);
            Grid.SetColumn(button, 1);

            row.Children.Add(text);
            row.Children.Add(button);
            ImportHost.Children.Add(row);
        }
    }

    private async void RefreshImport_Click(object sender, RoutedEventArgs e) => RefreshImportList();

    private async void ChooseImportDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!));
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            if (!ImportService.LooksLikeGameDir(folder.Path))
            {
                MainWindow.Instance?.Toast("这个文件夹看起来不是 Minecraft 游戏目录（没有 versions / saves / options.txt）", true);
                return;
            }

            await RunImportAsync(folder.Path);
        }
        catch (Exception ex)
        {
            Log.Error("Import", ex);
            MainWindow.Instance?.Toast("选择目录失败：" + ex.Message, true);
        }
    }

    private async Task RunImportAsync(string sourceDir)
    {
        try
        {
            MainWindow.Instance?.Toast("开始导入，文件多的话要等一会儿…");
            var progress = new Progress<string>(m => Log.Info("导入：" + m));
            var result = await Task.Run(() => ImportService.ImportAsync(sourceDir, progress, default));

            RefreshImportList();
            MainWindow.Instance?.Toast(result.Summary);
        }
        catch (Exception ex)
        {
            Log.Error("Import", ex);
            MainWindow.Instance?.Toast("导入失败：" + ex.Message.Split('\n')[0], true);
        }
    }

    private void RefreshJavaList()
    {
        var javas = JavaService.Detect(true);
        var items = new List<JavaOption> { new() };
        items.AddRange(javas.Select(j => new JavaOption { Info = j }));

        var keep = AppState.Settings.JavaHome;
        UiList.Fill(JavaBox, items);
        JavaBox.SelectedItem = string.IsNullOrWhiteSpace(keep)
            ? items[0]
            : items.FirstOrDefault(o => o.Info is not null && o.Info.Home.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    private void Memory_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MemoryValue is null) return;
        MemoryValue.Text = $"{e.NewValue:0} MB";
    }

    private void Thread_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ThreadValue is null) return;
        ThreadValue.Text = $"{e.NewValue:0}";
    }

    private void Mirror_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Endpoints.UseMirror = MirrorSwitch.IsOn;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            Paths.GameDir = folder.Path;
            AppState.Settings.GameDir = folder.Path;
            SettingsService.Save();
            AppState.RefreshInstalled();
            GameDirBox.Text = folder.Path;
            MainWindow.Instance?.Toast("游戏目录已切换");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.Toast("选择目录失败：" + ex.Message, true);
        }
    }

    private void RescanJava_Click(object sender, RoutedEventArgs e)
    {
        RefreshJavaList();
        var count = JavaService.Detect().Count;
        MainWindow.Instance?.Toast(count > 0 ? $"检测到 {count} 个 Java 运行时" : "没有检测到 Java");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;

        s.PlayerName = string.IsNullOrWhiteSpace(PlayerNameBox.Text) ? "Steve" : PlayerNameBox.Text.Trim();
        s.MemoryMb = (int)MemorySlider.Value;
        s.DownloadThreads = (int)ThreadSlider.Value;
        s.UseMirror = MirrorSwitch.IsOn;
        Endpoints.UseMirror = s.UseMirror;
        s.Fullscreen = FullscreenSwitch.IsOn;
        s.GameWidth = (int)(double.IsNaN(WidthBox.Value) ? 925 : WidthBox.Value);
        s.GameHeight = (int)(double.IsNaN(HeightBox.Value) ? 520 : HeightBox.Value);
        s.ExtraJvmArgs = string.IsNullOrWhiteSpace(JvmArgsBox.Text) ? null : JvmArgsBox.Text.Trim();
        s.ExtraGameArgs = string.IsNullOrWhiteSpace(GameArgsBox.Text) ? null : GameArgsBox.Text.Trim();
        s.JavaHome = (JavaBox.SelectedItem as JavaOption)?.Info?.Home;

        Paths.GameDir = s.GameDir;
        SettingsService.Save();
        AppState.RefreshInstalled();
        MainWindow.Instance?.RefreshAccountUi();

        SavedText.Visibility = Visibility.Visible;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.8);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SavedText.Visibility = Visibility.Collapsed;
        };
        timer.Start();

        MainWindow.Instance?.Toast("设置已保存");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Paths.AppData, "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.Toast("打开失败：" + ex.Message, true);
        }
    }
}

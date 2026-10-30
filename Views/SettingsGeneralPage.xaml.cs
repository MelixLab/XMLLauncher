using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class SettingsGeneralPage : Page
{
    private sealed class JavaOption
    {
        public JavaInfo? Info { get; init; }
        public override string ToString() => Info is null
            ? "自动选择（按版本需求）"
            : Info.Major == 0 ? $"已配置（未检测到） · {Info.Home}"
            : $"Java {Info.Major} · {Info.Arch} · {Info.Home}";
    }

    private bool _loading = true;
    private string _savedPlayerName = "";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private bool _savePending;

    public SettingsGeneralPage()
    {
        InitializeComponent();
        var s = AppState.Settings;
        GameDirBox.Text = Paths.GameDir;
        PlayerNameBox.Text = s.PlayerName;
        _savedPlayerName = PlayerName(s.PlayerName);
        MemorySlider.Value = Math.Clamp(s.MemoryMb, 1024, 16384);
        ThreadSlider.Value = Math.Clamp(s.DownloadThreads, 2, 24);
        MemoryValue.Text = $"{MemorySlider.Value:0} MB";
        ThreadValue.Text = $"{ThreadSlider.Value:0}";
        MirrorSwitch.IsOn = s.UseMirror;
        WidthBox.Value = s.GameWidth;
        HeightBox.Value = s.GameHeight;
        FullscreenSwitch.IsOn = s.Fullscreen;
        RefreshJavaList();
        RefreshImportList();
        _loading = false;
        Unloaded += (_, _) => FlushPendingSave();
    }

    private void Columns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 920;
        Columns.ColumnSpacing = wide ? 16 : 0;
        Columns.RowSpacing = wide ? 0 : 16;
        Columns.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(RightColumn, wide ? 1 : 0);
        Grid.SetRow(RightColumn, wide ? 0 : 1);
    }

    private void RefreshImportList()
    {
        ImportHost.Children.Clear();

        var candidates = ImportService.DetectCandidates();
        if (candidates.Count == 0)
        {
            ImportHost.Children.Add(new TextBlock
            {
                Text = "没找到常见的 .minecraft 目录。用下面的「选择其它文件夹」手动指定。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"]
            });
            return;
        }

        foreach (var dir in candidates)
        {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = dir,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeService.Brush("SecondaryTextBrush")
            };

            ToolTipService.SetToolTip(text, dir);
            var button = new Button { Content = "导入" };
            string captured = dir;
            button.Click += async (_, _) => await RunImportAsync(captured);
            Grid.SetColumn(button, 1);

            row.Children.Add(text);
            row.Children.Add(button);
            ImportHost.Children.Add(row);
        }
    }

    private void RefreshImport_Click(object sender, RoutedEventArgs e) => RefreshImportList();

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
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var javas = JavaService.Detect(true);
            var items = new List<JavaOption> { new() };
            items.AddRange(javas.Select(j => new JavaOption { Info = j }));

            var keep = JavaBox.SelectedItem is JavaOption selected ? selected.Info?.Home : AppState.Settings.JavaHome;
            if (!string.IsNullOrWhiteSpace(keep) && !items.Any(o => o.Info?.Home.Equals(keep, StringComparison.OrdinalIgnoreCase) == true))
                items.Add(new JavaOption { Info = new JavaInfo { Home = keep } });
            UiList.Fill(JavaBox, items);
            JavaBox.SelectedItem = string.IsNullOrWhiteSpace(keep)
                ? items[0]
                : items.FirstOrDefault(o => o.Info is not null && o.Info.Home.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }
        finally { _loading = wasLoading; }
    }

    private void Memory_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MemoryValue is null) return;
        MemoryValue.Text = $"{e.NewValue:0} MB";
        if (_loading) return;
        AppState.Settings.MemoryMb = (int)e.NewValue;
        ScheduleSave();
    }

    private void Thread_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ThreadValue is null) return;
        ThreadValue.Text = $"{e.NewValue:0}";
        if (_loading) return;
        AppState.Settings.DownloadThreads = (int)e.NewValue;
        ScheduleSave();
    }

    private void Mirror_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Endpoints.UseMirror = MirrorSwitch.IsOn;
        AppState.Settings.UseMirror = MirrorSwitch.IsOn;
        ScheduleSave();
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

    private static string PlayerName(string text) => string.IsNullOrWhiteSpace(text) ? "Steve" : text.Trim();

    private void PlayerName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        PlayerNameSaveButton.IsEnabled = PlayerName(PlayerNameBox.Text) != _savedPlayerName;
    }

    private void SavePlayerName_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        string previous = s.PlayerName;
        s.PlayerName = PlayerName(PlayerNameBox.Text);
        if (!SettingsService.TrySave())
        {
            s.PlayerName = previous;
            MainWindow.Instance?.Toast("保存失败，请检查配置目录权限后重试。", true);
            return;
        }
        _savedPlayerName = s.PlayerName;
        AccountService.RenameCurrentOffline(s.PlayerName);
        MainWindow.Instance?.RefreshAccountUi();
        PlayerNameSaveButton.IsEnabled = false;
        MainWindow.Instance?.Toast("已保存");
    }

    private void Java_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.JavaHome = (JavaBox.SelectedItem as JavaOption)?.Info?.Home;
        ScheduleSave();
    }

    private void Fullscreen_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.Fullscreen = FullscreenSwitch.IsOn;
        ScheduleSave();
    }

    private void WindowSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading || double.IsNaN(e.NewValue)) return;
        if (sender == WidthBox) AppState.Settings.GameWidth = (int)e.NewValue;
        else AppState.Settings.GameHeight = (int)e.NewValue;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _savePending = true;
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(250);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => FlushPendingSave();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        if (!_savePending) return;
        _saveTimer?.Stop();
        _savePending = !SettingsService.TrySave();
        if (_savePending)
            MainWindow.Instance?.Toast("保存失败，请检查配置目录权限后重试。", true);
    }
}

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


        LoadThemeSettings(s);

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

        RefreshXmlAccount();

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
        AccountService.RenameCurrentOffline(s.PlayerName);
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

    // ================= 主题 =================

    private static readonly (int Id, string Name, string Desc)[] Materials =
    {
        (0, "纯色", "整窗不透明"),
        (1, "云母", "跟随壁纸取色"),
        (2, "云母 Alt", "壁纸感更浓"),
        (3, "亚克力", "磨砂玻璃"),
    };

    private static readonly (string Hex, string Name)[] Accents =
    {
        ("#00A88E", "青"),
        ("#2F7BF6", "蓝"),
        ("#12A150", "绿"),
        ("#F0932B", "橙"),
        ("#E0453C", "红"),
        ("#5B6470", "石墨"),
    };

    private readonly Dictionary<int, Button> _materialButtons = new();
    private readonly List<Border> _accentRings = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

    /// <summary>把设置里的主题字段灌进控件（不触发任何应用动作）。</summary>
    private void LoadThemeSettings(AppSettings s)
    {
        ThemeModeBox.SelectedIndex = (s.ThemeMode ?? "System").Trim().ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        SurfaceSlider.Value = Math.Clamp(s.SurfaceOpacity, 0.15, 1.0);
        OpacityValue.Text = $"{SurfaceSlider.Value * 100:0}%";

        BuildMaterialCards();
        BuildAccentSwatches();
        UpdateMaterialSelection();
        UpdateAccentSelection();
        UpdateThemeNotes();
    }

    private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        AppState.Settings.ThemeMode = ThemeModeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };

        ApplyTheme();

        // 预览卡里的「模拟壁纸」跟着深浅色走，换模式后整排重建一次
        BuildMaterialCards();
        UpdateMaterialSelection();
    }

    private void Surface_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{e.NewValue * 100:0}%";

        if (_loading) return;

        AppState.Settings.SurfaceOpacity = Math.Round(e.NewValue, 2);
        ApplyTheme();
    }

    private void Material_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int id) return;
        if (AppState.Settings.ThemeMaterial == id) return;

        AppState.Settings.ThemeMaterial = id;
        ApplyTheme();

        // 这台机器不支持该材质时，别让人以为「点了没反应」
        if (ThemeService.EffectiveMaterial != (ThemeMaterial)id)
            MainWindow.Instance?.Toast("这台机器的系统不支持该材质，已回落到纯色", true);
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string hex) return;

        AppState.Settings.AccentColor = hex;
        ApplyTheme();
    }

    private void AccentReset_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.AccentColor = "";
        ApplyTheme();
    }

    /// <summary>应用主题 + 刷新选中态；写盘走 220ms 防抖（拖动滑杆时别每一帧都落盘）。</summary>
    private void ApplyTheme()
    {
        ThemeService.Apply();

        UpdateMaterialSelection();
        UpdateAccentSelection();
        UpdateThemeNotes();

        _saveTimer ??= DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(220);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        SettingsService.Save();

        // 拖完不透明度滑杆后再重画预览卡（拖动过程中每帧重建会卡）
        BuildMaterialCards();
        UpdateMaterialSelection();
    }

    private void BuildMaterialCards()
    {
        MaterialHost.Children.Clear();
        _materialButtons.Clear();

        foreach (var (id, name, desc) in Materials)
        {
            var content = new StackPanel();
            content.Children.Add(BuildMaterialPreview(id));

            var texts = new StackPanel { Spacing = 1, Margin = new Thickness(2, 7, 2, 1) };
            // 用页面样式而不是直接塞 Foreground：样式里的颜色是 {ThemeResource}，
            // 会跟着页面实际主题走；直接塞画笔的话，深色模式下会拿到浅色那套（深字 + 白边）
            // FontFamily 必须显式给：这几张卡是事后 new 出来的，样式里的
            // StaticResource TextFontFamily 在 App.xaml 解析时就定死了，
            // FontService 又是靠遍历可视树来刷字体的 —— 新加的文本不在树上，
            // 只能等到下次导航 / 重开一次字体开关才被刷到（用户看到的「要重新点一下 Noto」就是这个）。
            texts.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontFamily = FontService.BodyFont,
                Style = ThemeService.Style("CardTitleTextStyle"),
            });
            texts.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11,
                FontFamily = FontService.BodyFont,
                Style = ThemeService.Style("CaptionTextStyle"),
            });
            content.Children.Add(texts);

            var button = new Button
            {
                Content = content,
                Tag = id,
                Padding = new Thickness(5),
                // 卡片自己有一层底色：纯透明的话，深色主题下整块和页面糊在一起，看不出这是可点的卡
                Background = ThemeService.Brush("CardHoverBackgroundBrush"),
                BorderBrush = ThemeService.Brush("DividerBrush"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(7),
                UseSystemFocusVisuals = false,
            };
            button.Click += Material_Click;

            MaterialHost.Children.Add(button);
            _materialButtons[id] = button;
        }
    }

    /// <summary>材质预览：一张模拟壁纸 + 一层按当前不透明度盖上去的内容层 + 一张卡片。</summary>
    private Border BuildMaterialPreview(int id)
    {
        bool dark = ThemeService.IsDark;
        var surfaceBase = dark ? Windows.UI.Color.FromArgb(255, 0x0D, 0x0D, 0x0D) : Windows.UI.Color.FromArgb(255, 0xFD, 0xFD, 0xFD);
        var cardBase = dark ? Windows.UI.Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

        var root = new Grid
        {
            Width = 128,
            Height = 64,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(surfaceBase),
        };

        // 预览不跟「内容层不透明度」走：滑到 100% 时四张卡会全变成一样的实色板，
        // 反而分不出材质差别。这里固定一档「能看出材质性格」的透度。
        double factor = id switch { 2 => 0.82, 3 => 0.60, _ => 1.0 };
        double surface = id == 0 ? 1.0 : Math.Clamp(0.52 * factor, 0.15, 0.90);
        double card = id == 0 ? 1.0 : Math.Min(1.0, surface + 0.36);

        if (id != 0)
        {
            // 模拟一张桌面壁纸：蓝 → 青 → 粉
            var wallpaper = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            wallpaper.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(255, 0x4C, 0x6F, 0xC8) });
            wallpaper.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Windows.UI.Color.FromArgb(255, 0x62, 0xBE, 0xB2) });
            wallpaper.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(255, 0xE0, 0x9F, 0xB8) });
            root.Background = wallpaper;

            root.Children.Add(new Border
            {
                Background = new SolidColorBrush(ThemeService.WithAlpha(surfaceBase, surface)),
            });
        }

        root.Children.Add(new Border
        {
            Width = 52,
            Height = 20,
            Margin = new Thickness(10, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ThemeService.WithAlpha(cardBase, card)),
        });

        return new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.Brush("DividerBrush"),
        };
    }

    private void BuildAccentSwatches()
    {
        AccentHost.Children.Clear();
        _accentRings.Clear();

        foreach (var (hex, name) in Accents)
        {
            var color = ThemeService.ParseHex(hex) ?? Microsoft.UI.Colors.Teal;
            var button = new Button
            {
                Tag = hex,
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeService.Brush("DividerBrush"),
                CornerRadius = new CornerRadius(15),
                UseSystemFocusVisuals = false,
            };
            ToolTipService.SetToolTip(button, name);
            button.Click += Accent_Click;

            var ring = new Border
            {
                Child = button,
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(19),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };

            AccentHost.Children.Add(ring);
            _accentRings.Add(ring);
        }

        // 自定义：色轮 / 色值
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsMoreButtonVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            MinWidth = 320,
            MinHeight = 340,
        };
        picker.Color = ThemeService.ParseHex(AppState.Settings.AccentColor) ?? (ThemeService.ParseHex(Accents[0].Hex) ?? Microsoft.UI.Colors.Teal);
        picker.ColorChanged += (s, args) =>
        {
            if (_loading) return;
            AppState.Settings.AccentColor = ThemeService.ToHex(args.NewColor);
            ApplyTheme();
        };

        var custom = new Button
        {
            Content = "自定义…",
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 5, 12, 5),
        };
        custom.Flyout = new Flyout { Content = picker };
        AccentHost.Children.Add(custom);

        var reset = new HyperlinkButton
        {
            Content = "用品牌默认色",
            VerticalAlignment = VerticalAlignment.Center,
        };
        reset.Click += AccentReset_Click;
        AccentHost.Children.Add(reset);
    }

    private void UpdateMaterialSelection()
    {
        var accent = ThemeService.Brush("BrandBrush");
        var idle = ThemeService.Brush("DividerBrush");

        foreach (var pair in _materialButtons)
        {
            bool selected = pair.Key == AppState.Settings.ThemeMaterial;
            pair.Value.BorderBrush = selected ? accent : idle;
        }
    }

    private void UpdateAccentSelection()
    {
        string current = (AppState.Settings.AccentColor ?? "").Trim().ToUpperInvariant();
        var accent = ThemeService.Brush("BrandBrush");

        for (int i = 0; i < _accentRings.Count && i < Accents.Length; i++)
        {
            bool selected = current.Length > 0 && current == Accents[i].Hex.ToUpperInvariant();
            _accentRings[i].BorderBrush = selected ? accent : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void UpdateThemeNotes()
    {
        var wanted = (ThemeMaterial)Math.Clamp(AppState.Settings.ThemeMaterial, 0, 4);
        var effective = ThemeService.EffectiveMaterial;

        MaterialNote.Text = wanted switch
        {
            ThemeMaterial.Solid => "纯色：整窗不透明，和旧版一致，老显卡最省。",
            ThemeMaterial.Mica => "云母：Windows 11 的默认观感，从桌面壁纸取一层极淡的色。",
            ThemeMaterial.MicaAlt => "云母 Alt：比标准云母更浓一些，适合大屏看内容。",
            ThemeMaterial.Acrylic => "亚克力：桌面被真实模糊成磨砂玻璃，最花哨也最吃一点性能。",
            _ => "亚克力：桌面被真实模糊成磨砂玻璃，最花哨也最吃一点性能。",
        };

        if (wanted != ThemeMaterial.Solid && effective != wanted)
            MaterialNote.Text += "　（这台机器的系统不支持该材质，已回落到纯色。）";

        AccentNote.Text = string.IsNullOrWhiteSpace(AppState.Settings.AccentColor)
            ? "当前用品牌默认色：浅色模式青绿、深色模式自动提亮。点色点或「自定义…」换一个。"
            : $"当前主题色 {AppState.Settings.AccentColor}，深色模式下会自动提亮，保证对比度。";
    }

    private static Brush Res(string key) => ThemeService.Brush(key);

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

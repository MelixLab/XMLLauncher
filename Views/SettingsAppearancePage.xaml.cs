using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class SettingsAppearancePage : Page
{
    private bool _loading = true;
    private bool? _previewIsDark;
    public SettingsAppearancePage()
    {
        InitializeComponent();
        LoadThemeSettings(AppState.Settings);
        FontSwitch.IsOn = AppState.Settings.UseNotoSans;
        LightGlowSwitch.IsOn = AppState.Settings.LightCardGlowEnabled;
        CardShapeBox.SelectedIndex = AppState.Settings.RoundedCards ? 0 : 1;
        GlobalCardShapeSwitch.IsOn = AppState.Settings.GlobalCardCornersEnabled;
        NewTitleBarSwitch.IsOn = AppState.Settings.UseNewTitleBar;
        UpdateCardShapeEditors();
        TransparencySlider.Value = Math.Clamp(AppState.Settings.AcrylicTransparency, 0, 100);
        VividnessSlider.Value = Math.Clamp(AppState.Settings.AcrylicVividness, 0, 100);
        UpdateAcrylicLabels();
        _loading = false;
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateMaterialPreviews);
        Loaded += (_, _) => UpdateMaterialPreviews();
        Unloaded += (_, _) => FlushPendingSave();
    }

    private void FlushPendingSave()
    {
        if (_saveTimer?.IsRunning != true) return;
        _saveTimer.Stop();
        SettingsService.Save();
    }

    private void SetAcrylicOptionsOpen(bool open)
    {
        MaterialCard.IsExpanded = open;
    }

    private void UpdateAcrylicLabels()
    {
        if (TransparencyValue is null || VividnessValue is null) return;
        TransparencyValue.Text = $"{TransparencySlider.Value:0}%";
        VividnessValue.Text = $"{VividnessSlider.Value:0}%";
    }

    private void Acrylic_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateAcrylicLabels();
        if (_loading) return;
        AppState.Settings.AcrylicTransparency = TransparencySlider.Value;
        AppState.Settings.AcrylicVividness = VividnessSlider.Value;
        ApplyTheme();
    }

    private void Font_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.UseNotoSans = FontSwitch.IsOn;
        SettingsService.Save();

        // 先换全局资源（管以后新建的控件），再把已有的刷一遍
        FontService.ApplyGlobal(FontSwitch.IsOn);
        FontService.RefreshAll();
    }

    private void LightGlow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.LightCardGlowEnabled = LightGlowSwitch.IsOn;
        ApplyTheme();
    }

    private void CardShape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CardShapeBox.SelectedIndex < 0 || AppState.Settings.GlobalCardCornersEnabled) return;
        AppState.Settings.RoundedCards = CardShapeBox.SelectedIndex == 0;
        ApplyTheme();
    }

    private void UpdateCardShapeEditors()
    {
        bool global = GlobalCardShapeSwitch.IsOn;
        InteractionCardShapeRow.IsEnabled = !global;
        InteractionCardShapeRow.Opacity = global ? 0.5 : 1;
    }

    private void GlobalCardShape_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.GlobalCardCornersEnabled = GlobalCardShapeSwitch.IsOn;
        UpdateCardShapeEditors();
        ApplyTheme();
    }

    private void NewTitleBar_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.UseNewTitleBar = NewTitleBarSwitch.IsOn;
        ApplyTheme();
    }

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

    private readonly Dictionary<int, ToggleButton> _materialButtons = new();
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
        if (sender is not ToggleButton button || button.Tag is not int id) return;
        button.IsChecked = true;
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

        // 预览使用手动生成的画笔，必须在主题引擎完成更新后同步，不能只依赖主题事件时序。
        UpdateMaterialPreviews();
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

            var button = new ToggleButton
            {
                Style = ThemeService.Style("SelectionCardStyle"),
                Content = content,
                Tag = id,
                Padding = new Thickness(5),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += Material_Click;

            MaterialHost.Children.Add(button);
            _materialButtons[id] = button;
        }
    }

    private void UpdateMaterialPreviews()
    {
        bool dark = IsLoaded ? ActualTheme == ElementTheme.Dark : ThemeService.IsDark;
        if (_previewIsDark == dark) return;
        foreach (var (id, button) in _materialButtons)
        {
            if (button.Content is StackPanel content)
                content.Children[0] = BuildMaterialPreview(id);
        }
        _previewIsDark = dark;
    }

    /// <summary>材质预览：一张模拟壁纸 + 一层按当前不透明度盖上去的内容层 + 一张卡片。</summary>
    private Border BuildMaterialPreview(int id)
    {
        bool dark = IsLoaded ? ActualTheme == ElementTheme.Dark : ThemeService.IsDark;
        var surfaceBase = dark ? Windows.UI.Color.FromArgb(255, 0x0D, 0x0D, 0x0D) : Windows.UI.Color.FromArgb(255, 0xFD, 0xFD, 0xFD);
        var cardBase = dark ? Windows.UI.Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

        var root = new Grid
        {
            Width = 128,
            Height = 64,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(surfaceBase),
        };
        CardAppearance.SetIsCard(root, true);

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

        var previewCard = new Border
        {
            Width = 52,
            Height = 20,
            Margin = new Thickness(10, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ThemeService.WithAlpha(cardBase, card)),
        };
        CardAppearance.SetIsCard(previewCard, true);
        root.Children.Add(previewCard);

        var preview = new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.Brush("DividerBrush"),
        };
        CardAppearance.SetIsCard(preview, true);
        return preview;
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
                UseSystemFocusVisuals = true,
            };
            CardAppearance.SetIsCard(button, true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name + "主题色");
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
            CardAppearance.SetIsCard(ring, true);

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
            MinWidth = 0,
            MaxWidth = 300,
            MinHeight = 340,
        };
        picker.Color = ThemeService.ParseHex(AppState.Settings.AccentColor) ?? (ThemeService.ParseHex(Accents[0].Hex) ?? Microsoft.UI.Colors.Teal);
        picker.ColorChanged += (s, args) =>
        {
            if (_loading) return;
            AppState.Settings.AccentColor = ThemeService.ToHex(args.NewColor);
            ApplyTheme();
        };

        CustomAccentButton.Flyout = new Flyout { Content = picker };
    }

    private void AccentLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 520;
        Grid.SetColumn(AccentActions, narrow ? 0 : 1);
        Grid.SetRow(AccentActions, narrow ? 1 : 0);
        AccentLayout.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : GridLength.Auto;
        AccentLayout.ColumnSpacing = narrow ? 0 : 16;
    }

    private void UpdateMaterialSelection()
    {
        foreach (var pair in _materialButtons)
        {
            pair.Value.IsChecked = pair.Key == AppState.Settings.ThemeMaterial;
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
        SetAcrylicOptionsOpen(AppState.Settings.ThemeMaterial == 3);
        var wanted = (ThemeMaterial)Math.Clamp(AppState.Settings.ThemeMaterial, 0, 3);
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
            ? "当前用品牌默认色：浅色模式青绿、深色模式自动提亮。点色点或「自定义」换一个。"
            : $"当前主题色 {AppState.Settings.AccentColor}，深色模式下会自动提亮，保证对比度。";
    }

}

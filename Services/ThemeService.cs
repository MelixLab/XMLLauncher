using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace LinxLauncher.Services;

/// <summary>窗口材质。数值直接存进 AppSettings，别随意改序号。</summary>
public enum ThemeMaterial
{
    /// <summary>纯色：老样子，整窗不透明。</summary>
    Solid = 0,

    /// <summary>云母：跟随桌面壁纸取色，Win11 的默认观感。</summary>
    Mica = 1,

    /// <summary>云母 Alt：比标准云母更浓、更「有壁纸感」，适合大面积内容。</summary>
    MicaAlt = 2,

    /// <summary>亚克力：磨砂玻璃，桌面内容会被真实模糊。</summary>
    Acrylic = 3,
}

/// <summary>
/// 主题引擎：窗口材质（云母 / 亚克力）、主题色、深浅模式。
///
/// 做法上刻意**不去改 XAML 里的每一处颜色**，而是改资源字典里的画笔对象：
/// 画笔是 DependencyObject，各家控件拿到的都是同一份实例，
/// 改一次 Brush.Color，界面上所有引用它的位置立刻跟着变（不用重建页面）。
///
/// 深色 / 浅色两套字典都会刷一遍，所以运行中切深浅模式不会出现「换了一半」。
/// </summary>
public static class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class Slot
    {
        public Slot(ResourceDictionary dict, bool isDark, bool ours)
        {
            Dict = dict;
            IsDark = isDark;
            Ours = ours;
        }

        public ResourceDictionary Dict { get; }
        public bool IsDark { get; }

        /// <summary>是不是 Styles/Theme.xaml 里那套（我们自己写的画笔，可以整组重刷）。</summary>
        public bool Ours { get; }
    }

    private static readonly List<Slot> Slots = new();
    private static readonly List<ResourceDictionary> Seen = new();
    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new();

    private static Window? _window;
    private static FrameworkElement? _root;
    private static FrameworkElement? _navShell;

    private static bool _applying;
    private static bool _initialized;

    /// <summary>实际生效的材质（不支持云母的机器会回落成纯色）。</summary>
    public static ThemeMaterial EffectiveMaterial { get; private set; } = ThemeMaterial.Solid;

    /// <summary>当前挂着的材质；背景宿主只创建一次，材质控制器按需复用。</summary>
    private static ThemeMaterial? _currentBackdrop;
    private static WindowBackdrop? _backdrop;

    // 等最后一次选择停稳，也留出底色提交到渲染线程的时间，避免连点时逐次切换中间材质。
    private static readonly TimeSpan BackdropRequestDelay = TimeSpan.FromMilliseconds(120);
    // 系统材质没有“首帧已就绪”事件，先留出稳定期，再渐隐承接底色；时长可按实机观感调整。
    private static readonly TimeSpan BackdropSettleDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BackdropRevealDuration = TimeSpan.FromMilliseconds(120);
    private static bool _backdropTransitionActive;
    private static double _backdropTransitionOpacity;
    private static ThemeMaterial? _pendingBackdrop;
    private static TimeSpan? _backdropCoverFrame;
    private static TimeSpan? _backdropChangedAt;

    /// <summary>界面此刻实际是深色还是浅色（用户选的模式 + 系统主题算出来的结果）。</summary>
    public static bool IsDark { get; private set; }

    // ================= 对外入口 =================

    /// <summary>启动时调一次：把窗口、根元素、侧边栏交给主题引擎。</summary>
    public static void Initialize(Window window, FrameworkElement root, FrameworkElement navShell)
    {
        _window = window;
        _root = root;
        _navShell = navShell;

        // 系统切换深浅色时，跟随模式的用户要立刻跟上（ActualTheme 只在 RequestedTheme=Default 时才会变）
        root.ActualThemeChanged += (_, _) => Apply();
        window.Closed += (_, _) =>
        {
            StopBackdropTransition();
            _initialized = false;
            _backdrop = null;
            _currentBackdrop = null;
            _window = null;
            _root = null;
            _navShell = null;
        };

        _initialized = true;
        Apply();
    }

    /// <summary>按当前 AppState.Settings 重新刷一遍主题。设置页每次改动都调它。</summary>
    public static void Apply()
    {
        if (!_initialized || _applying) return;
        if (_window is null || _root is null) return;

        _applying = true;
        try
        {
            var s = AppState.Settings;

            var choice = ParseMode(s.ThemeMode);
            var want = choice switch
            {
                ThemeMode.Light => ElementTheme.Light,
                ThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            if (_root.RequestedTheme != want) _root.RequestedTheme = want;

            bool dark = choice switch
            {
                ThemeMode.Dark => true,
                ThemeMode.Light => false,
                _ => _root.ActualTheme == ElementTheme.Dark,
            };
            IsDark = dark;

            var material = ResolveMaterial(s.ThemeMaterial);
            EffectiveMaterial = material;
            ApplyBackdrop(material);

            ApplyPalette(s, EffectiveMaterial);
            RefreshLiveBrushes();
            ApplyTitleBar(dark);
        }
        catch (Exception ex)
        {
            Log.Warn("应用主题失败：" + ex.Message);
        }
        finally
        {
            _applying = false;
        }
    }

    private enum ThemeMode
    {
        System,
        Light,
        Dark,
    }

    private static ThemeMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    // ================= 材质 =================

    private static ThemeMaterial ResolveMaterial(int raw)
    {
        var m = (ThemeMaterial)Math.Clamp(raw, 0, (int)ThemeMaterial.Acrylic);
        if (m == ThemeMaterial.Solid) return ThemeMaterial.Solid;

        try
        {
            if (m is ThemeMaterial.Mica or ThemeMaterial.MicaAlt)
                return MicaController.IsSupported() ? m : ThemeMaterial.Solid;

            return DesktopAcrylicController.IsSupported() ? m : ThemeMaterial.Solid;
        }
        catch
        {
            return ThemeMaterial.Solid;
        }
    }

    private static void ApplyBackdrop(ThemeMaterial material)
    {
        if (_backdropTransitionActive && (_pendingBackdrop ?? _currentBackdrop) == material) return;

        if (!_backdropTransitionActive)
        {
            if (_currentBackdrop == material) return;

            // 两种云母只改同一控制器的 Kind。已有过渡未结束时不能走此捷径，
            // 否则快速点回云母会提前移除遮挡，露出上一轮尚未稳定的背景。
            bool switchMicaKind = _currentBackdrop is ThemeMaterial.Mica or ThemeMaterial.MicaAlt
                                  && material is ThemeMaterial.Mica or ThemeMaterial.MicaAlt;
            if (_currentBackdrop is null || !_root!.IsLoaded || switchMicaKind)
            {
                ReplaceBackdrop(material);
                return;
            }
        }

        // 过渡中点回原材质也保留遮挡，只更新最后一次选择，不中止正在稳定的背景。
        _pendingBackdrop = material;
        _backdropCoverFrame = null;
        // 保留上一次实际切换的时间：新请求即使已防抖，也要等旧背景结束稳定期。
        _backdropTransitionOpacity = 1;
        if (!_backdropTransitionActive)
        {
            _backdropTransitionActive = true;
            CompositionTarget.Rendering += BackdropTransition_Rendering;
        }
    }

    private static void ReplaceBackdrop(ThemeMaterial material)
    {
        try
        {
            if (_backdrop is null)
            {
                // 一直使用纯色的窗口无须创建材质宿主；第一次启用材质后保留到窗口关闭。
                if (material != ThemeMaterial.Solid)
                {
                    _backdrop = new WindowBackdrop(material);
                    _window!.SystemBackdrop = _backdrop;
                }
            }
            else
            {
                _backdrop.SetMaterial(material);
            }
            _currentBackdrop = material;
        }
        catch (Exception ex)
        {
            Log.Warn("切换窗口材质失败：" + ex.Message);
            _currentBackdrop = ThemeMaterial.Solid;
            EffectiveMaterial = ThemeMaterial.Solid;
            try { _backdrop?.SetMaterial(ThemeMaterial.Solid); } catch { }
        }
    }

    private static void BackdropTransition_Rendering(object? sender, object args)
    {
        if (!_backdropTransitionActive || args is not RenderingEventArgs frame) return;

        try
        {
            if (_pendingBackdrop is { } material)
            {
                // Rendering 在绘制之前触发，不能把下一次回调当成画面已显示。
                // 保持底色一段时间，并合并这段时间内的新选择，再实际切换控制器。
                if (_backdropCoverFrame is null)
                {
                    _backdropCoverFrame = frame.RenderingTime;
                    return;
                }
                if (frame.RenderingTime - _backdropCoverFrame.Value < BackdropRequestDelay) return;
                if (_backdropChangedAt is { } previousChange
                    && frame.RenderingTime - previousChange < BackdropSettleDuration) return;

                _pendingBackdrop = null;
                ReplaceBackdrop(material);
                _backdropChangedAt = frame.RenderingTime;
                if (EffectiveMaterial != ThemeMaterial.Solid) return;

                // 切到纯色或重建失败时保持不透明，不再等待材质显现。
                StopBackdropTransition();
                ApplyPalette(AppState.Settings, EffectiveMaterial);
                RefreshLiveBrushes();
                return;
            }

            if (_backdropChangedAt is not { } changedAt) return;
            var elapsed = frame.RenderingTime - changedAt;
            if (elapsed < BackdropSettleDuration) return;

            double progress = Math.Clamp(
                (elapsed - BackdropSettleDuration).TotalMilliseconds / BackdropRevealDuration.TotalMilliseconds, 0, 1);
            _backdropTransitionOpacity = 1 - progress;
            if (progress >= 1) StopBackdropTransition();
            RefreshPaneBackground();
        }
        catch (Exception ex)
        {
            Log.Warn("窗口材质过渡失败：" + ex.Message);
            StopBackdropTransition();
            RefreshPaneBackground();
        }
    }

    private static void StopBackdropTransition()
    {
        if (_backdropTransitionActive)
            CompositionTarget.Rendering -= BackdropTransition_Rendering;

        _backdropTransitionActive = false;
        _backdropTransitionOpacity = 0;
        _pendingBackdrop = null;
        _backdropCoverFrame = null;
        _backdropChangedAt = null;
    }

    private static void RefreshPaneBackground()
    {
        double opacity = EffectiveMaterial == ThemeMaterial.Solid ? 1 : _backdropTransitionOpacity;
        foreach (var slot in Slots)
        {
            // 只改窗口底层；保留 ThemeResource 引用，让过渡期间切深浅模式也能立即同步。
            if (TryGet(slot.Dict, "WindowBackgroundBrush", out var value) && value is SolidColorBrush background)
                SetBrush(slot.Dict, "PaneBackgroundBrush", WithAlpha(background.Color, opacity));
        }

        if (LiveBrushes.TryGetValue("PaneBackgroundBrush", out var live)
            && ResolveBrush("PaneBackgroundBrush") is SolidColorBrush source)
            live.Color = source.Color;
    }

    // ================= 配色 =================

    /// <summary>表面画笔的透明度档位。</summary>
    private enum Alpha
    {
        /// <summary>永远不透明（分隔线、边框）。</summary>
        Solid,

        /// <summary>窗口底：材质稳定后全透明，重建期间用当前主题底色承接。</summary>
        Pane,

        /// <summary>页面层：跟「表面不透明度」走。</summary>
        Surface,

        /// <summary>卡片层：比页面再实一点，保证文字可读。</summary>
        Card,

        /// <summary>按钮 / 侧栏这类「近处」元素：最实。</summary>
        Chrome,
    }

    private sealed class SurfaceKey
    {
        public SurfaceKey(string key, Alpha alpha, string light, string dark)
        {
            Key = key;
            Alpha = alpha;
            Light = ParseHex(light)!.Value;
            Dark = ParseHex(dark)!.Value;
        }

        public string Key { get; }
        public Alpha Alpha { get; }
        public Color Light { get; }
        public Color Dark { get; }
    }

    private static readonly SurfaceKey[] Surfaces =
    {
        new("PaneBackgroundBrush", Alpha.Pane, "#FFFFFF", "#000000"),
        new("NavigationPaneBackgroundBrush", Alpha.Solid, "#F2F2F2", "#2B2B2B"),
        new("WindowBackgroundBrush", Alpha.Solid, "#FFFFFF", "#000000"),
        new("PageLayerBackgroundBrush", Alpha.Surface, "#FDFDFD", "#000000"),
        new("CardBackgroundBrush", Alpha.Card, "#FFFFFF", "#1C1C1C"),
        new("CardHoverBackgroundBrush", Alpha.Card, "#F2F2F2", "#262626"),
        new("CardPressedBackgroundBrush", Alpha.Card, "#EFEFEF", "#303030"),
        new("NavSelectionBackgroundBrush", Alpha.Card, "#F6F6F6", "#262626"),
        new("ThreatCardBackgroundBrush", Alpha.Card, "#F2F2F2", "#1C1C1C"),
        new("ThreatCardHoverBackgroundBrush", Alpha.Card, "#EAEAEA", "#262626"),
        new("RecordExpandedBackgroundBrush", Alpha.Card, "#F2F2F2", "#1F1F1F"),
        new("RecordHoverBackgroundBrush", Alpha.Card, "#EDEDED", "#262626"),
        new("RecordPressedBackgroundBrush", Alpha.Card, "#E6E6E6", "#2E2E2E"),
        new("CardActionBackgroundBrush", Alpha.Chrome, "#D2D2D2", "#323232"),
        new("CardActionHoverBrush", Alpha.Chrome, "#C6C6C6", "#3D3D3D"),
        new("CardActionPressedBrush", Alpha.Chrome, "#B9B9B9", "#484848"),
        new("DividerBrush", Alpha.Solid, "#E5E5E5", "#2E2E2E"),
        new("PageLayerBorderBrush", Alpha.Solid, "#F0F0F0", "#2A2A2A"),
        new("RadioHoleBrush", Alpha.Solid, "#FFFFFF", "#000000"),
    };

    private static void ApplyPalette(AppSettings settings, ThemeMaterial material)
    {
        EnsureSlots();

        bool on = material != ThemeMaterial.Solid;

        // 不同材质给一个「透度系数」：同样是 50% 不透明度，高透要明显比云母更透
        double factor = material switch
        {
            ThemeMaterial.MicaAlt => 0.82,
            ThemeMaterial.Acrylic => 0.70,
            _ => 1.0,
        };

        double surface = on ? Math.Clamp(settings.SurfaceOpacity * factor, 0.08, 1.0) : 1.0;
        double card = on ? Math.Min(1.0, surface + 0.36) : 1.0;
        double chrome = on ? Math.Min(1.0, surface + 0.62) : 1.0;

        var userAccent = ParseHex(settings.AccentColor);

        foreach (var slot in Slots)
        {
            bool dark = slot.IsDark;

            // ---- 表面 ----
            foreach (var spec in Surfaces)
            {
                var baseColor = dark ? spec.Dark : spec.Light;

                // 不透明区域（高对比、分隔线）永远满 alpha
                double alpha = spec.Alpha switch
                {
                    Alpha.Pane => on ? _backdropTransitionOpacity : 1,
                    Alpha.Surface => surface,
                    Alpha.Card => card,
                    Alpha.Chrome => chrome,
                    _ => 1,
                };

                SetBrush(slot.Dict, spec.Key, WithAlpha(baseColor, alpha));
            }

            // ---- 主题色 ----
            var brand = ResolveBrand(userAccent, dark);
            var soft = dark ? Mix(brand, Colors.Black, 0.80) : Mix(brand, Colors.White, 0.87);
            var onBrand = Luminance(brand) > 0.62 ? Color.FromArgb(255, 8, 20, 18) : Colors.White;
            var link = dark ? Mix(brand, Colors.White, 0.25) : Mix(brand, Colors.Black, 0.30);

            SetBrush(slot.Dict, "BrandBrush", brand);
            SetBrush(slot.Dict, "BrandSoftBrush", WithAlpha(soft, on ? card : 1));
            SetBrush(slot.Dict, "OnBrandTextBrush", onBrand);
            SetBrush(slot.Dict, "NavSelectionIndicatorBrush", brand);
            SetBrush(slot.Dict, "RadioCircleOnBrush", brand);
            SetBrush(slot.Dict, "SecurityBlueBrush", brand);
            SetBrush(slot.Dict, "LinkTextBrush", link);
            SetBrush(slot.Dict, "ToggleSwitchFillOn", brand);
            SetBrush(slot.Dict, "ToggleSwitchFillOnPointerOver", dark ? Mix(brand, Colors.White, 0.18) : Mix(brand, Colors.Black, 0.10));
            SetBrush(slot.Dict, "ToggleSwitchFillOnPressed", dark ? Mix(brand, Colors.White, 0.10) : Mix(brand, Colors.Black, 0.18));

            // 系统控件（按钮、进度条、复选框）的强调色跟着一起走
            if (!slot.Ours)
            {
                SetBrushKeepAlpha(slot.Dict, "AccentFillColorDefaultBrush", brand);
                SetBrushKeepAlpha(slot.Dict, "AccentFillColorSecondaryBrush", brand);
                SetBrushKeepAlpha(slot.Dict, "AccentFillColorTertiaryBrush", brand);
                SetBrushKeepAlpha(slot.Dict, "SystemControlHighlightAccentBrush", brand);
                SetBrushKeepAlpha(slot.Dict, "SystemControlBackgroundAccentBrush", brand);
                SetBrushKeepAlpha(slot.Dict, "AccentTextFillColorPrimaryBrush", link);
                SetBrushKeepAlpha(slot.Dict, "TextOnAccentFillColorPrimaryBrush", onBrand);
            }
        }

        ApplyNavPalette(surface, card, chrome, userAccent);
    }

    /// <summary>侧边栏 NavigationView 的配色写在 MainWindow.xaml 自己的资源里，这里单独刷。</summary>
    private static void ApplyNavPalette(double surface, double card, double chrome, Color? userAccent)
    {
        if (_navShell is null) return;

        try
        {
            foreach (var pair in _navShell.Resources.ThemeDictionaries)
            {
                if (pair.Value is not ResourceDictionary dict) continue;

                string key = pair.Key?.ToString() ?? "";
                bool dark = key.Equals("Dark", StringComparison.OrdinalIgnoreCase);

                var brand = ResolveBrand(userAccent, dark);
                var softBase = dark ? Mix(brand, Colors.Black, 0.78) : Mix(brand, Colors.White, 0.86);
                var neutralHover = dark ? Color.FromArgb(255, 0x2A, 0x2A, 0x2A) : Color.FromArgb(255, 0xED, 0xED, 0xED);
                var neutralPressed = dark ? Color.FromArgb(255, 0x32, 0x32, 0x32) : Color.FromArgb(255, 0xE6, 0xE6, 0xE6);
                SetBrush(dict, "NavigationViewContentBackground", WithAlpha(dark ? Colors.Black : Color.FromArgb(255, 0xFD, 0xFD, 0xFD), surface));
                SetBrush(dict, "NavigationViewItemBackgroundSelected", WithAlpha(softBase, card));
                SetBrush(dict, "NavigationViewItemBackgroundSelectedPointerOver", WithAlpha(Mix(softBase, brand, 0.12), card));
                SetBrush(dict, "NavigationViewItemBackgroundSelectedPressed", WithAlpha(Mix(softBase, brand, 0.22), card));
                SetBrush(dict, "NavigationViewItemBackgroundPointerOver", WithAlpha(neutralHover, chrome));
                SetBrush(dict, "NavigationViewItemBackgroundPressed", WithAlpha(neutralPressed, chrome));
                SetBrush(dict, "NavigationViewSelectionIndicatorForeground", brand);
                SetBrush(dict, "NavigationViewItemForegroundSelected", dark ? Mix(brand, Colors.White, 0.55) : Mix(brand, Colors.Black, 0.55));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("刷侧边栏配色失败：" + ex.Message);
        }
    }

    private static Color ResolveBrand(Color? userAccent, bool dark)
    {
        if (userAccent is { } custom)
            return dark ? Mix(custom, Colors.White, 0.42) : custom;

        return dark ? Color.FromArgb(255, 0x35, 0xD4, 0xBA) : Color.FromArgb(255, 0x00, 0xA8, 0x8E);
    }

    // ================= 标题栏 =================

    private static void ApplyTitleBar(bool dark)
    {
        if (_window is null) return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("设置暗色标题栏失败：" + ex.Message);
        }

        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var bar = _window.AppWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(255, 0x1B, 0x1B, 0x1B);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x30, 0x00, 0x00, 0x00);
            bar.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
        }
        catch { }
    }

    // ================= 资源字典工具 =================

    private static void EnsureSlots()
    {
        if (Slots.Count > 0) return;

        try
        {
            Collect(Application.Current.Resources);
        }
        catch (Exception ex)
        {
            Log.Warn("收集主题资源失败：" + ex.Message);
        }
    }

    private static void Collect(ResourceDictionary dict)
    {
        if (dict is null) return;
        if (Seen.Contains(dict)) return;
        Seen.Add(dict);

        try
        {
            foreach (var pair in dict.ThemeDictionaries)
            {
                if (pair.Value is not ResourceDictionary themeDict) continue;

                string key = pair.Key?.ToString() ?? "";
                if (key.Equals("HighContrast", StringComparison.OrdinalIgnoreCase)) continue;

                bool ours = themeDict.ContainsKey("BrandBrush");

                // Theme.xaml 的 Default 是浅色那套；WinUI 自带资源里的 Default 才是深色
                bool isDark = key.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                              || (key.Equals("Default", StringComparison.OrdinalIgnoreCase) && !ours);

                Slots.Add(new Slot(themeDict, isDark, ours));
            }
        }
        catch { }

        try
        {
            foreach (var child in dict.MergedDictionaries) Collect(child);
        }
        catch { }
    }

    private static bool TryGet(ResourceDictionary dict, string key, out object value)
    {
        value = null!;
        try
        {
            if (!dict.ContainsKey(key)) return false;
            value = dict[key];
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 取当前**实际生效**主题下的一支画笔。
    ///
    /// 代码里 new 出来的控件写不了 {ThemeResource}，而 Application.Current.Resources[key]
    /// 取的是「系统主题」那一套：用户选深色、我们只在 RootGrid 上覆盖主题时，
    /// 从这里拿到的还是浅色的笔刷（深色界面上冒出一圈白边框、深字看不见，就是这么来的）。
    /// 按 IsDark 解析颜色，并返回稳定的画笔实例；切换主题时更新这些实例，
    /// 让已经创建的控件和绑定模型也能同步变色。
    /// </summary>
    public static Brush Brush(string key)
    {
        if (LiveBrushes.TryGetValue(key, out var live)) return live;

        var source = ResolveBrush(key);
        if (source is not SolidColorBrush solid) return source;

        live = new SolidColorBrush(solid.Color);
        LiveBrushes.Add(key, live);
        return live;
    }

    private static void RefreshLiveBrushes()
    {
        foreach (var (key, brush) in LiveBrushes)
        {
            if (ResolveBrush(key) is SolidColorBrush source)
                brush.Color = source.Color;
        }
    }

    private static Brush ResolveBrush(string key)
    {
        EnsureSlots();

        foreach (var slot in Slots)
        {
            if (!slot.Ours || slot.IsDark != IsDark) continue;
            if (TryGet(slot.Dict, key, out var value) && value is Brush brush) return brush;
        }

        try
        {
            if (Application.Current.Resources[key] is Brush fallback) return fallback;
        }
        catch { }

        return new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>取一支样式（样式本身不分深浅，里面的颜色用 {ThemeResource} 会自己跟着走）。</summary>
    public static Style? Style(string key)
    {
        try
        {
            return Application.Current.Resources[key] as Style;
        }
        catch
        {
            return null;
        }
    }

    private static void SetBrush(ResourceDictionary dict, string key, Color color)
    {
        try
        {
            if (!TryGet(dict, key, out var value)) return;
            if (value is SolidColorBrush brush) brush.Color = color;
        }
        catch { }
    }

    /// <summary>只换 RGB、保留原 alpha（系统强调色画笔本身带透明度档位）。</summary>
    private static void SetBrushKeepAlpha(ResourceDictionary dict, string key, Color color)
    {
        try
        {
            if (!TryGet(dict, key, out var value)) return;
            if (value is SolidColorBrush brush)
            {
                var old = brush.Color;
                brush.Color = Color.FromArgb(old.A, color.R, color.G, color.B);
            }
        }
        catch { }
    }

    // ================= 颜色工具 =================

    public static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        var text = hex.Trim().TrimStart('#');
        try
        {
            return text.Length switch
            {
                3 => Color.FromArgb(
                    255,
                    Convert.ToByte(new string(text[0], 2), 16),
                    Convert.ToByte(new string(text[1], 2), 16),
                    Convert.ToByte(new string(text[2], 2), 16)),
                6 => Color.FromArgb(
                    255,
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16)),
                8 => Color.FromArgb(
                    Convert.ToByte(text.Substring(0, 2), 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16)),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}

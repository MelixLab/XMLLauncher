using System;
using System.Runtime.InteropServices.WindowsRuntime;
using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Controls;

/// <summary>概览页功能卡片：图标 + 标题 + 状态 + 可选操作，带 Defender 式鼠标 Reveal。</summary>
public sealed partial class FeatureCard : UserControl
{
    private const double AmbientReach = 150.0;
    private const int SurfaceTextureSize = 384;
    private const byte DarkRevealPeakAlpha = 0x90;
    private const byte LightRevealPeakAlpha = 0x58;
    private static WriteableBitmap? _lightTexture;
    private static WriteableBitmap? _darkTexture;

    public static readonly DependencyProperty IconTemplateProperty =
        DependencyProperty.Register(nameof(IconTemplate), typeof(DataTemplate), typeof(FeatureCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(FeatureCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string), typeof(FeatureCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(FeatureCard),
            new PropertyMetadata(null));

    /// <summary>
    /// 状态行最少占多高。文案长度不一时，给个两行的高度，卡片里的按钮才会落在同一条线上。
    /// 默认 0 = 按内容走（首页就是这种行为）。
    /// </summary>
    public static readonly DependencyProperty StatusMinHeightProperty =
        DependencyProperty.Register(nameof(StatusMinHeight), typeof(double), typeof(FeatureCard),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty NavTagProperty =
        DependencyProperty.Register(nameof(NavTag), typeof(string), typeof(FeatureCard),
            new PropertyMetadata(string.Empty, (sender, _) => ((FeatureCard)sender).UpdateNavigation()));

    private RadialGradientBrush? _revealBrush;
    private readonly TranslateTransform _surfacePosition = new();
    private bool _inside;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private long _pressedToken;

    public FeatureCard()
    {
        InitializeComponent();
        CardAppearance.SetIsInteractiveCard(this, true);
        CardAppearance.SetIsCard(this, true);
        BuildRevealBrush();
        InteractionMotion.SetHoverScale(NavigationButton, 1.008);
        InteractionMotion.SetPressedScale(NavigationButton, 0.985);
        InteractionMotion.Attach(NavigationButton, Root);
        ActualThemeChanged += (_, _) => BuildRevealBrush();
        Loaded += (_, _) =>
        {
            UpdateNavigation();
            ConfigureMotion();
            _pressedToken = NavigationButton.RegisterPropertyChangedCallback(
                Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.IsPressedProperty, (_, _) => UpdatePressedFill());
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged += MotionSettingsChanged;
            _uiSettings.ColorValuesChanged += ContrastSettingsChanged;
            ThemeService.AppearanceChanged += AppearanceChanged;
        };
        Unloaded += (_, _) =>
        {
            NavigationButton.UnregisterPropertyChangedCallback(
                Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.IsPressedProperty, _pressedToken);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged -= MotionSettingsChanged;
            _uiSettings.ColorValuesChanged -= ContrastSettingsChanged;
            ThemeService.AppearanceChanged -= AppearanceChanged;
            ResetReveal();
            PressedFill.Opacity = 0;
        };
        PointerMoved += (_, e) =>
        {
            var point = e.GetCurrentPoint(this).Position;
            UpdateReveal(point.X, point.Y);
        };
        // 父级容器继续驱动邻近描边；离开单张卡片时只收起表面光，不清除余光。
    }

    /// <summary>卡片图标（Icons.xaml 里的 DataTemplate，48×48 画布）。</summary>
    public DataTemplate? IconTemplate
    {
        get => (DataTemplate?)GetValue(IconTemplateProperty);
        set => SetValue(IconTemplateProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public double StatusMinHeight
    {
        get => (double)GetValue(StatusMinHeightProperty);
        set => SetValue(StatusMinHeightProperty, value);
    }

    public string NavTag
    {
        get => (string)GetValue(NavTagProperty);
        set => SetValue(NavTagProperty, value);
    }

    // ==================================================================
    //  Reveal
    // ==================================================================

    public void UpdateReveal(double px, double py)
    {
        if (_revealBrush is null) return;
        ConfigureMotion();
        if (!IsEnabled || _accessibilitySettings.HighContrast) { ResetReveal(); return; }

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double dx = Math.Max(0.0, Math.Max(-px, px - w));
        double dy = Math.Max(0.0, Math.Max(-py, py - h));
        bool inside = dx <= 0.0 && dy <= 0.0;

        if (_inside != inside)
        {
            _inside = inside;
            UpdateHoverFeedback();
        }
        // 缝隙两侧与卡片内部使用连续的强度，避免边界处从 1 突然降到 0.45。
        SetOpacity(RevealStroke, Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / AmbientReach));

        // 绝对 DIP 半径让宽卡、窄卡上的光斑保持圆形，并让相邻卡片的余光自然衰减。
        var origin = new Point(px, py);

        _revealBrush.Center = origin;
        _revealBrush.GradientOrigin = origin;
        _surfacePosition.X = px - SurfaceTextureSize / 2;
        _surfacePosition.Y = py - SurfaceTextureSize / 2;
    }

    public void ResetReveal()
    {
        _inside = false;
        HoverFill.Opacity = 0;
        SurfaceGlow.Opacity = 0;
        SetOpacity(RevealStroke, 0.0);
    }

    private void BuildRevealBrush()
    {
        bool dark = ActualTheme == ElementTheme.Dark;

        // 深色白光、浅色柔和阴影；表面开关不改变相邻卡片的边缘追光。
        Color Shade(byte alpha) => dark
            ? Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(alpha, 0x00, 0x00, 0x00);

        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            RadiusX = 180,
            RadiusY = 180,
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = Shade(dark ? DarkRevealPeakAlpha : LightRevealPeakAlpha) });
        brush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = Shade(0x00) });

        _revealBrush = brush;
        RevealStroke.BorderBrush = brush;

        // 低透明度渐变只有少量 alpha 档位；预计算抖动采样打散量化色圈。
        SurfaceGlow.Background = new ImageBrush
        {
            ImageSource = GetSurfaceTexture(dark),
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Transform = _surfacePosition,
        };
        ResetReveal();
    }

    private static WriteableBitmap GetSurfaceTexture(bool dark)
    {
        if ((dark ? _darkTexture : _lightTexture) is { } cached) return cached;
        var bitmap = new WriteableBitmap(SurfaceTextureSize, SurfaceTextureSize);
        var pixels = new byte[SurfaceTextureSize * SurfaceTextureSize * 4];
        var noise = new Random(20261101);
        double radius = SurfaceTextureSize / 2.0;
        for (int y = 0; y < SurfaceTextureSize; y++)
        for (int x = 0; x < SurfaceTextureSize; x++)
        {
            double dx = (x + 0.5 - radius) / radius;
            double dy = (y + 0.5 - radius) / radius;
            double r2 = dx * dx + dy * dy;
            double envelope = r2 >= 1 ? 0 : Math.Exp(-3 * r2) * Math.Pow(1 - r2, 2);
            byte alpha = (byte)((dark ? 32 : 16) * envelope + noise.NextDouble());
            int offset = (y * SurfaceTextureSize + x) * 4;
            // WriteableBitmap 使用预乘 BGRA；深色白光、浅色黑色低透明度阴影。
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = dark ? alpha : (byte)0;
            pixels[offset + 3] = alpha;
        }
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        if (dark) _darkTexture = bitmap;
        else _lightTexture = bitmap;
        return bitmap;
    }

    private static void SetOpacity(UIElement element, double value)
    {
        if (Math.Abs(element.Opacity - value) > 0.02)
            element.Opacity = value;
    }

    private void UpdateHoverFeedback()
    {
        bool hovered = _inside && IsEnabled && !_accessibilitySettings.HighContrast;
        bool useGlow = ActualTheme == ElementTheme.Dark || AppState.Settings.LightCardGlowEnabled;
        SurfaceGlow.Opacity = hovered && useGlow ? 1 : 0;
        // 仅在用户关闭浅色光晕时，用整卡灰底提供替代悬停反馈。
        HoverFill.Opacity = hovered && !useGlow ? 1 : 0;
    }

    private void AppearanceChanged() => UpdateHoverFeedback();

    // ==================================================================
    //  点击跳转
    // ==================================================================

    private void UpdateNavigation()
    {
        if (NavigationButton is null) return;
        NavigationButton.Visibility = string.IsNullOrWhiteSpace(NavTag) ? Visibility.Collapsed : Visibility.Visible;
        UpdatePressedFill();
    }

    private bool? _animationsEnabled;

    private void ConfigureMotion()
    {
        bool enabled = _uiSettings.AnimationsEnabled && !_accessibilitySettings.HighContrast;
        if (_animationsEnabled == enabled) return;
        _animationsEnabled = enabled;
        HoverFill.OpacityTransition = enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        RevealStroke.OpacityTransition = enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(100) } : null;
        SurfaceGlow.OpacityTransition = enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        PressedFill.OpacityTransition = enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(70) } : null;
    }

    private void UpdatePressedFill()
    {
        ConfigureMotion();
        PressedFill.Opacity = NavigationButton.IsPressed ? 1 : 0;
    }

    private void MotionSettingsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { ConfigureMotion(); ResetReveal(); });

    private void ContrastSettingsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { ResetReveal(); UpdatePressedFill(); });

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NavTag)) MainWindow.Instance?.NavigateTo(NavTag);
    }
}

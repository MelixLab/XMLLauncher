using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace LinxLauncher.Controls;

/// <summary>概览页功能卡片：图标 + 标题 + 状态 + 可选操作，带 Defender 式鼠标 Reveal。</summary>
public sealed partial class FeatureCard : UserControl
{
    private const double AmbientReach = 150.0;
    private const double AmbientStrength = 0.45;

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
            new PropertyMetadata(string.Empty));

    private RadialGradientBrush? _revealBrush;

    public FeatureCard()
    {
        InitializeComponent();
        BuildRevealBrush();
        ActualThemeChanged += (_, _) => BuildRevealBrush();
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

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double dx = Math.Max(0.0, Math.Max(-px, px - w));
        double dy = Math.Max(0.0, Math.Max(-py, py - h));
        bool inside = dx <= 0.0 && dy <= 0.0;

        double strokeOpacity = 1.0;
        if (!inside)
        {
            double falloff = Math.Max(0.0, 1.0 - Math.Sqrt(dx * dx + dy * dy) / AmbientReach);
            strokeOpacity = falloff * falloff * AmbientStrength;
        }

        SetOpacity(HoverFill, inside ? 1.0 : 0.0);
        SetOpacity(RevealStroke, strokeOpacity);

        var origin = new Point(
            Math.Clamp(px / w, 0.0, 1.0),
            Math.Clamp(py / h, 0.0, 1.0));

        _revealBrush.Center = origin;
        _revealBrush.GradientOrigin = origin;
    }

    public void ResetReveal()
    {
        SetOpacity(HoverFill, 0.0);
        SetOpacity(RevealStroke, 0.0);
    }

    private void BuildRevealBrush()
    {
        bool dark = ActualTheme == ElementTheme.Dark;

        Color Shade(byte alpha) => dark
            ? Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(alpha, 0x00, 0x00, 0x00);

        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.62,
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = Shade(0x58) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.30, Color = Shade(0x32) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.65, Color = Shade(0x14) });
        brush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = Shade(0x00) });

        _revealBrush = brush;
        RevealStroke.BorderBrush = brush;
    }

    private static void SetOpacity(UIElement element, double value)
    {
        if (Math.Abs(element.Opacity - value) > 0.02)
            element.Opacity = value;
    }

    // ==================================================================
    //  点击跳转
    // ==================================================================

    private void Root_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(NavTag) || IsFromInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        MainWindow.Instance?.NavigateTo(NavTag);
    }

    private static bool IsFromInteractiveControl(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ButtonBase or ToggleSwitch or Selector)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}

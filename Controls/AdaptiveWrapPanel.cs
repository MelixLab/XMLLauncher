using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LinxLauncher.Controls;

/// <summary>适用于少量操作按钮和预览卡片；按实际可用宽度换行。</summary>
/// <remarks>复用于外观页材质预览和主题色色点、常规页导入操作、账户页登录后的操作按钮。</remarks>
public sealed partial class AdaptiveWrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(8d, (sender, _) => ((AdaptiveWrapPanel)sender).InvalidateMeasure()));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, height = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += height + Spacing;
                x = height = 0;
            }
            width = Math.Max(width, x + size.Width);
            x += size.Width + Spacing;
            height = Math.Max(height, size.Height);
        }
        return new Size(width, y + height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += height + Spacing;
                x = height = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing;
            height = Math.Max(height, size.Height);
        }
        return finalSize;
    }
}

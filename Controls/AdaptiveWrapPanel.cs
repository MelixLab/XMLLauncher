using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LinxLauncher.Controls;

/// <summary>适用于少量操作按钮和预览卡片；按实际可用宽度换行。</summary>
/// <remarks>默认按内容换行；设置 ItemMinWidth 后用于首页、联机和工具箱的等宽卡片。</remarks>
public sealed partial class AdaptiveWrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(8d, (sender, _) => ((AdaptiveWrapPanel)sender).InvalidateMeasure()));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static readonly DependencyProperty ItemMinWidthProperty = DependencyProperty.Register(
        nameof(ItemMinWidth), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(0d, (sender, _) => ((AdaptiveWrapPanel)sender).InvalidateMeasure()));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(3, (sender, _) => ((AdaptiveWrapPanel)sender).InvalidateMeasure()));

    public double ItemMinWidth { get => (double)GetValue(ItemMinWidthProperty); set => SetValue(ItemMinWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    public static readonly DependencyProperty ContentPaddingProperty = DependencyProperty.Register(
        nameof(ContentPadding), typeof(Thickness), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(new Thickness(0), (sender, _) => ((AdaptiveWrapPanel)sender).InvalidateMeasure()));

    /// <summary>为交互卡片缩放留出空间；普通按钮行保持默认无内边距。</summary>
    public Thickness ContentPadding { get => (Thickness)GetValue(ContentPaddingProperty); set => SetValue(ContentPaddingProperty, value); }

    private double Gap => double.IsFinite(Spacing) ? Math.Max(0, Spacing) : 0;

    private (int Columns, double Width) GetColumns(double width)
    {
        int limit = Math.Max(1, MaxColumns);
        double minimum = Math.Max(1, ItemMinWidth);
        if (!double.IsFinite(width)) return (limit, minimum);
        width = Math.Max(0, width);
        int columns = (int)Math.Clamp(Math.Floor((width + Gap) / (minimum + Gap)), 1, limit);
        return (columns, Math.Max(0, (width - Gap * (columns - 1)) / columns));
    }

    private Size LayoutCards(double width, bool arrange)
    {
        var padding = ContentPadding;
        var (columns, itemWidth) = GetColumns(Math.Max(0, width - padding.Left - padding.Right));
        var visible = Children.Where(child => child.Visibility != Visibility.Collapsed).ToList();
        double y = padding.Top;
        for (int start = 0; start < visible.Count; start += columns)
        {
            int count = Math.Min(columns, visible.Count - start);
            double height = 0;
            for (int column = 0; column < count; column++)
            {
                var child = visible[start + column];
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                height = Math.Max(height, child.DesiredSize.Height);
            }
            if (arrange)
                for (int column = 0; column < count; column++)
                    visible[start + column].Arrange(new Rect(padding.Left + column * (itemWidth + Gap), y, itemWidth, height));
            y += height;
            if (start + count < visible.Count) y += Gap;
        }
        return new Size(visible.Count == 0 ? 0 : columns * itemWidth + (columns - 1) * Gap + padding.Left + padding.Right, y + padding.Bottom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ItemMinWidth > 0) return LayoutCards(availableSize.Width, false);
        double x = 0, y = 0, height = 0, width = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += height + Gap;
                x = height = 0;
            }
            width = Math.Max(width, x + size.Width);
            x += size.Width + Gap;
            height = Math.Max(height, size.Height);
        }
        return new Size(width, y + height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (ItemMinWidth > 0)
        {
            LayoutCards(finalSize.Width, true);
            return finalSize;
        }
        var visible = Children.Where(child => child.Visibility != Visibility.Collapsed).ToList();
        double x = 0, y = 0, height = 0;
        int rowStart = 0;

        void ArrangeRow(int end)
        {
            double rowX = 0;
            for (int i = rowStart; i < end; i++)
            {
                var child = visible[i];
                var size = child.DesiredSize;
                // 每个控件取得整行高度，VerticalAlignment 才能相对按钮等较高控件生效。
                child.Arrange(new Rect(rowX, y, Math.Min(size.Width, finalSize.Width), height));
                rowX += size.Width + Gap;
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var child = visible[i];
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                ArrangeRow(i);
                y += height + Gap;
                x = height = 0;
                rowStart = i;
            }
            x += size.Width + Gap;
            height = Math.Max(height, size.Height);
        }
        ArrangeRow(visible.Count);
        return finalSize;
    }
}

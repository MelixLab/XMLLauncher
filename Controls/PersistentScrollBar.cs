using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LinxLauncher.Controls;

/// <summary>
/// Native scrollbar for lists whose available content must remain discoverable.
/// Shared by the biome/structure lists, without replacing either control template
/// or changing the user's system-wide automatic scrollbar preference.
/// </summary>
public sealed partial class PersistentScrollBar : UserControl
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(ListView), typeof(PersistentScrollBar), new PropertyMetadata(null, TargetChanged));
    private ScrollViewer? _viewer;
    private bool _updating;
    private readonly ScrollBar _bar = new()
    {
        Orientation = Orientation.Vertical,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator
    };

    public ListView? Target
    {
        get => (ListView?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public PersistentScrollBar()
    {
        Width = 14;
        Content = _bar;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (!IsLoaded) Detach(); };
        _bar.Scroll += Scroll_Changed;
    }

    private static void TargetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var bar = (PersistentScrollBar)sender;
        bar.Detach();
        if (bar.IsLoaded) bar.Attach();
    }

    private void Attach()
    {
        if (_viewer is not null || Target is null) return;
        Target.ApplyTemplate();
        _viewer = FindViewer(Target);
        if (_viewer is null) return;
        _viewer.ViewChanged += Viewer_Changed;
        _viewer.SizeChanged += Viewer_SizeChanged;
        _viewer.LayoutUpdated += Viewer_LayoutUpdated;
        Update();
    }

    private static ScrollViewer? FindViewer(DependencyObject node)
    {
        if (node is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindViewer(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private void Detach()
    {
        if (_viewer is null) return;
        _viewer.ViewChanged -= Viewer_Changed;
        _viewer.SizeChanged -= Viewer_SizeChanged;
        _viewer.LayoutUpdated -= Viewer_LayoutUpdated;
        _viewer = null;
    }

    private void Viewer_Changed(object? sender, ScrollViewerViewChangedEventArgs args) => Update();
    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs args) => Update();
    private void Viewer_LayoutUpdated(object? sender, object args) => Update();
    private void Update()
    {
        if (_viewer is null || _updating) return;
        _updating = true;
        try
        {
            _bar.Maximum = _viewer.ScrollableHeight;
            _bar.ViewportSize = _viewer.ViewportHeight;
            _bar.LargeChange = Math.Max(1, _viewer.ViewportHeight * .9);
            _bar.SmallChange = 40;
            _bar.Value = _viewer.VerticalOffset;
            _bar.IsEnabled = _bar.Maximum > 0;
        }
        finally { _updating = false; }
    }
    private void Scroll_Changed(object sender, ScrollEventArgs args)
    {
        if (!_updating) _viewer?.ChangeView(null, args.NewValue, null, true);
    }
}

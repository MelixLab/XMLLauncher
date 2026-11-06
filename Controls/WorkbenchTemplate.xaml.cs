using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;
using Windows.Foundation;

namespace LinxLauncher.Controls;

/// <summary>
/// Shared workbench shell for a header, CommandBar, interactive viewport and right tool pane.
/// SeedMapWorkbench and ProjectionPage provide only their feature-specific content.
/// </summary>
public sealed partial class WorkbenchTemplate : UserControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(WorkbenchTemplate), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandContentProperty = DependencyProperty.Register(
        nameof(CommandContent), typeof(object), typeof(WorkbenchTemplate), new PropertyMetadata(null));

    public static readonly DependencyProperty PaneContentProperty = DependencyProperty.Register(
        nameof(PaneContent), typeof(object), typeof(WorkbenchTemplate), new PropertyMetadata(null));

    public static readonly DependencyProperty ViewportContentProperty = DependencyProperty.Register(
        nameof(ViewportContent), typeof(object), typeof(WorkbenchTemplate), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent), typeof(object), typeof(WorkbenchTemplate), new PropertyMetadata(null));

    public static readonly DependencyProperty IsImmersiveProperty = DependencyProperty.Register(
        nameof(IsImmersive), typeof(bool), typeof(WorkbenchTemplate), new PropertyMetadata(false, OnImmersiveChanged));

    public static readonly DependencyProperty InlinePaneLengthProperty = DependencyProperty.Register(
        nameof(InlinePaneLength), typeof(double), typeof(WorkbenchTemplate),
        new PropertyMetadata(380d, OnPaneLengthChanged));

    public static readonly DependencyProperty OverlayPaneLengthProperty = DependencyProperty.Register(
        nameof(OverlayPaneLength), typeof(double), typeof(WorkbenchTemplate),
        new PropertyMetadata(320d, OnPaneLengthChanged));

    private const double PaneBreakpoint = 900;
    private bool _layoutInitialized;
    private bool _themeSubscribed;
    private SplitViewDisplayMode _layoutMode = SplitViewDisplayMode.Overlay;

    public WorkbenchTemplate()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Adapt();
        Loaded += WorkbenchTemplate_Loaded;
        Unloaded += WorkbenchTemplate_Unloaded;
        CommandPresenter.Loaded += (_, _) => ApplyCommandBarDefaults();
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? CommandContent
    {
        get => GetValue(CommandContentProperty);
        set => SetValue(CommandContentProperty, value);
    }

    public object? PaneContent
    {
        get => GetValue(PaneContentProperty);
        set => SetValue(PaneContentProperty, value);
    }

    public object? ViewportContent
    {
        get => GetValue(ViewportContentProperty);
        set => SetValue(ViewportContentProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public bool IsImmersive
    {
        get => (bool)GetValue(IsImmersiveProperty);
        set => SetValue(IsImmersiveProperty, value);
    }

    public double InlinePaneLength
    {
        get => (double)GetValue(InlinePaneLengthProperty);
        set => SetValue(InlinePaneLengthProperty, value);
    }

    public double OverlayPaneLength
    {
        get => (double)GetValue(OverlayPaneLengthProperty);
        set => SetValue(OverlayPaneLengthProperty, value);
    }

    public Grid LayoutRoot => RootGrid;
    public Border CommandSurface => CommandSurfaceHost;
    public SplitView Workspace => WorkspaceSplitView;
    public Border ViewportBorder => ViewportSurface;
    public Grid PaneContainer => PaneHost;
    public NavigationView PaneNavigationView => PaneNavigation;
    public Frame PaneFrame => PaneContentFrame;
    public IList<object> PaneMenuItems => PaneNavigation.MenuItems;
    public bool IsOverlay => WorkspaceSplitView.DisplayMode == SplitViewDisplayMode.Overlay;

    public event TypedEventHandler<NavigationView, NavigationViewSelectionChangedEventArgs>? PaneSelectionChanged;

    public void Adapt()
    {
        if (ActualWidth <= 0)
            return;

        var nextMode = IsImmersive || ActualWidth < PaneBreakpoint
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
        var modeChanged = !_layoutInitialized || nextMode != _layoutMode;
        _layoutMode = nextMode;
        WorkspaceSplitView.DisplayMode = nextMode;
        double requestedPaneLength = nextMode == SplitViewDisplayMode.Overlay
            ? OverlayPaneLength
            : InlinePaneLength;
        WorkspaceSplitView.OpenPaneLength = Math.Min(
            Math.Max(0, requestedPaneLength),
            Math.Max(0, ActualWidth - 32));
        // SplitView 会裁切 PaneRoot；让内容从测量阶段就使用它实际分配的宽度。
        PaneHost.Width = WorkspaceSplitView.OpenPaneLength;

        if (!_layoutInitialized || modeChanged)
            WorkspaceSplitView.IsPaneOpen = nextMode == SplitViewDisplayMode.Inline;

        _layoutInitialized = true;
        ApplyImmersiveLayout();
        ApplyViewportMargin();
        ApplyCommandBarDefaults();
    }

    public void ApplyChromeMaterial()
    {
        CommandSurfaceHost.Background = IsImmersive ? CreateChromeBrush() : null;
        CommandSurfaceHost.BorderBrush = IsImmersive
            ? ThemeService.Brush("CardStrokeColorDefaultBrush")
            : null;
    }

    internal static Brush CreateChromeBrush()
    {
        var background = ThemeService.Brush("WindowBackgroundBrush");
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return background;
        var color = background is SolidColorBrush solid
            ? solid.Color
            : ThemeService.IsDark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        return new AcrylicBrush
        {
            TintColor = color,
            TintOpacity = .25,
            TintLuminosityOpacity = .65,
            FallbackColor = color
        };
    }

    private void ApplyImmersiveLayout()
    {
        if (IsImmersive)
        {
            HeaderRow.Height = new GridLength(0);
            CommandRow.Height = new GridLength(0);
            FooterRow.Height = new GridLength(0);
            WorkspaceRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(WorkspaceSplitView, 2);
            Grid.SetRow(CommandSurfaceHost, 2);
            CommandSurfaceHost.HorizontalAlignment = HorizontalAlignment.Right;
            CommandSurfaceHost.VerticalAlignment = VerticalAlignment.Top;
            CommandSurfaceHost.Margin = new Thickness(12, 56, 12, 0);
            CommandSurfaceHost.CornerRadius = new CornerRadius(8);
            CommandSurfaceHost.BorderThickness = new Thickness(1);
            PaneHost.Padding = new Thickness(12, 112, 12, 48);
            ViewportSurface.Margin = new Thickness(0);
            ViewportSurface.CornerRadius = new CornerRadius(0);
            ViewportSurface.BorderThickness = new Thickness(0);
            FindDescendant<CommandBar>(CommandPresenter)?.SetValue(
                HorizontalAlignmentProperty, HorizontalAlignment.Right);
            Canvas.SetZIndex(CommandSurfaceHost, 2);
            RootGrid.RowSpacing = 0;
        }
        else
        {
            HeaderRow.Height = new GridLength(1, GridUnitType.Auto);
            CommandRow.Height = new GridLength(1, GridUnitType.Auto);
            FooterRow.Height = new GridLength(1, GridUnitType.Auto);
            WorkspaceRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(WorkspaceSplitView, 2);
            Grid.SetRow(CommandSurfaceHost, 1);
            CommandSurfaceHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            CommandSurfaceHost.VerticalAlignment = VerticalAlignment.Stretch;
            CommandSurfaceHost.Margin = new Thickness(0);
            CommandSurfaceHost.CornerRadius = new CornerRadius(0);
            CommandSurfaceHost.BorderThickness = new Thickness(0);
            PaneHost.Padding = new Thickness(12, 0, 12, 8);
            ViewportSurface.Margin = new Thickness(0, 0, 8, 0);
            ViewportSurface.CornerRadius = new CornerRadius(8);
            ViewportSurface.BorderThickness = new Thickness(1);
            FindDescendant<CommandBar>(CommandPresenter)?.SetValue(
                HorizontalAlignmentProperty, HorizontalAlignment.Left);
            Canvas.SetZIndex(CommandSurfaceHost, 0);
            RootGrid.RowSpacing = 8;
        }
        ApplyChromeMaterial();
    }

    private void ApplyCommandBarDefaults()
    {
        var commandBar = FindDescendant<CommandBar>(CommandPresenter);
        if (commandBar is null)
            return;

        if (commandBar.Style is null && Application.Current.Resources.TryGetValue(
            "WorkbenchCommandBarStyle", out var resource) && resource is Style style)
            commandBar.Style = style;

        commandBar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
    }

    private void ApplyViewportMargin()
    {
        ViewportSurface.Margin = IsImmersive || WorkspaceSplitView.DisplayMode == SplitViewDisplayMode.Overlay
            ? new Thickness(0)
            : new Thickness(0, 0, 8, 0);
    }

    private void WorkbenchTemplate_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_themeSubscribed)
        {
            ThemeService.AppearanceChanged += ThemeService_AppearanceChanged;
            _themeSubscribed = true;
        }
        Adapt();
    }

    private void WorkbenchTemplate_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && _themeSubscribed)
        {
            ThemeService.AppearanceChanged -= ThemeService_AppearanceChanged;
            _themeSubscribed = false;
        }
    }

    private void ThemeService_AppearanceChanged()
    {
        RequestedTheme = ThemeService.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyChromeMaterial();
    }

    private static void OnImmersiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var workbench = (WorkbenchTemplate)sender;
        workbench.Adapt();
    }

    private static void OnPaneLengthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WorkbenchTemplate workbench && workbench.IsLoaded)
            workbench.Adapt();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            return match;
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } child)
                return child;
        return null;
    }

    private void PaneNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args) =>
        PaneSelectionChanged?.Invoke(sender, args);
}

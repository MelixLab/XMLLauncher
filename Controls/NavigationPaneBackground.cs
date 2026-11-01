using System;
using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinRT;

namespace LinxLauncher.Controls;

/// <summary>展开背景随原生面板一起裁剪、滑动，收起的图标栏始终透出窗口材质。</summary>
internal sealed class NavigationPaneBackground
{
    private readonly NavigationView _navigation;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly BrushTransition _themeTransition = new()
    {
        Duration = TimeSpan.FromMilliseconds(250),
    };
    private Grid? _paneRoot;
    private Border? _background;
    private string? _missingElement;

    private NavigationPaneBackground(NavigationView navigation)
    {
        _navigation = navigation;
        navigation.Loaded += (_, _) => ConnectTheme();
        navigation.Unloaded += (_, _) => ThemeService.AppearanceChanged -= AppearanceChanged;
        navigation.PaneOpening += (_, _) => SetOpen(true);
        navigation.PaneClosing += (_, _) => SetOpen(false);
        navigation.PaneClosed += (_, _) => UpdateBackground(false);
        if (navigation.IsLoaded) ConnectTheme();
    }

    public static void Attach(NavigationView navigation) => _ = new NavigationPaneBackground(navigation);

    private void ConnectTheme()
    {
        EnsureBackground();
        UpdateBackground(false);
        ThemeService.AppearanceChanged -= AppearanceChanged;
        ThemeService.AppearanceChanged += AppearanceChanged;
    }

    private void AppearanceChanged() => UpdateBackground(true);

    private void UpdateBackground(bool animate)
    {
        if (_background is null) return;
        if (ThemeService.Brush("NavigationPaneBackgroundBrush") is not SolidColorBrush source) return;

        bool shouldAnimate = animate && _navigation.IsLoaded && _navigation.IsPaneOpen
            && _background.Opacity > 0
            && _uiSettings.AnimationsEnabled && !_accessibilitySettings.HighContrast;
        _background.BackgroundTransition = shouldAnimate ? _themeTransition : null;
        if (shouldAnimate && _background.Background is SolidColorBrush current && current.Color == source.Color) return;

        // 共享画笔会直接改色；使用独立快照并替换 Background，才能触发原生颜色过渡。
        // 收起时即使颜色相同也重新赋值，清理 WinUI 留到首次渲染才启动的旧过渡。
        _background.Background = new SolidColorBrush(source.Color);
    }

    private void EnsureBackground()
    {
        _navigation.ApplyTemplate();
        // AOT 下模板节点可能只有基类包装；按名称查找后显式取得所需的 WinRT 投影。
        var splitView = FindElement(_navigation, "RootSplitView")?.As<SplitView>();
        if (splitView is null)
        {
            ReportMissingElement("RootSplitView");
            return;
        }
        splitView.ApplyTemplate();

        var paneRoot = FindElement(splitView, "PaneRoot")?.As<Grid>();
        if (paneRoot is null)
        {
            ReportMissingElement("PaneRoot");
            return;
        }
        _missingElement = null;
        if (paneRoot == _paneRoot) return;

        _paneRoot = paneRoot;
        _background = new Border
        {
            CornerRadius = paneRoot.CornerRadius,
            IsHitTestVisible = false,
            Opacity = _navigation.IsPaneOpen ? 1 : 0,
            OpacityTransition = new ScalarTransition(),
        };
        UpdateBackground(false);

        // PaneRoot 自身的裁剪和位移动画会带着背景走；不要放进会突然变窄的菜单布局网格。
        paneRoot.Children.Insert(0, _background);
    }

    private void SetOpen(bool open)
    {
        EnsureBackground();
        if (_background is null) return;

        // 使用隐式透明度过渡：反向操作会从当前动画值继续，不停表、不换画笔。
        _background.OpacityTransition.Duration = TimeSpan.FromMilliseconds(open ? 200 : 100);
        _background.Opacity = open ? 1 : 0;
    }

    private void ReportMissingElement(string name)
    {
        if (_missingElement == name) return;
        _missingElement = name;
        Log.Warn("导航栏展开背景未就绪：找不到模板节点 " + name + "，将在面板切换时重试。");
    }

    private static FrameworkElement? FindElement(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindElement(child, name) is { } descendant) return descendant;
        }

        return null;
    }
}

using System;
using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LinxLauncher.Controls;

/// <summary>展开背景随原生面板一起裁剪、滑动，收起的图标栏始终透出窗口材质。</summary>
internal sealed class NavigationPaneBackground
{
    private readonly NavigationView _navigation;
    private Grid? _paneRoot;
    private Border? _background;

    private NavigationPaneBackground(NavigationView navigation)
    {
        _navigation = navigation;
        navigation.Loaded += (_, _) => EnsureBackground();
        navigation.PaneOpening += (_, _) => SetOpen(true);
        navigation.PaneClosing += (_, _) => SetOpen(false);
    }

    public static void Attach(NavigationView navigation) => _ = new NavigationPaneBackground(navigation);

    private void EnsureBackground()
    {
        _navigation.ApplyTemplate();
        var splitView = FindElement<SplitView>(_navigation, "RootSplitView");
        if (splitView is null) return;
        splitView.ApplyTemplate();

        var paneRoot = FindElement<Grid>(splitView, "PaneRoot");
        if (paneRoot is null || paneRoot == _paneRoot) return;

        _paneRoot = paneRoot;
        _background = new Border
        {
            Background = ThemeService.Brush("NavigationPaneBackgroundBrush"),
            CornerRadius = paneRoot.CornerRadius,
            IsHitTestVisible = false,
            Opacity = _navigation.IsPaneOpen ? 1 : 0,
            OpacityTransition = new ScalarTransition(),
        };

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

    private static T? FindElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name) return element;
            if (FindElement<T>(child, name) is { } descendant) return descendant;
        }

        return null;
    }
}

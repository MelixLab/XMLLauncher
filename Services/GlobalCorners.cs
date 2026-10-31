using Microsoft.UI.Xaml;

namespace LinxLauncher.Services;

/// <summary>仅覆盖 WinUI 原生边角资源；不遍历或改写控件模板中的元素与绑定。</summary>
public static class GlobalCorners
{
    private static ResourceDictionary? _squareResources;
    private static bool? _square;

    // 这些键由当前项目引用的 WinUI generic.xaml 定义，值均为 CornerRadius。
    private static readonly string[] RadiusKeys =
    [
        "ControlCornerRadius", "OverlayCornerRadius",
        "ComboBoxDropDownButtonBackgroundCornerRadius", "ComboBoxHiglightBorderCornerRadius",
        "ComboBoxItemCornerRadius", "ComboBoxItemPillCornerRadius",
        "ListViewItemCornerRadius", "GridViewItemCornerRadius",
        "ListViewItemCheckBoxCornerRadius", "GridViewItemCheckBoxCornerRadius",
        "ListViewItemSelectionIndicatorCornerRadius",
        "InfoBarActionButtonCornerRadius", "SliderTrackCornerRadius", "SliderThumbCornerRadius",
        "ScrollBarCornerRadius", "ProgressBarCornerRadius", "ColorPickerSliderCornerRadius",
    ];

    /// <returns>原生资源是否变化；由主题引擎统一刷新现有控件的 ThemeResource。</returns>
    public static bool ApplyResources(FrameworkElement? navigation)
    {
        bool square = AppState.Settings.GlobalCardCornersEnabled;
        if (_square == square) return false;
        _square = square;
        if (_squareResources is not null)
            Application.Current.Resources.MergedDictionaries.Remove(_squareResources);
        _squareResources = null;
        if (square)
        {
            _squareResources = new ResourceDictionary();
            foreach (string key in RadiusKeys) _squareResources[key] = new CornerRadius(0);
            Application.Current.Resources.MergedDictionaries.Add(_squareResources);
        }

        // 主窗口自行声明了导航内容区的圆角，单独更新这一个已知的本地资源。
        if (navigation is not null)
            foreach (var value in navigation.Resources.ThemeDictionaries.Values)
                if (value is ResourceDictionary dictionary)
                    dictionary["NavigationViewContentGridCornerRadius"] = square
                        ? new CornerRadius(0) : new CornerRadius(8, 0, 0, 0);
        return true;
    }
}

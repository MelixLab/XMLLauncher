using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly Type[] CategoryPages =
        [typeof(SettingsGeneralPage), typeof(SettingsAppearancePage), typeof(SettingsAccountPage), typeof(SettingsAdvancedPage)];
    private int _selectedIndex = -1;

    public SettingsPage()
    {
        InitializeComponent();
        CategoryNavigation.SelectedItem = CategoryNavigation.MenuItems[0];
        Loaded += (_, _) => FontService.RefreshAll();
    }

    private void Category_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item ||
            !int.TryParse(item.Tag?.ToString(), out int index) || index == _selectedIndex) return;

        NavigationTransitionInfo transition = _selectedIndex < 0 || !new UISettings().AnimationsEnabled
            ? new SuppressNavigationTransitionInfo()
            : new SlideNavigationTransitionInfo
            {
                Effect = index > _selectedIndex
                    ? SlideNavigationTransitionEffect.FromRight
                    : SlideNavigationTransitionEffect.FromLeft
            };
        // 分类页面直接交给 Frame 导航，使用原生页面缓存保留输入和滚动位置。
        if (CategoryFrame.Navigate(CategoryPages[index], null, transition))
        {
            _selectedIndex = index;
            FontService.RefreshAll();
        }
    }

    private void PageLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageLayout.Padding = e.NewSize.Width < 640 ? new Thickness(16, 20, 16, 12) : new Thickness(32, 28, 32, 16);
    }

}

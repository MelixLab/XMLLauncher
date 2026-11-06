using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
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
        CategoryFrame.ContentTransitions = new TransitionCollection();
        Loaded += (_, _) => FontService.RefreshAll();
        Unloaded += (_, _) => CategoryFrame.ContentTransitions.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _selectedIndex = -1;
        CategoryNavigation.SelectedItem = CategoryNavigation.MenuItems[0];
        // 已选中常规时不会触发 SelectionChanged，仍需清除上一轮子页转场。
        if (_selectedIndex < 0) NavigateCategory(0);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CategoryFrame.ContentTransitions.Clear();
        base.OnNavigatedFrom(e);
    }

    private void Category_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item ||
            !int.TryParse(item.Tag?.ToString(), out int index) || index == _selectedIndex) return;

        NavigateCategory(index);
    }

    private void NavigateCategory(int index)
    {
        bool animate = _selectedIndex >= 0 && new UISettings().AnimationsEnabled;
        CategoryFrame.ContentTransitions.Clear();
        if (animate) CategoryFrame.ContentTransitions.Add(new NavigationThemeTransition());
        NavigationTransitionInfo transition = !animate
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

            // 页面刚导航完，子控件还没生成，这时候扫是空的。
            // 等到下一帧再扫一次，外观/账户/高级页里的文字才会换成 Noto。
            FontService.RefreshAll();
            DispatcherQueue.TryEnqueue(() => FontService.RefreshAll());
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FontService.RefreshAll());
        }
    }

    private void PageLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double side = e.NewSize.Width < 640 ? 16 : 32;
        PageLayout.Padding = new Thickness(side, e.NewSize.Width < 640 ? 20 : 28, side, 0);
    }

}

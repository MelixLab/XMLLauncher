using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

/// <summary>让版本详情与联机大厅的现有视图复用原生 Frame 页面转场，保留页面本身的事件与数据。</summary>
public sealed partial class TransitionPage : Page
{
    public TransitionPage()
    {
        InitializeComponent();
        // 父页面可能被缓存后重新载入，不能在 Unloaded 清空视图；再次导航时再解除旧宿主。
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FrameworkElement view) return;
        // 快速反向导航时，旧 Page 可能尚未卸载。
        if (view.Parent is ContentPresenter presenter) presenter.Content = null;
        view.Visibility = Visibility.Visible;
        ViewHost.Content = view;
    }
}

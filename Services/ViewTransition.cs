using LinxLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Services;

/// <summary>
/// 版本列表/详情、联机入口/大厅共用原生 Frame 导航。
/// 动画由 NavigationThemeTransition 与 SlideNavigationTransitionInfo 执行。
/// </summary>
public sealed class ViewTransition
{
    private readonly FrameworkElement _first;
    private readonly FrameworkElement[] _views;
    private readonly Frame _frame = new();
    private readonly UISettings _settings = new();
    private FrameworkElement _current;
    private int _generation;

    public ViewTransition(FrameworkElement owner, Panel parent, FrameworkElement first, params FrameworkElement[] children)
    {
        _first = first;
        _views = [first, .. children];
        _current = first;
        // InitializeComponent 后、首次布局前 Parent 可能仍为 null，检查已建立的 Children 集合。
        if (_views.Any(view => !parent.Children.Contains(view)))
            throw new ArgumentException("转场视图必须属于同一个容器。");
        // 标题留在父容器的固定行，Frame 只替换下面的内容行。
        Grid.SetRow(_frame, Grid.GetRow(first));
        foreach (var view in _views) parent.Children.Remove(view);
        parent.Children.Add(_frame);
        _frame.ContentTransitions = new TransitionCollection { new NavigationThemeTransition() };
        Reset(first);
        owner.Unloaded += (_, _) => Stop();
    }

    public void Show(FrameworkElement target, bool forward, Action focus)
    {
        Validate(target);
        if (_current != target)
        {
            bool goBack = !forward && target == _first && _frame.CanGoBack;
            NavigationTransitionInfo transition = _settings.AnimationsEnabled
                ? new SlideNavigationTransitionInfo
                {
                    // GoBack 会自动反向播放；再次指定 FromLeft 会把返回方向反转两次。
                    Effect = forward || goBack ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft
                }
                : new SuppressNavigationTransitionInfo();
            bool navigated;
            if (goBack)
            {
                _frame.GoBack(transition);
                navigated = true;
            }
            else navigated = _frame.Navigate(typeof(TransitionPage), target, transition);
            if (navigated) _current = target;
        }
        int generation = ++_generation;
        target.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_generation == generation && target.IsLoaded) focus();
        });
    }

    public void Reset(FrameworkElement target)
    {
        Validate(target);
        Stop();
        _frame.Navigate(typeof(TransitionPage), target, new SuppressNavigationTransitionInfo());
        _frame.BackStack.Clear();
        _frame.ForwardStack.Clear();
        _current = target;
    }

    // 页面离开时取消排队的焦点恢复；原生 Frame 自己管理动画生命周期。
    public void Stop() => ++_generation;

    public bool IsShowing(FrameworkElement target) => _current == target;

    private void Validate(FrameworkElement target)
    {
        if (!_views.Contains(target)) throw new ArgumentException("视图不属于此导航。", nameof(target));
    }
}

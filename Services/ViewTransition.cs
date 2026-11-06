using LinxLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Services;

/// <summary>
/// 版本列表/详情、联机入口/大厅、账户/皮肤共用原生 Frame 导航。
/// 动画由 NavigationThemeTransition 与 SlideNavigationTransitionInfo 执行。
/// </summary>
public sealed class ViewTransition
{
    private readonly FrameworkElement[] _views;
    private readonly Frame _frame;
    private readonly UISettings _settings = new();
    private FrameworkElement _current;
    private int _generation;

    public ViewTransition(FrameworkElement owner, Panel parent, FrameworkElement first, params FrameworkElement[] children)
        : this(owner, new Frame(), parent, true, first, children)
    {
    }

    public ViewTransition(FrameworkElement owner, Frame frame, Panel parent, FrameworkElement first, params FrameworkElement[] children)
        : this(owner, frame, parent, false, first, children)
    {
    }

    private ViewTransition(
        FrameworkElement owner,
        Frame frame,
        Panel parent,
        bool attachFrame,
        FrameworkElement first,
        params FrameworkElement[] children)
    {
        _views = [first, .. children];
        _current = first;
        _frame = frame;
        // InitializeComponent 后、首次布局前 Parent 可能仍为 null，检查已建立的 Children 集合。
        if (_views.Any(view => !parent.Children.Contains(view)))
            throw new ArgumentException("转场视图必须属于同一个容器。");
        // 标题留在父容器的固定行，Frame 只替换下面的内容行。
        _frame.HorizontalAlignment = HorizontalAlignment.Stretch;
        _frame.VerticalAlignment = VerticalAlignment.Stretch;
        _frame.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _frame.VerticalContentAlignment = VerticalAlignment.Stretch;
        foreach (var view in _views) parent.Children.Remove(view);
        if (attachFrame)
        {
            Grid.SetRow(_frame, Grid.GetRow(first));
            parent.Children.Add(_frame);
        }
        _frame.ContentTransitions = new TransitionCollection();
        Reset(first);
        owner.Unloaded += (_, _) => Stop();
    }

    public void Show(FrameworkElement target, bool forward, Action? focus = null)
    {
        Validate(target);
        if (_current != target)
        {
            if (_frame.ContentTransitions.Count == 0)
                _frame.ContentTransitions.Add(new NavigationThemeTransition());
            var backEntry = _frame.CanGoBack ? _frame.BackStack[_frame.BackStack.Count - 1] : null;
            var backTarget = backEntry?.Parameter as FrameworkElement;
            bool goBack = !forward && backEntry is not null && ReferenceEquals(backTarget, target);
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
        if (focus is null)
            return;

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

    public void Stop()
    {
        ++_generation;
        // 缓存页面重载也会触发原生 Load 转场，不能保留上一次子导航的横向动画。
        _frame.ContentTransitions.Clear();
    }

    public bool IsShowing(FrameworkElement target) => _current == target;

    private void Validate(FrameworkElement target)
    {
        if (!_views.Contains(target)) throw new ArgumentException("视图不属于此导航。", nameof(target));
    }
}

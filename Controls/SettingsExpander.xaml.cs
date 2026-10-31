using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Controls;

/// <summary>设置卡片的展开区域，复用于导入、Java、材质、全局边角和高级启动参数。</summary>
/// <remarks>共用高度/透明度动画；可由标题按钮展开，也可由标题内的开关或选择控件展开。</remarks>
[ContentProperty(Name = nameof(Details))]
public sealed partial class SettingsExpander : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(SettingsExpander), new PropertyMetadata(null, OnHeaderChanged));
    public static readonly DependencyProperty IsHeaderToggleVisibleProperty = DependencyProperty.Register(
        nameof(IsHeaderToggleVisible), typeof(bool), typeof(SettingsExpander), new PropertyMetadata(true, OnHeaderChanged));
    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(object), typeof(SettingsExpander), new PropertyMetadata(null));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SettingsExpander), new PropertyMetadata(false, OnExpandedChanged));

    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>关闭后直接呈现 Header，由其中的开关或选择控件设置 IsExpanded。</summary>
    public bool IsHeaderToggleVisible { get => (bool)GetValue(IsHeaderToggleVisibleProperty); set => SetValue(IsHeaderToggleVisibleProperty, value); }
    public object? Details { get => GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private Storyboard? _transition;

    public SettingsExpander()
    {

        InitializeComponent();
        UpdateHeader();

        // 必须在 InitializeComponent 之后：之前 HeaderToggle 还是 null。
        // ToggleButton 的 Content 会自己生成一个 TextBlock，
        // 它不跟 FontService 的遍历走，这里直接点名设字体。
        HeaderToggle.FontFamily = FontService.BodyFont;
        Loaded += (_, _) =>
        {
            AutomationProperties.SetName(HeaderToggle, AutomationProperties.GetName(this));
            FinishTransition();
        };
        Unloaded += (_, _) => FinishTransition();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SettingsExpanderAutomationPeer(this);

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((SettingsExpander)sender).UpdateHeader();

    private void UpdateHeader()
    {
        if (HeaderToggle is null || HeaderPresenter is null) return;
        // 先释放旧宿主；同一个 Header 只能有一个父元素。
        HeaderToggle.Content = null;
        HeaderPresenter.Content = null;
        HeaderToggle.Visibility = IsHeaderToggleVisible ? Visibility.Visible : Visibility.Collapsed;
        StaticHeader.Visibility = IsHeaderToggleVisible ? Visibility.Collapsed : Visibility.Visible;
        if (IsHeaderToggleVisible) HeaderToggle.Content = Header;
        else HeaderPresenter.Content = Header;
    }

    private static void OnExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (SettingsExpander)sender;
        card.HeaderToggle.IsChecked = card.IsExpanded;
        card.AnimateExpansion();
        if (FrameworkElementAutomationPeer.FromElement(card) is SettingsExpanderAutomationPeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                (bool)args.OldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                card.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    private void Header_Checked(object sender, RoutedEventArgs e)
    {
        IsExpanded = true;

        // 展开的内容是 ContentPresenter 现给的，等一帧再扫字体
        DispatcherQueue.TryEnqueue(() => FontService.Apply(this));
    }
    private void Header_Unchecked(object sender, RoutedEventArgs e) => IsExpanded = false;

    private void AnimateExpansion()
    {
        bool animate = IsLoaded && new UISettings().AnimationsEnabled;
        if (!animate)
        {
            FinishTransition();
            if (IsLoaded && IsExpanded) RevealExpandedContent();
            return;
        }

        double startHeight = Reveal.ActualHeight;
        double startOpacity = Reveal.Opacity;
        _transition?.Stop();
        Reveal.Visibility = Visibility.Visible;
        Reveal.IsHitTestVisible = IsExpanded;
        DetailBorder.MinHeight = 0;
        DetailBorder.Measure(new Size(Math.Max(Reveal.ActualWidth, ActualWidth), double.PositiveInfinity));
        double fullHeight = DetailBorder.DesiredSize.Height;
        DetailBorder.MinHeight = fullHeight;
        Reveal.Height = startHeight;
        Reveal.Opacity = startOpacity;

        var transition = new Storyboard();
        AddAnimation(transition, "Height", startHeight, IsExpanded ? fullHeight : 0);
        AddAnimation(transition, "Opacity", startOpacity, IsExpanded ? 1 : 0);
        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_transition, transition)) return;
            FinishTransition();
            if (IsExpanded) RevealExpandedContent();
        };
        _transition = transition;
        transition.Begin();
    }

    private void AddAnimation(Storyboard storyboard, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, Reveal);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void FinishTransition()
    {
        _transition?.Stop();
        _transition = null;
        DetailBorder.MinHeight = 0;
        Reveal.Height = IsExpanded ? double.NaN : 0;
        Reveal.Opacity = IsExpanded ? 1 : 0;
        Reveal.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        Reveal.IsHitTestVisible = IsExpanded;
    }

    private void Reveal_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Reveal.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        // 动画过程中改变窗口宽度时，以重新排版后的自然高度为准。
        if (e.PreviousSize.Width > 0 && e.PreviousSize.Width != e.NewSize.Width && _transition is not null)
        {
            FinishTransition();
            if (IsExpanded) RevealExpandedContent();
        }
    }

    private void RevealExpandedContent()
    {
        // 选择或开关触发的内嵌展开保持原滚动位置，避免操作控件移出视口。
        if (!IsHeaderToggleVisible) return;
        // 等自然高度和 ScrollViewer 的可滚动范围完成更新，避免展开前的范围截断滚动。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !IsExpanded) return;
            UpdateLayout();
            DependencyObject? parent = VisualTreeHelper.GetParent(this);
            while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
            if (parent is not ScrollViewer scroll) return;

            double top = TransformToVisual(scroll).TransformPoint(new Point()).Y;
            double overflow = top + ActualHeight - (scroll.ActualHeight - scroll.Padding.Bottom - 12);
            // 内容高于视口时保留标题，让用户仍能看清展开的是哪一项。
            double distance = Math.Min(overflow, Math.Max(0, top - scroll.Padding.Top - 12));
            if (distance <= 0) return;
            scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + distance), null,
                disableAnimation: !new UISettings().AnimationsEnabled);
        });
    }

    private sealed partial class SettingsExpanderAutomationPeer(SettingsExpander owner) : FrameworkElementAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Collapse() => owner.IsExpanded = false;
        public void Expand() => owner.IsExpanded = true;
        protected override string GetClassNameCore() => nameof(SettingsExpander);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);
    }
}

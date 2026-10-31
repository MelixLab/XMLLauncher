using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LinxLauncher.Controls;

/// <summary>版本详情、联机大厅与创建房间共用 Windows 设置式标题，交互由原生 BreadcrumbBar 提供。</summary>
public sealed partial class PageBreadcrumb : UserControl
{
    public static readonly DependencyProperty ParentTitleProperty = DependencyProperty.Register(
        nameof(ParentTitle), typeof(string), typeof(PageBreadcrumb), new PropertyMetadata(string.Empty, PathChanged));
    public static readonly DependencyProperty CurrentTitleProperty = DependencyProperty.Register(
        nameof(CurrentTitle), typeof(string), typeof(PageBreadcrumb), new PropertyMetadata(string.Empty, PathChanged));

    // BreadcrumbBar 只有 ItemsSource；复用 UiList 填充原生 ItemCollection，避免 AOT 封送托管集合。
    // 此 ItemsControl 只持有数据，不加入可视树；生命周期与面包屑一致。
    private readonly ItemsControl _path = new();

    public PageBreadcrumb()
    {
        InitializeComponent();
        Breadcrumbs.ItemsSource = _path.Items;
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => FontService.Apply(this));
    }

    public string ParentTitle
    {
        get => (string)GetValue(ParentTitleProperty);
        set => SetValue(ParentTitleProperty, value);
    }

    public string CurrentTitle
    {
        get => (string)GetValue(CurrentTitleProperty);
        set => SetValue(CurrentTitleProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? ParentClicked;

    // 不在局部 ThemeDictionary 中重定向文字画笔：它可能按系统主题解析，
    // 与用户在应用内强制选择的主题相反。稳定画笔由 ThemeService 随实际主题更新。
    private void ApplyLabel(TextBlock label)
    {
        bool parent = !string.IsNullOrWhiteSpace(ParentTitle) && label.DataContext as string == ParentTitle;
        label.Foreground = ThemeService.Brush(parent ? "SecondaryTextBrush" : "PrimaryTextBrush");
    }

    private void Label_Loaded(object sender, RoutedEventArgs args) => ApplyLabel((TextBlock)sender);
    private void Label_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => ApplyLabel((TextBlock)sender);
    private void Label_PointerExited(object sender, PointerRoutedEventArgs args) => ApplyLabel((TextBlock)sender);
    private void Label_PointerEntered(object sender, PointerRoutedEventArgs args) =>
        ((TextBlock)sender).Foreground = ThemeService.Brush("PrimaryTextBrush");

    private static void PathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PageBreadcrumb)sender).UpdatePath();

    private void UpdatePath()
    {
        UiList.Clear(_path);
        if (!string.IsNullOrWhiteSpace(ParentTitle)) UiList.Add(_path, ParentTitle);
        if (!string.IsNullOrWhiteSpace(CurrentTitle)) UiList.Add(_path, CurrentTitle);
    }

    private void Breadcrumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && _path.Items.Count > 1)
            ParentClicked?.Invoke(this, new RoutedEventArgs());
    }

    public void FocusParent()
    {
        if (FocusManager.FindFirstFocusableElement(Breadcrumbs) is Control parent)
            parent.Focus(FocusState.Programmatic);
    }
}

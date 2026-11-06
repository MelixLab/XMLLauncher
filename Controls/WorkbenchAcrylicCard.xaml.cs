using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace LinxLauncher.Controls;

[ContentProperty(Name = nameof(CardContent))]
public sealed partial class WorkbenchAcrylicCard : UserControl
{
    public static readonly DependencyProperty CardContentProperty = DependencyProperty.Register(
        nameof(CardContent), typeof(object), typeof(WorkbenchAcrylicCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CardPaddingProperty = DependencyProperty.Register(
        nameof(CardPadding), typeof(Thickness), typeof(WorkbenchAcrylicCard), new PropertyMetadata(new Thickness(0)));

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }

    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    public WorkbenchAcrylicCard()
    {
        InitializeComponent();
    }
}

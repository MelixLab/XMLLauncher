using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinxLauncher.Controls;

/// <summary>按设置行自身的可用宽度重排，兼容双列卡片与窄窗口。</summary>
/// <remarks>复用于常规的镜像/全屏、外观的模式/字体、账户的登录/注册/隐私及高级的工具操作。</remarks>
public sealed partial class SettingsRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsRow), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(object), typeof(SettingsRow), new PropertyMetadata(null, OnChanged));
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(SettingsRow), new PropertyMetadata("", OnChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }

    public SettingsRow() => InitializeComponent();

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)sender;
        row.TitleText.Text = row.Title;
        row.DescriptionText.Text = row.Description;
        row.DescriptionText.Visibility = string.IsNullOrEmpty(row.Description) ? Visibility.Collapsed : Visibility.Visible;
        row.LeadingIcon.Glyph = row.IconGlyph;
        row.LeadingIcon.Visibility = string.IsNullOrEmpty(row.IconGlyph) ? Visibility.Collapsed : Visibility.Visible;
        row.LabelHost.ColumnSpacing = string.IsNullOrEmpty(row.IconGlyph) ? 0 : 16;
        row.EditorHost.Content = row.Editor;
        if (row.Editor is ToggleSwitch toggle)
        {
            // 原生开关默认 MinWidth=154，内部轨道仍靠左；去掉预留空白后再把整个开关靠右。
            toggle.MinWidth = 0;
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            row.EditorHost.HorizontalContentAlignment = HorizontalAlignment.Right;
        }
        else
        {
            row.EditorHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 360;
        Grid.SetColumn(EditorHost, narrow ? 0 : 1);
        Grid.SetRow(EditorHost, narrow ? 1 : 0);
        Layout.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : GridLength.Auto;
        Layout.ColumnSpacing = narrow ? 0 : 20;
        Layout.RowSpacing = narrow ? 10 : 0;
        EditorHost.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }
}

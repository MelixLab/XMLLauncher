using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Services;

/// <summary>
/// 正文字体切换：字号 &lt; 20 的正文在 TextFontFamily / Noto Sans 之间切；
/// 页面/小节标题（字号 ≥ 20，用 DisplayFontFamily）始终不动。
/// </summary>
public static class FontService
{
    private static object? _originalControlFont;
    private static object? _originalTextFont;

    /// <summary>当前正文该用的字体（Noto 开关打开就是 Noto Sans SC）。</summary>
    public static FontFamily BodyFont
    {
        get
        {
            try
            {
                return AppState.Settings.UseNotoSans
                    ? new FontFamily("Noto Sans SC")
                    : (FontFamily)(_originalTextFont ?? new FontFamily("Segoe UI Variable Text"));
            }
            catch
            {
                return new FontFamily("Segoe UI Variable Text");
            }
        }
    }

    /// <summary>
    /// 把「控件默认字体」这个全局资源换掉。
    /// 只靠遍历已经画出来的控件是不够的：Toast、对话框、浮窗都是事后才创建的，
    /// 那时候早遍历完了，所以它们一直显示默认字体（用户看到的「没适配 Noto」就是这个）。
    /// </summary>
    public static void ApplyGlobal(bool noto)
    {
        try
        {
            var res = Application.Current.Resources;
            if (_originalControlFont is null && res.TryGetValue("ContentControlThemeFontFamily", out var original))
                _originalControlFont = original;

            res["ContentControlThemeFontFamily"] = noto
                ? new FontFamily("Noto Sans SC")
                : _originalControlFont ?? new FontFamily("Segoe UI Variable Text");

            // 样式都写的是 StaticResource TextFontFamily，所以把这个资源本身换掉，
            // 之后新建的页面（设置页、对话框、浮窗……）一出生就是 Noto。
            if (_originalTextFont is null && res.TryGetValue("TextFontFamily", out var textFont))
                _originalTextFont = textFont;

            res["TextFontFamily"] = noto
                ? new FontFamily("Noto Sans SC")
                : _originalTextFont ?? new FontFamily("Segoe UI Variable Text");
        }
        catch (Exception ex)
        {
            Log.Warn("设置全局字体失败：" + ex.Message);
        }
    }

    /// <summary>对话框内容是在 Opened 之后才有可视树，所以挂到 Opened 上再刷一次。</summary>
    public static void ApplyToDialog(ContentDialog dialog)
    {
        dialog.Opened += (_, _) => Apply(dialog.Content as DependencyObject);
    }

    public static void RefreshAll()
    {
        if (MainWindow.Instance?.Content is DependencyObject root)
        {
            Apply(root);
        }
    }

    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;

        bool noto = AppState.Settings.UseNotoSans;
        var body = (FontFamily)Application.Current.Resources[noto ? "NotoFontFamily" : "TextFontFamily"];

        Walk(root, body);
    }

    /// <summary>
    /// 用私有区字符（U+E000–U+F8FF）画出来的就是图标字形：
    /// 换成正文用的字体后，字形在字体里不存在，界面上会变成一个个方框。
    /// </summary>
    private static bool IsGlyph(TextBlock tb)
    {
        var text = tb.Text;
        if (string.IsNullOrEmpty(text) || text.Length > 2) return false;

        foreach (var ch in text)
        {
            if (ch < '\uE000' || ch > '\uF8FF') return false;
        }

        return true;
    }

    private static void Walk(DependencyObject node, FontFamily body)
    {
        // ★ FontIcon / SymbolIcon 内部就是一个画字形的 TextBlock：
        //   换成 Noto 之后字形在字体里不存在，界面上会变成一个个方框，
        //   所以遇到图标控件整棵子树都跳过。
        if (node is FontIcon or SymbolIcon)
        {
            return;
        }

        // 只碰正文；标题（≥ 20）保持 DisplayFontFamily
        if (node is TextBlock tb && tb.FontSize < 20 && tb.FontFamily != body && !IsGlyph(tb))
        {
            tb.FontFamily = body;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), body);
        }
    }
}

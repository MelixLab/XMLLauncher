using System.Collections;
using Microsoft.UI.Xaml.Controls;

namespace LinxLauncher.Services;

/// <summary>
/// 往列表控件里塞数据的统一入口。
///
/// 为什么不用 ItemsSource：
/// Native AOT 构建里不能把 List&lt;T&gt; / ObservableCollection&lt;T&gt; 直接赋给 ItemsSource。
/// CsWinRT 在 AOT 下不会为托管集合生成 IBindableIterable 的 CCW 适配，
/// WinUI 拿到这个对象直接判无理，抛 ArgumentException: Value does not fall within the
/// expected range（普通构建里看不出来，因为那边有运行时生成的适配器）。
/// 逐个元素塞进 Items 走的是另一条路：每一项单独投影成 IInspectable，AOT 下正常。
///
/// 代价是列表很长时略慢一点，对几百条的量级没有可感差异。
/// </summary>
public static class UiList
{
    /// <summary>清空后一次性填入（整表刷新用）。</summary>
    public static void Fill(ItemsControl target, IEnumerable? items)
    {
        target.Items.Clear();
        if (items is null) return;
        foreach (var item in items) target.Items.Add(item);
    }

    /// <summary>追加一条（分页加载用）。</summary>
    public static void Add(ItemsControl target, object? item)
    {
        if (item is null) return;
        target.Items.Add(item);
    }

    /// <summary>清空（等价于 ItemsSource = null）。</summary>
    public static void Clear(ItemsControl target) => target.Items.Clear();
}

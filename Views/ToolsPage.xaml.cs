using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LinxLauncher.Views;

/// <summary>
/// 工具箱：整体内存整理、模组汉化、种子地图、缓存清理。
/// </summary>
public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FontService.Apply(this);
            ToolInit();
            RefreshMem();
            RefreshTranslate();
            RefreshSeed();
            RefreshCache();
        };
    }

    private static void Toast(string msg) => MainWindow.Instance?.Toast(msg);

    private void ToolInit()
    {
        MemCard.Header = "内存整理";
        TranslateCard.Header = "模组汉化";
        SeedCard.Header = "种子地图";
        CacheCard.Header = "缓存清理";
    }

    /* ==================== 内存整理 ==================== */

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static (ulong availMb, int loadPct) GetMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return (0, 0);
        return (mem.ullAvailPhys / 1024 / 1024, (int)mem.dwMemoryLoad);
    }

    private void RefreshMem()
    {
        var (avail, load) = GetMemory();
        MemCard.Status = $"可用 {avail / 1024.0:F1} GB · 占用 {load}%\n"
                         + "把每个进程的物理内存压回页面文件，治「开着越用越卡」";
    }

    private void MemClean_Click(object sender, RoutedEventArgs e)
    {
        var (before, _) = GetMemory();
        var beforeMb = before;

        var done = 0;
        var failed = 0;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0 || p.Id == 4) continue;   // Idle / System
                if (EmptyWorkingSet(p.Handle) != 0) done++;
                else failed++;
            }
            catch
            {
                failed++;
            }
            finally
            {
                p.Dispose();
            }
        }

        var (after, load) = GetMemory();
        var delta = (long)after - (long)beforeMb;

        MemCard.Status = $"可用 {after / 1024.0:F1} GB · 占用 {load}%\n"
                         + $"上次整理：{done} 个进程成功，{failed} 个没权限";

        Toast(delta > 0
            ? $"已整理 {done} 个进程，可用内存 +{delta} MB"
            : $"已整理 {done} 个进程（有些进程需要管理员权限）");
    }

    /* ==================== 缓存清理 ==================== */

    private void RefreshCache()
    {
        CacheCard.Status = "清掉临时文件和旧日志备份，不动版本和存档";
    }

    private void CacheClean_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            long freed = 0;
            var removed = 0;

            var appData = Paths.AppData;
            foreach (var pattern in new[] { "*.tmp", "*.oldlog", "launcher.log.*" })
            {
                foreach (var f in Directory.GetFiles(appData, pattern))
                {
                    try
                    {
                        freed += new FileInfo(f).Length;
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
            }

            var temp = Path.Combine(Path.GetTempPath(), "LinxLauncher");
            if (Directory.Exists(temp))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
                    {
                        try { freed += new FileInfo(f).Length; } catch { }
                    }
                    Directory.Delete(temp, true);
                    removed++;
                }
                catch { }
            }

            CacheCard.Status = removed > 0
                ? $"清掉 {removed} 项，释放 {freed / 1024.0 / 1024.0:F1} MB"
                : "没有可清理的临时文件";
            Toast(removed > 0 ? $"已清理，释放 {freed / 1024.0 / 1024.0:F1} MB" : "很干净，没什么可清的。");
        }
        catch (Exception ex)
        {
            Toast("清理失败：" + ex.Message);
        }
    }

    /* ==================== 模组汉化 ==================== */

    private void RefreshTranslate()
    {
        TranslateCard.Status = "选一个模组 .jar，把里面的英文文本翻成中文，\n生成 zh_cn.json 塞回去（原文件备份成 .bak）";
    }

    private async void TranslatePick_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeFilter.Add(".jar");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        TranslateCard.Status = $"正在处理 {file.Name}…";

        try
        {
            var result = await ModTranslator.TranslateAsync(file.Path, (msg) =>
                DispatcherQueue.TryEnqueue(() => TranslateCard.Status = msg));

            TranslateCard.Status = result.Success
                ? $"完成：{result.Translated} 条\n{Path.GetFileName(result.OutputPath)}"
                : "失败：" + result.Message;

            Toast(result.Success ? $"汉化完成，翻译 {result.Translated} 条" : "汉化失败：" + result.Message);

            if (result.Success)
                _ = ShowDiffAsync(result.OutputPath, result.Entries);
        }
        catch (Exception ex)
        {
            TranslateCard.Status = "失败：" + ex.Message;
            Toast("汉化失败：" + ex.Message);
        }
    }

    /// <summary>把翻译前后的对照摊开给用户看。</summary>
    private async Task ShowDiffAsync(string jarPath, List<(string Key, string En, string Zh)> entries)
    {
        var panel = new StackPanel { Spacing = 8, Width = 640 };

        var head = new TextBlock
        {
            Text = $"{Path.GetFileName(jarPath)} · 共 {entries.Count} 条",
            FontFamily = FontService.BodyFont,
        };
        panel.Children.Add(head);

        var list = new ListView
        {
            Height = 380,
            ItemsSource = entries
                .Select(x => new TextBlock
                {
                    Text = $"{x.Key}\n  {x.En}\n  → {x.Zh}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    FontFamily = FontService.BodyFont,
                    Margin = new Thickness(0, 4, 0, 4),
                })
                .ToList(),
        };
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "汉化结果",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = "知道了",
        };
        FontService.ApplyToDialog(dialog);
        await dialog.ShowAsync();
    }

    /* ==================== 种子地图 ==================== */

    private void RefreshSeed()
    {
        SeedCard.Status = "输入种子号，看附近有什么村庄和结构\n（需要先下载地形解析组件）";
    }

    private async void SeedOpen_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            Header = "种子",
            PlaceholderText = "例如 -3525218295543650087",
        };
        var ver = new ComboBox { Header = "版本", SelectedIndex = 0 };
        foreach (var v in new[] { "1.20", "1.19", "1.18", "1.16" }) ver.Items.Add(v);

        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "种子地图",
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { box, ver } },
            PrimaryButtonText = "生成地图",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        FontService.ApplyToDialog(ask);

        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

        var seed = box.Text.Trim();
        if (seed.Length == 0)
        {
            Toast("先填个种子。");
            return;
        }

        var version = ver.SelectedItem?.ToString() ?? "1.20";
        SeedCard.Status = "正在渲染…";

        const int size = 512;
        const int scale = 4;      // 每像素 4 格 → 覆盖 2048×2048 格
        const int view = 400;     // 弹窗里显示的边长

        var rgb = await SeedMap.RenderAsync(seed, version, size, scale);
        if (rgb is null)
        {
            SeedCard.Status = $"种子 {seed} · 地形库不可用";
            _ = SeedMap.DescribeAsync(seed, version);
            return;
        }

        var bmpPath = Path.Combine(Path.GetTempPath(), $"linx-map-{Guid.NewGuid():N}.bmp");
        try
        {
            WriteBmp(bmpPath, rgb, size, size);
        }
        catch (Exception ex)
        {
            SeedCard.Status = "写图失败：" + ex.Message;
            return;
        }

        var hits = await SeedMap.FindStructuresAsync(seed, version) ?? new();

        // ---- 地图 + 标点 ----
        var canvas = new Canvas { Width = view, Height = view };

        canvas.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(bmpPath)),
            Width = view,
            Height = view,
            Stretch = Stretch.Fill,
        });

        foreach (var hit in hits)
        {
            // 世界坐标 → 图像像素：中心 (0,0)，一圈 2048 格
            var px = hit.X / (double)scale + size / 2.0;
            var py = hit.Z / (double)scale + size / 2.0;

            // 换算到显示尺寸
            var dx = px / size * view;
            var dy = py / size * view;

            if (dx < 6 || dy < 6 || dx > view - 6 || dy > view - 6) continue;

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Red),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(dot, dx - 4.5);
            Canvas.SetTop(dot, dy - 4.5);
            canvas.Children.Add(dot);

            var label = new TextBlock
            {
                Text = hit.Name,
                FontSize = 10,
                FontFamily = FontService.BodyFont,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            // 描边让字在浅色底上也看得清
            var outline = new TextBlock
            {
                Text = hit.Name,
                FontSize = 10,
                FontFamily = FontService.BodyFont,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                Opacity = 0.6,
            };

            Canvas.SetLeft(outline, dx + 7);
            Canvas.SetTop(outline, dy - 6);
            Canvas.SetLeft(label, dx + 6);
            Canvas.SetTop(label, dy - 7);
            canvas.Children.Add(outline);
            canvas.Children.Add(label);
        }

        // 视口比图小，这样才有得拖；缩放用滑块控制（WinUI 的滚轮缩放要触摸屏才好用）
        var scroll = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            Height = view,
            Width = view,
        };

        var zoom = new Slider
        {
            Minimum = 0.5,
            Maximum = 4,
            Value = 1,
            StepFrequency = 0.25,
            Width = view,
        };

        var zoomLabel = new TextBlock
        {
            Text = "缩放 1.0×",
            FontSize = 12,
            FontFamily = FontService.BodyFont,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
        };

        void ApplyZoom(double z)
        {
            // 整个 Canvas 一起缩放，图上的结构点/文字位置就不会跟图错开
            canvas.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform
            {
                ScaleX = z,
                ScaleY = z,
            };
            canvas.Width = size * z;
            canvas.Height = size * z;
            zoomLabel.Text = $"缩放 {z:F1}×";
        }

        zoom.ValueChanged += (_, e) => ApplyZoom(e.NewValue);
        ApplyZoom(1);

        // ---- 右侧结构列表 ----
        var list = new StackPanel { Spacing = 6, Width = 260 };
        list.Children.Add(new TextBlock
        {
            Text = $"种子 {seed} · {version}",
            FontSize = 12,
            FontFamily = FontService.BodyFont,
            Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
        });

        foreach (var hit in hits.OrderBy(h => h.Distance))
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{hit.Name}\n({hit.X}, {hit.Z}) · {hit.Distance:F0} 格",
                FontSize = 11.5,
                FontFamily = FontService.BodyFont,
                LineHeight = 16,
            });
        }

        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(scroll);
        left.Children.Add(zoom);
        left.Children.Add(zoomLabel);

        var body = new Grid { ColumnSpacing = 14 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(list, 1);
        body.Children.Add(left);
        body.Children.Add(list);

        var result = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "种子地图",
            Content = body,
            CloseButtonText = "关闭",
        };
        FontService.ApplyToDialog(result);
        await result.ShowAsync();

        // 卡片只留一行，不要把坐标堆上去
        SeedCard.Status = $"种子 {seed} · {version} · 点「解析」看地图";
    }

    /// <summary>写一张 24 位 BMP（无压缩，WinUI 的 BitmapImage 能直接读）。</summary>
    private static void WriteBmp(string path, byte[] rgb, int w, int h)
    {
        var rowSize = (w * 3 + 3) / 4 * 4;
        var dataSize = rowSize * h;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // 文件头
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(54 + dataSize);
        bw.Write(0);
        bw.Write(54);

        // DIB 头
        bw.Write(40);
        bw.Write(w);
        bw.Write(-h);          // 负高度 = 从上往下存，和我们的行序一致
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0);
        bw.Write(dataSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        var pad = new byte[rowSize - w * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                bw.Write(rgb[i + 2]);   // B
                bw.Write(rgb[i + 1]);   // G
                bw.Write(rgb[i]);       // R
            }
            if (pad.Length > 0) bw.Write(pad);
        }
    }
}

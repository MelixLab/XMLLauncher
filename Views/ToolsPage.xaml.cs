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
        ToolCardsHost.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(ToolCards_PointerMoved), true);
        SizeChanged += (_, _) => UpdateContentPadding();
        Loaded += (_, _) =>
        {
            UpdateContentPadding();
            FontService.Apply(this);
            ToolInit();
            RefreshMem();
            RefreshTranslate();
            RefreshSeed();
            RefreshCache();
        };
    }

    private void UpdateContentPadding() => PageContent.Padding = ActualWidth < 640
        ? new Thickness(16, 20, 16, 24) : new Thickness(32, 28, 32, 24);

    private void ToolCards_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in ToolCardsHost.Children.OfType<Controls.FeatureCard>())
        {
            var point = e.GetCurrentPoint(card).Position;
            card.UpdateReveal(point.X, point.Y);
        }
    }

    private void ToolCards_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        foreach (var card in ToolCardsHost.Children.OfType<Controls.FeatureCard>()) card.ResetReveal();
    }

    private static void Toast(string msg) => MainWindow.Instance?.Toast(msg);

    private void ToolInit()
    {
        MemCard.Header = "内存整理";
        TranslateCard.Header = "模组汉化";
        SeedCard.Header = "种子地图";
        CacheCard.Header = "缓存清理";
        LinkCard.Header = "链接下载";
        RefreshLink();
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
        if (!IsLoaded || XamlRoot is not { } root) return;
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new TextBlock
        {
            Text = $"{Path.GetFileName(jarPath)} · 共 {entries.Count} 条",
            FontFamily = FontService.BodyFont,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(head);

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        UiList.Fill(list, entries
                .Select(x => new TextBlock
                {
                    Text = $"{x.Key}\n  {x.En}\n  → {x.Zh}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    FontFamily = FontService.BodyFont,
                    Margin = new Thickness(0, 4, 0, 4),
                })
                .ToList());
        Grid.SetRow(list, 1);
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "汉化结果",
            Content = panel,
            CloseButtonText = "知道了",
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        void UpdateDialogSize()
        {
            panel.MaxWidth = Math.Max(0, Math.Min(640, root.Size.Width - 96));
            list.MaxHeight = Math.Max(0, Math.Min(380, root.Size.Height - 260));
        }
        void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogSize();
        UpdateDialogSize();
        FontService.ApplyToDialog(dialog);
        root.Changed += Root_Changed;
        try { await dialog.ShowAsync(); }
        finally { root.Changed -= Root_Changed; }
    }

    /* ==================== 种子地图 ==================== */

    private void RefreshSeed() => SeedCard.Status = "探索 Java 世界";

    private void SeedOpen_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("seedmap");

    /* ==================== 链接下载 ==================== */

    private void RefreshLink()
    {
        LinkCard.Status = "粘贴任意 http(s) 地址，交给内置下载引擎（单文件分块并发）";
    }

    private async void LinkDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || XamlRoot is not { } root) return;

        var input = new TextBox
        {
            PlaceholderText = "https://example.com/file.zip",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            FontFamily = FontService.BodyFont,
        };

        var hint = new TextBlock
        {
            Text = "一行一个地址。下载任务会出现在「下载中心」，可以随时取消。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("SecondaryTextBrush"),
            FontFamily = FontService.BodyFont,
        };

        var threadsBox = new ComboBox { Header = "线程数", MinWidth = 140 };
        int currentThreads = Math.Clamp(AppState.Settings.DownloadThreads, 1, 32);
        var threadOptions = new[] { 4, 8, 16, 32, currentThreads }.Distinct().OrderBy(n => n).ToArray();
        foreach (var n in threadOptions) threadsBox.Items.Add(n + " 线程");
        threadsBox.SelectedIndex = Array.IndexOf(threadOptions, currentThreads);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "链接下载",
            Content = new StackPanel { Spacing = 10, Children = { input, hint, threadsBox } },
            PrimaryButtonText = "开始下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var links = LinkDownloader.SplitLinks(input.Text).ToList();
        if (links.Count == 0)
        {
            LinkCard.Status = "没有读到有效地址";
            return;
        }

        // 保存目录
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var jobs = LinkDownloader.BuildJobs(links, folder.Path);
        if (jobs.Count == 0)
        {
            LinkCard.Status = "地址格式不对，只支持 http / https";
            return;
        }

        int threads = threadOptions[threadsBox.SelectedIndex];

        AppState.Settings.DownloadThreads = threads;
        SettingsService.Save();

        // 和模组下载同一套：开任务 → 跳到下载中心 → 上报 → 收尾
        var job = AppState.Downloads.Begin(
            jobs.Count == 1 ? $"{jobs[0].Label} · 链接" : $"{jobs.Count} 个链接 · 下载",
            $"{threads} 线程");

        MainWindow.Instance?.NavigateTo("downloads");

        try
        {
            var (ok, failed, error) = await LinkDownloader.RunAsync(jobs, threads, job, job.Cts.Token);

            if (error.Length > 0)
            {
                AppState.Downloads.Finish(job, false, error);
                MainWindow.Instance?.Toast($"下载结束：{failed} 个失败", true);
            }
            else
            {
                AppState.Downloads.Finish(job, true);
                MainWindow.Instance?.Toast($"下载完成（{ok} 个文件）");
            }

            LinkCard.Status = error.Length == 0
                ? $"上次下载 {ok} 个文件成功，存到 {folder.Path}"
                : $"完成 {ok} 个，失败 {failed} 个：{error}";
            Log.Info($"链接下载：{ok}/{jobs.Count} → {folder.Path}");
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            AppState.Downloads.Finish(job, false, "已取消");
            LinkCard.Status = "已取消";
        }
        catch (Exception ex)
        {
            Log.Error("LinkDownload", ex);
            AppState.Downloads.Finish(job, false, ex.Message);
            LinkCard.Status = "失败：" + ex.Message.Split('\n')[0];
            MainWindow.Instance?.Toast("下载失败：" + ex.Message.Split('\n')[0], true);
        }
    }
}

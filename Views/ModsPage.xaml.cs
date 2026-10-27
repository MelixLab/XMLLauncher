using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using LinxLauncher.Models;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

/// <summary>
/// Mod 下载页：Modrinth 搜索 + 加载器/版本筛选 + 一键装进选中的实例（含 required 依赖）。
/// 结果分页：底部「加载更多」或滚到底自动续下一页。
/// </summary>
public sealed partial class ModsPage : Page
{
    private const int PageSize = 25;

    private readonly ObservableCollection<ModHit> _items = new();
    private CancellationTokenSource? _searchCts;
    private bool _loading;
    private int _total;

    private ScrollViewer? _scroll;

    public ModsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _scroll = FindScrollViewer(ResultList);
            if (_scroll is not null) _scroll.ViewChanged += ResultList_ViewChanged;
        };
    }

    /// <summary>ListView 内部才有 ScrollViewer，翻到底部靠它判断。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        FillTargets();
        _ = SearchAsync(reset: true);
    }

    private void FillTargets()
    {
        var installed = AppState.Installed;
        UiList.Fill(TargetBox, installed);

        var current = AppState.Current;
        if (current is not null) TargetBox.SelectedItem = current;
        else if (installed.Count > 0) TargetBox.SelectedIndex = 0;
    }

    private string? SelectedLoader()
    {
        if (LoaderBox.SelectedIndex <= 0 || LoaderBox.SelectedItem is not ComboBoxItem item) return null;

        return item.Content?.ToString() switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "NeoForge" => "neoforge",
            "Quilt" => "quilt",
            _ => null
        };
    }

    private static string? GameVersionFor(LocalVersion? v)
    {
        if (v is null) return null;
        return string.IsNullOrWhiteSpace(v.BaseVersion) ? v.Id : v.BaseVersion;
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 换目标实例时，加载器跟着实例走
        var inst = TargetBox.SelectedItem as LocalVersion;
        var tag = ModrinthService.LoaderTag(inst?.DisplayKind);
        LoaderBox.SelectedIndex = tag switch
        {
            "fabric" => 1,
            "forge" => 2,
            "neoforge" => 3,
            "quilt" => 4,
            _ => 0
        };
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = SearchAsync(reset: true);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: true);
    private void More_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: false);

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SearchAsync(reset: true);
    }

    /// <summary>滚到接近底部就自动续一页。</summary>
    private void ResultList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_loading) return;
        if (_items.Count == 0 || _items.Count >= _total) return;

        if (sender is ScrollViewer sv && sv.VerticalOffset >= sv.ScrollableHeight - 120)
            _ = SearchAsync(reset: false);
    }

    private async Task SearchAsync(bool reset)
    {
        if (_loading && !reset) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _loading = true;

        if (reset)
        {
            _items.Clear();
            UiList.Clear(ResultList);
            _total = 0;
            MoreButton.Visibility = Visibility.Collapsed;
            FootHint.Text = "";
        }

        var loader = SelectedLoader();
        var target = TargetBox.SelectedItem as LocalVersion;
        var gameVersion = GameVersionFor(target);
        var sort = (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "相关度";
        var offset = _items.Count;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = reset ? "正在搜索…" : "正在加载更多…";

        try
        {
            var (hits, total) = await ModrinthService.SearchAsync(
                SearchBox.Text.Trim(), loader, gameVersion, sort, offset, PageSize, cts.Token);

            if (cts.IsCancellationRequested) return;

            _total = total;
            foreach (var hit in hits)
            {
                _items.Add(hit);
                UiList.Add(ResultList, hit);
            }

            StatusText.Text = total == 0
                ? "没搜到。换个关键词，或者把加载器切回「全部加载器」再试。"
                : $"共 {total:N0} 个结果"
                  + (gameVersion is null ? "" : $" · 游戏版本 {gameVersion}")
                  + (loader is null ? "" : $" · {loader}");

            FootHint.Text = _items.Count >= _total
                ? (_items.Count == 0 ? "" : $"已经到底了（共 {_total:N0} 个）")
                : $"已显示 {_items.Count:N0} / {_total:N0}";

            MoreButton.Visibility = _items.Count < _total ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Modrinth", ex);
            StatusText.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    // ================= 安装 =================

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModHit hit) return;

        var target = TargetBox.SelectedItem as LocalVersion;
        if (target is null)
        {
            MainWindow.Instance?.Toast("先在顶部选一个要装进去的版本", true);
            return;
        }

        if (target.DisplayKind == "原版")
        {
            MainWindow.Instance?.Toast("原版实例装不了 Mod，先给它装个 Fabric / Forge", true);
            return;
        }

        var loader = ModrinthService.LoaderTag(target.DisplayKind);
        var gameVersion = GameVersionFor(target);
        var modsDir = AppState.Settings.Instances.TryGetValue(target.Id, out var inst) && inst.Isolated
            ? Path.Combine(Paths.VersionDir(target.Id), "mods")
            : Paths.ModsDir;

        Directory.CreateDirectory(modsDir);

        var job = AppState.Downloads.Begin($"{hit.Title} · Mod");
        var dialog = new Controls.DownloadProgressDialog(job) { XamlRoot = XamlRoot };
        _ = dialog.ShowAsync();

        try
        {
            job.Apply(new InstallProgress { Stage = "查找文件", Indeterminate = true, Message = "正在查询可用版本…" });

            var file = await ModrinthService.ResolveAsync(hit.ProjectId, loader, gameVersion, job.Cts.Token);
            if (file is null || string.IsNullOrEmpty(file.Url))
                throw new IOException($"{hit.Title} 没有适用于 {target.Id} 的文件（加载器 {target.DisplayKind}）。");

            var all = new List<ModFile> { file };
            all.AddRange(file.Dependencies);

            long totalBytes = all.Sum(f => Math.Max(0, f.Size));
            long doneBytes = 0;
            int doneFiles = 0;

            foreach (var f in all)
            {
                job.Cts.Token.ThrowIfCancellationRequested();

                var dest = Path.Combine(modsDir, string.IsNullOrEmpty(f.FileName) ? "mod.jar" : f.FileName);
                var label = Path.GetFileName(f.FileName);

                void Report() => job.Apply(new InstallProgress
                {
                    Stage = "下载 Mod",
                    Message = label,
                    DoneBytes = doneBytes,
                    TotalBytes = Math.Max(totalBytes, 1),
                    DoneFiles = doneFiles,
                    TotalFiles = all.Count
                });

                Report();

                await Http.DownloadToFileAsync(f.Url, dest, n =>
                {
                    doneBytes += n;
                    Report();
                }, job.Cts.Token);

                doneFiles++;
            }

            AppState.Downloads.Finish(job, true);

            var depText = all.Count > 1 ? $"，连依赖一共 {all.Count} 个文件" : "";
            MainWindow.Instance?.Toast($"已把 {hit.Title} 装进 {target.Id}{depText}");
            Log.Info($"安装 Mod：{hit.Title} → {modsDir}");
        }
        catch (OperationCanceledException)
        {
            AppState.Downloads.Finish(job, false, "已取消");
        }
        catch (Exception ex)
        {
            Log.Error("ModInstall", ex);
            AppState.Downloads.Finish(job, false, ex.Message);
            MainWindow.Instance?.Toast("安装失败：" + ex.Message.Split('\n')[0], true);
        }
    }
}

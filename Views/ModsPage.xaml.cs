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
using Windows.Networking.Connectivity;
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
    private bool _active;
    private bool _updatingFilters;
    private bool _requestFailed;
    private bool _hasMore;
    private int _total;
    private string _errorTitle = "";
    private string _errorMessage = "";
    private (string Query, string? Loader, string? GameVersion, string Sort) _search = ("", null, null, "下载量");

    private ScrollViewer? _scroll;

    public ModsPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        DetachScroll();
        _scroll = FindScrollViewer(ResultList);
        if (_scroll is not null) _scroll.ViewChanged += ResultList_ViewChanged;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _active = false;
        CancelSearch();
        DetachScroll();
    }

    private void DetachScroll()
    {
        if (_scroll is not null) _scroll.ViewChanged -= ResultList_ViewChanged;
        _scroll = null;
    }

    private void CancelSearch()
    {
        var cts = _searchCts;
        _searchCts = null;
        cts?.Cancel();
        // 请求自身在 finally 中释放 CTS，避免其仍在使用取消令牌时提前 Dispose。
        _loading = false;
        LoadingRing.IsActive = false;
        MoreLoadingRing.IsActive = false;
    }

    private void PageContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double side = e.NewSize.Width < 720 ? 16 : 32;
        ModHeader.Margin = new Thickness(side, e.NewSize.Width < 720 ? 20 : 28, side, 0);
        SearchRow.Margin = new Thickness(side, 20, side, 0);
        FilterHost.Margin = new Thickness(side, 8, side, 0);
        ResultBackground.Margin = new Thickness(side, 0, side, 0);
        StatusText.Margin = new Thickness(side + 12, 12, side + 12, 8);
        ResultList.Padding = new Thickness(side + 12, 0, side + 12, 12);
        ResultFooter.Margin = new Thickness(side + 12, 10, side + 12, 12);
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
        _active = true;
        _updatingFilters = true;
        try
        {
            FillTargets();
            UpdateLoaderForTarget();
        }
        finally
        {
            _updatingFilters = false;
        }
        _ = SearchAsync(reset: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        CancelSearch();
        DetachScroll();
        base.OnNavigatedFrom(e);
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
        if (!_active || _updatingFilters) return;
        _updatingFilters = true;
        try
        {
            UpdateLoaderForTarget();
        }
        finally
        {
            _updatingFilters = false;
        }
        // 即使加载器不变，也要重新应用目标实例的游戏版本条件。
        _ = SearchAsync(reset: true);
    }

    private void UpdateLoaderForTarget()
    {
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
        if (!_active || _updatingFilters) return;
        _ = SearchAsync(reset: true);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: true);
    private void More_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: false);
    private void Retry_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: true, reuseSearch: true);

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SearchAsync(reset: true);
        }
    }

    /// <summary>滚到接近底部就自动续一页。</summary>
    private void ResultList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_active || _loading || _requestFailed || !_hasMore || _items.Count == 0) return;

        if (sender is ScrollViewer sv && sv.VerticalOffset >= sv.ScrollableHeight - 120)
            _ = SearchAsync(reset: false);
    }

    private async Task SearchAsync(bool reset, bool reuseSearch = false)
    {
        if (!_active) return;
        if (_loading && !reset) return;
        if (!reset && (!_hasMore || _items.Count == 0)) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _loading = true;
        _requestFailed = false;
        _errorTitle = _errorMessage = "";

        if (reset)
        {
            _items.Clear();
            UiList.Clear(ResultList);
            _total = 0;
            _hasMore = false;
            if (!reuseSearch)
            {
                _search = (
                    SearchBox.Text.Trim(),
                    SelectedLoader(),
                    GameVersionFor(TargetBox.SelectedItem as LocalVersion),
                    (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "相关度");
            }
            StatusText.Text = "Modrinth · 搜索结果";
        }

        // 后续页和重试始终沿用已提交的条件，编辑尚未提交的关键词不会混入其他结果。
        var search = _search;
        var offset = _items.Count;
        UpdateSearchState();

        try
        {
            var (hits, total) = await ModrinthService.SearchAsync(
                search.Query, search.Loader, search.GameVersion, search.Sort, offset, PageSize, cts.Token);

            if (!OwnsRequest(cts)) return;

            _total = total;
            foreach (var hit in hits)
            {
                _items.Add(hit);
                UiList.Add(ResultList, hit);
            }

            // 服务端总数可能在翻页期间变化；空页应停止自动续页。
            _hasMore = hits.Count > 0 && _items.Count < _total;
            StatusText.Text = $"共 {total:N0} 个结果"
                + (search.GameVersion is null ? "" : $" · 游戏版本 {search.GameVersion}")
                + (search.Loader is null ? "" : $" · {search.Loader}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!OwnsRequest(cts)) return;
            Log.Error("Modrinth", ex);
            _requestFailed = true;
            (_errorTitle, _errorMessage) = SearchError(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                _loading = false;
                if (_active) UpdateSearchState();
            }
            cts.Dispose();
        }
    }

    private bool OwnsRequest(CancellationTokenSource cts) =>
        _active && ReferenceEquals(_searchCts, cts) && !cts.IsCancellationRequested;

    private static (string Title, string Message) SearchError(Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException || ex is TimeoutException)
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile is null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
                    return ("请检查网络连接", "当前网络可能无法访问互联网。连接网络后可重试本次搜索。");
            }
            catch
            {
                // 系统网络状态仅作提示；读取失败时仍显示原始请求的错误状态。
            }
        }
        if (ex is OperationCanceledException || ex is TimeoutException)
            return ("请求超时", "Modrinth 暂未响应，请稍后重试。");
        if (ex is System.Net.Http.HttpRequestException)
            return ("暂时无法连接 Modrinth", "请检查网络连接，或稍后重试。");
        return ("搜索请求失败", "搜索请求未能完成，请调整关键词或筛选条件后重试。");
    }

    private void UpdateSearchState()
    {
        bool hasItems = _items.Count > 0;
        // 空态覆盖结果区，但保留列表的可视树，确保其内部滚动器在首次加载时即可订阅。
        ResultList.IsHitTestVisible = hasItems;
        ResultList.IsTabStop = hasItems;
        ResultState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = _loading && !hasItems;
        LoadingRing.Visibility = LoadingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Visibility = _loading ? Visibility.Collapsed : Visibility.Visible;
        StateIcon.Glyph = _requestFailed ? "\uE783" : "\uE721";
        StateTitle.Text = _loading ? "正在搜索 Mod…"
            : _requestFailed ? _errorTitle : "没有找到匹配的 Mod";
        StateMessage.Text = _loading ? "正在从 Modrinth 获取结果，请稍候。"
            : _requestFailed ? _errorMessage : "试试其他关键词，或调整加载器与目标实例筛选。";
        RetryButton.Visibility = _requestFailed && !_loading ? Visibility.Visible : Visibility.Collapsed;

        ResultFooter.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        MoreLoadingRing.IsActive = _loading && hasItems;
        MoreLoadingRing.Visibility = MoreLoadingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        FootHint.Text = _loading ? "正在加载下一页…"
            : _requestFailed ? $"下一页加载失败，已有结果已保留。{_errorMessage}"
            : _hasMore ? $"已显示 {_items.Count:N0} / {_total:N0}"
            : $"已显示全部结果（{_items.Count:N0} 个）";
        MoreButton.Content = _requestFailed ? "重试加载" : "加载更多";
        MoreButton.IsEnabled = !_loading;
        MoreButton.Visibility = hasItems && (_hasMore || _requestFailed) ? Visibility.Visible : Visibility.Collapsed;
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

        var operationKey = $"mod:{target.Id}:{hit.ProjectId}";
        if (AppState.Downloads.IsRunningFor(operationKey))
        {
            MainWindow.Instance?.NavigateTo("downloads");
            return;
        }
        var job = AppState.Downloads.Begin($"{hit.Title} · Mod", operationKey: operationKey);

        try
        {
            MainWindow.Instance?.NavigateTo("downloads");
            Directory.CreateDirectory(modsDir);
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

            job.Cts.Token.ThrowIfCancellationRequested();
            AppState.Downloads.Finish(job, true);

            var depText = all.Count > 1 ? $"，连依赖一共 {all.Count} 个文件" : "";
            MainWindow.Instance?.Toast($"已把 {hit.Title} 装进 {target.Id}{depText}");
            Log.Info($"安装 Mod：{hit.Title} → {modsDir}");
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
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

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

/// <summary>
/// 云存档：配额、上传、下载、删除。
///
/// 上传的是「一个世界目录」打成 zip。同一个世界名在服务端只留一份，
/// 再传会覆盖——所以上传前会明确提示，并且拒绝明显异常的存档
/// （比如本地被删空了还照传，那等于把好的备份冲掉）。
/// </summary>
public sealed partial class CloudSavesPage : Page
{
    private List<CloudSave> _cloudSaves = new();
    private long _quota;
    private long _used;
    private CancellationTokenSource _refreshCts = new();
    private int _refreshVersion;
    private bool _active;

    private sealed class CloudSave
    {
        public long Id { get; set; }
        public string World { get; set; } = "";
        public string Version { get; set; } = "";
        public long Size { get; set; }
        public long UpdatedAt { get; set; }
    }

    public CloudSavesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _refreshCts.Cancel();
            _refreshCts.Dispose();
            _refreshCts = new CancellationTokenSource();
            _active = true;
            // 缓存保留页面结构；每次进入仍重新读取当前账号的数据。
            _cloudSaves.Clear();
            _quota = _used = 0;
            CloudSubtitle.Text = "把世界备份到服务器，换台机器也能接着玩。";
            SaveList.Children.Clear();
            QuotaText.Text = "";
            QuotaBar.Value = 0;
            EmptyHint.Visibility = Visibility.Visible;
            FontService.Apply(this);
            await RefreshAsync();
        };
        Unloaded += (_, _) => DeactivatePage();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DeactivatePage();
        base.OnNavigatedFrom(e);
    }

    private void DeactivatePage()
    {
        _active = false;
        ++_refreshVersion;
        _refreshCts.Cancel();
    }

    private static void Toast(string msg, bool error = false) => MainWindow.Instance?.Toast(msg, error);

    /* ==================== 数据 ==================== */

    private async Task RefreshAsync()
    {
        if (!_active) return;
        var token = _refreshCts.Token;
        int request = ++_refreshVersion;
        string viewer = LinxAccountService.DisplayName;
        bool IsCurrentRequest() => _active && !token.IsCancellationRequested
            && request == _refreshVersion && viewer == LinxAccountService.DisplayName;
        if (!LinxAccountService.IsLoggedIn)
        {
            CloudSubtitle.Text = "先在左下角登录 XML 账号，才能用云存档。";
            QuotaText.Text = "未登录";
            SaveList.Children.Clear();
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var (ok, doc, err) = await LinxAccountService.CallAsync(
                "/api/cloud/saves", null, token, "GET");
            using var response = doc;
            if (!IsCurrentRequest()) return;

            if (!ok || doc is null)
            {
                CloudSubtitle.Text = "读取失败：" + err;
                return;
            }

            if (doc is not null)
            {
                var root = doc.RootElement;
                var quota = root.TryGetProperty("quota", out var q) ? q.GetInt64() : 0;
                var used = root.TryGetProperty("used", out var u) ? u.GetInt64() : 0;

                UpdateQuota(quota, used);

                _quota = quota;
                _used = used;

                var list = new List<CloudSave>();
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        list.Add(new CloudSave
                        {
                            Id = it.GetProperty("id").GetInt64(),
                            World = it.TryGetProperty("world", out var w) ? w.GetString() ?? "" : "",
                            Version = it.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                            Size = it.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                            UpdatedAt = it.TryGetProperty("updated_at", out var t) ? t.GetInt64() : 0,
                        });
                    }
                }

                _cloudSaves = list;
                RenderSaves(list);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentRequest()) CloudSubtitle.Text = "读取失败：" + ex.Message;
        }
    }

    private void UpdateQuota(long quota, long used)
    {
        if (quota <= 0)
        {
            QuotaText.Text = "还没有分配空间";
            QuotaBar.Value = 0;
            return;
        }

        var free = Math.Max(0, quota - used);
        QuotaText.Text = $"还剩 {FormatSize(free)}（已用 {FormatSize(used)} / {FormatSize(quota)}）";
        QuotaBar.Value = Math.Min(100, used * 100.0 / quota);
    }

    private void RenderSaves(List<CloudSave> list)
    {
        SaveList.Children.Clear();
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var s in list)
        {
            var card = new Grid
            {
                Padding = new Thickness(16, 12, 16, 12),
                ColumnSpacing = 12,
                Background = ThemeService.Brush("CardBackgroundBrush"),
                BorderBrush = ThemeService.Brush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 3 };
            info.Children.Add(new TextBlock
            {
                Text = s.World,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = FontService.BodyFont,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{FormatSize(s.Size)} · {s.Version} · {FormatTime(s.UpdatedAt)}",
                FontSize = 12.5,
                FontFamily = FontService.BodyFont,
                Foreground = ThemeService.Brush("SecondaryTextBrush"),
            });
            Grid.SetColumn(info, 0);
            card.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

            var down = new Button { Content = "下载" };
            down.Click += async (_, _) => await DownloadAsync(s);
            actions.Children.Add(down);

            var del = new Button { Content = "删除" };
            del.Click += async (_, _) => await DeleteAsync(s);
            actions.Children.Add(del);

            Grid.SetColumn(actions, 1);
            card.Children.Add(actions);

            SaveList.Children.Add(card);
        }
    }

    /* ==================== 上传 ==================== */

    /// <summary>
    /// 检查同步：把本地和云端摆在一起比。
    ///
    /// 关键点：本地原始文件大小和云端 zip 大小不是一回事，
    /// 所以这里先把本地世界按同样方式压一遍再比，差多少才算得准。
    /// </summary>
    private async void SyncCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!LinxAccountService.IsLoggedIn)
        {
            Toast("先登录 XML 账号。", error: true);
            return;
        }

        var locals = FindWorlds();
        if (locals.Count == 0)
        {
            Toast("本地没有存档。", error: true);
            return;
        }

        SyncCheckButton.IsEnabled = false;
        SyncCheckButton.Content = "检查中…";

        try
        {
            var marks = AppState.Settings;
            var cloudByName = _cloudSaves.ToDictionary(s => s.World, StringComparer.OrdinalIgnoreCase);

            var rows = new List<(string Name, string State, string Detail, (World World, long Zipped, long Mtime)? Local)>();

            foreach (var w in locals)
            {
                var zipped = await Task.Run(() => EstimateZippedSize(w.Path));

                long newest = 0;
                foreach (var f in Directory.GetFiles(w.Path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var t2 = new DateTimeOffset(new FileInfo(f).LastWriteTimeUtc).ToUnixTimeSeconds();
                        if (t2 > newest) newest = t2;
                    }
                    catch { }
                }

                cloudByName.TryGetValue(w.Name, out var cloud);

                // 没传过 → 要传
                if (cloud is null)
                {
                    rows.Add((w.Name, "待上传", $"本地 {FormatSize(zipped)} · 云端还没有", (w, zipped, newest)));
                    continue;
                }

                // 传过：看本地在上次上传之后有没有再改
                var lastUpload = marks.LocalSyncMarks.TryGetValue(w.Name, out var lm) ? lm : 0;

                if (lastUpload == 0)
                {
                    // 没有记录（老版本传的），按云端记录时间和本地文件时间粗判一次
                    var changed = newest > cloud.UpdatedAt + 30;
                    rows.Add((w.Name,
                        changed ? "待上传" : "已同步",
                        changed
                            ? $"本地 {FormatSize(zipped)} · 云端 {FormatSize(cloud.Size)} · 本地可能改过"
                            : $"本地 {FormatSize(zipped)} · 云端 {FormatSize(cloud.Size)}",
                        (w, zipped, newest)));
                }
                else if (newest > lastUpload + 5)
                {
                    rows.Add((w.Name, "待上传",
                        $"本地 {FormatSize(zipped)} · 云端 {FormatSize(cloud.Size)} · 上次同步后改过",
                        (w, zipped, newest)));
                }
                else
                {
                    rows.Add((w.Name, "已同步",
                        $"本地 {FormatSize(zipped)} · 云端 {FormatSize(cloud.Size)}",
                        (w, zipped, newest)));
                }
            }

            var need = rows.Where(r => r.State == "待上传").ToList();

            var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };

            panel.Children.Add(new TextBlock
            {
                Text = need.Count == 0
                    ? "都同步过了，没有要传的"
                    : $"{need.Count} 个存档需要上传，{rows.Count - need.Count} 个已同步",
                FontSize = 12.5,
                FontFamily = FontService.BodyFont,
            });

            foreach (var r in rows.OrderByDescending(x => x.State == "待上传").ThenBy(x => x.Name))
            {
                var row = new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                };

                var g = new Grid { ColumnSpacing = 10 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Spacing = 3 };
                left.Children.Add(new TextBlock
                {
                    Text = $"{r.Name}   {r.State}",
                    FontSize = 14,
                    FontFamily = FontService.BodyFont,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                left.Children.Add(new TextBlock
                {
                    Text = r.Detail,
                    FontSize = 12,
                    FontFamily = FontService.BodyFont,
                    TextWrapping = TextWrapping.Wrap,
                });
                g.Children.Add(left);

                if (r.State == "待上传")
                {
                    var up = new Button { Content = "上传", FontSize = 12 };
                    var w = r.Local!.Value.World;
                    up.Click += async (_, _) => await UploadWorldAsync(w);

                    var act = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    act.Children.Add(up);
                    Grid.SetColumn(act, 1);
                    g.Children.Add(act);
                }

                row.Child = g;
                panel.Children.Add(row);
            }

            // 全部待上传的，给一个一键按钮
            if (need.Count > 1)
            {
                var all = new Button
                {
                    Content = $"全部上传（{need.Count} 个）",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                all.Click += async (_, _) =>
                {
                    foreach (var r in need)
                    {
                        await UploadWorldAsync(r.Local!.Value.World);
                    }
                };
                panel.Children.Add(all);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "同步状态",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
                CloseButtonText = "关闭",
            };
            FontService.ApplyToDialog(dialog);
            await dialog.ShowAsync();
        }
        finally
        {
            SyncCheckButton.IsEnabled = true;
            SyncCheckButton.Content = "检查同步";
        }
    }

    /// <summary>把某个世界压一遍，拿到压缩后的真实体积（和云端存的口径一致）。</summary>
    private static long EstimateZippedSize(string worldPath)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"linx-size-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(worldPath, temp, CompressionLevel.Fastest, includeBaseDirectory: false);
            var size = new FileInfo(temp).Length;
            try { File.Delete(temp); } catch { }
            return size;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>单个世界上传到云端（同步检查里直接点上传用）。</summary>
    private async Task UploadWorldAsync(World world)
    {
        try
        {
            ResetUploadProgress();
            CloudSubtitle.Text = "正在打包…";
            var zip = await Task.Run(() => PackWorld(world));

            CloudSubtitle.Text = "正在上传…";
            var result = await UploadZipAsync(world.Name, zip, UpdateUploadProgress);

            Toast(result.ok ? $"「{world.Name}」已上传" : "上传失败：" + result.error, error: !result.ok);
            ResetUploadProgress();

            // 先刷新，让 _cloudSaves 拿到刚上传的那条，再记同步标记。
            // 反过来的话记录会找不到云端条目，下次检查就会误判成「云端有更新」。
            await RefreshAsync();

            if (result.ok)
            {
                var cloud = _cloudSaves.FirstOrDefault(x =>
                    string.Equals(x.World, world.Name, StringComparison.OrdinalIgnoreCase));

                var localMtime = 0L;
                foreach (var f in Directory.GetFiles(world.Path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var tt = new DateTimeOffset(new FileInfo(f).LastWriteTimeUtc).ToUnixTimeSeconds();
                        if (tt > localMtime) localMtime = tt;
                    }
                    catch { }
                }

                AppState.Settings.LocalSyncMarks[world.Name] = localMtime;
                if (cloud is not null) AppState.Settings.CloudSyncMarks[world.Name] = cloud.UpdatedAt;
                SettingsService.Save();
            }
        }
        catch (Exception ex)
        {
            CloudSubtitle.Text = "上传失败：" + ex.Message;
            Toast("上传失败：" + ex.Message, error: true);
        }
    }

    /// <summary>
    /// 从云端同步：把云端最新的一份拉下来覆盖本地。
    /// 覆盖前把云端和本地的体积、时间都摆出来，让用户看清楚会丢什么。
    /// </summary>
    private async void SyncDown_Click(object sender, RoutedEventArgs e)
    {
        if (!LinxAccountService.IsLoggedIn)
        {
            Toast("先登录 XML 账号。", error: true);
            return;
        }

        if (_cloudSaves.Count == 0)
        {
            Toast("云端还没有存档，先上传一份。", error: true);
            return;
        }

        var picker = new ComboBox { Header = "要恢复哪个存档", MinWidth = 360 };
        foreach (var s in _cloudSaves.OrderBy(x => x.World))
            picker.Items.Add($"{s.World}   （{FormatSize(s.Size)} · {FormatTime(s.UpdatedAt)}）");
        picker.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从云端同步",
            Content = new StackPanel { Spacing = 12, Children = { picker } },
            PrimaryButtonText = "查看差异",
            CloseButtonText = "取消",
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var idx = picker.SelectedIndex;
        if (idx < 0 || idx >= _cloudSaves.Count) return;

        var save = _cloudSaves.OrderBy(x => x.World).ElementAt(idx);

        // 找本地同名世界，算清楚差多少
        var localWorld = FindWorlds().FirstOrDefault(w =>
            string.Equals(w.Name, save.World, StringComparison.OrdinalIgnoreCase));

        var localBytes = 0L;
        if (localWorld is not null)
        {
            localBytes = Directory.GetFiles(localWorld.Path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }

        var detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = localWorld is null
                ? $"本地没有「{save.World}」，会新建一个。\n\n云端：{FormatSize(save.Size)} · {FormatTime(save.UpdatedAt)}"
                : $"本地：{FormatSize(localBytes)} · {localWorld.SavedBy}\n"
                  + $"云端：{FormatSize(save.Size)} · {FormatTime(save.UpdatedAt)}\n\n"
                  + $"差 {(localBytes > save.Size ? "-" : "+")}{FormatSize(Math.Abs(localBytes - save.Size))}"
                  + $"（本地{(localBytes > save.Size ? "更大" : "更小")}）",
        };

        var warn = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = localWorld is null ? "确认新建" : "确认覆盖本地",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    detail,
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = localWorld is null
                            ? "会把云端的存档写到本地 saves 目录。"
                            : "本地这个世界的存档会被云端那份整个替换掉：进度、建筑、箱子全部丢失，没法撤销。",
                    },
                },
            },
            PrimaryButtonText = "确认同步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        FontService.ApplyToDialog(warn);

        if (await warn.ShowAsync() != ContentDialogResult.Primary) return;

        await DownloadAsync(save, skipConfirm: true);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!LinxAccountService.IsLoggedIn)
        {
            Toast("先登录 XML 账号。", error: true);
            return;
        }

        var worlds = FindWorlds();
        if (worlds.Count == 0)
        {
            Toast("没找到任何存档。先玩一局，或者检查游戏目录。", error: true);
            return;
        }

        // 按版本分组：不同实例可能有同名世界，所以要先选版本
        var byVersion = worlds
            .GroupBy(w => w.Version)
            .OrderBy(g => g.Key == "共享" ? 0 : 1)
            .ThenBy(g => g.Key)
            .ToList();

        var versionBox = new ComboBox { Header = "版本实例", MinWidth = 320 };
        foreach (var g in byVersion) versionBox.Items.Add(g.Key);
        versionBox.SelectedIndex = 0;

        var worldBox = new ComboBox { Header = "该实例下的存档", MinWidth = 320 };

        void FillWorlds()
        {
            worldBox.Items.Clear();
            var idx = versionBox.SelectedIndex;
            if (idx < 0 || idx >= byVersion.Count) return;

            foreach (var w in byVersion[idx].OrderBy(x => x.Name))
                worldBox.Items.Add($"{w.Name}   （存档 {w.SavedBy}）");

            if (worldBox.Items.Count > 0) worldBox.SelectedIndex = 0;
        }

        FillWorlds();
        versionBox.SelectionChanged += (_, _) => FillWorlds();

        var tip = new TextBlock
        {
            Text = "同一个世界在云端只保留一份，这次上传会覆盖旧的那份。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("SecondaryTextBrush"),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "上传存档",
            Content = new StackPanel { Spacing = 12, Children = { versionBox, worldBox, tip } },
            PrimaryButtonText = "上传",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var vi = versionBox.SelectedIndex;
        var wi = worldBox.SelectedIndex;
        if (vi < 0 || vi >= byVersion.Count) return;

        var group = byVersion[vi].OrderBy(x => x.Name).ToList();
        if (wi < 0 || wi >= group.Count) return;

        var world = group[wi];

        // 存档安全检查
        var files = Directory.GetFiles(world.Path, "*", SearchOption.AllDirectories);
        var total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

        if (files.Length < 3 || total < 4096)
        {
            var warn = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "这个存档看起来不对",
                Content = $"「{world.Version} · {world.Name}」只有 {files.Length} 个文件、{FormatSize(total)}。\n\n"
                          + "如果本地存档刚被误删，现在上传会把云端那份好的覆盖掉。确认要传吗？",
                PrimaryButtonText = "我确定要传",
                CloseButtonText = "算了",
            };
            FontService.ApplyToDialog(warn);
            if (await warn.ShowAsync() != ContentDialogResult.Primary) return;
        }

        // 先算清楚这次要占多少空间：新存档大小减去云端已有的那份。
        // 等打包、上传完再报「空间不够」纯属浪费流量和时间。
        var localBytes = Directory.GetFiles(world.Path, "*", SearchOption.AllDirectories)
            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

        var existing = _cloudSaves.FirstOrDefault(s =>
            string.Equals(s.World, world.Name, StringComparison.OrdinalIgnoreCase));

        // 云端存的是 zip，本地是原始文件，用 0.92 估一下压缩率
        var estimated = (long)(localBytes * 0.92);
        var delta = estimated - (existing?.Size ?? 0);
        var free = Math.Max(0, _quota - _used);

        if (delta > free)
        {
            var detail = existing is null
                ? $"要传 {FormatSize(estimated)}，但只剩 {FormatSize(free)} 可用。"
                : $"这次要传 {FormatSize(estimated)}，云端已有 {FormatSize(existing.Size)}，"
                  + $"净增 {FormatSize(Math.Max(0, delta))}，但只剩 {FormatSize(free)} 可用。";

            CloudSubtitle.Text = "空间不够：" + detail;

            Toast("空间不够 — " + detail, error: true);
            return;
        }

        try
        {
            ResetUploadProgress();
            CloudSubtitle.Text = "正在打包…";
            var zip = await Task.Run(() => PackWorld(world));

            CloudSubtitle.Text = "正在上传…";
            var result = await UploadZipAsync(world.Name, zip, UpdateUploadProgress);

            CloudSubtitle.Text = result.ok
                ? $"已上传「{world.Version} · {world.Name}」（存档 {world.SavedBy}，{FormatSize(result.size)}）"
                : "上传失败：" + result.error;

            Toast(result.ok ? "云存档已更新" : "上传失败：" + result.error, error: !result.ok);
            ResetUploadProgress();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            CloudSubtitle.Text = "上传失败：" + ex.Message;
        }
    }

    private sealed record World(string Name, string Path, string Version, string SavedBy);

    /// <summary>
    /// 扫所有版本实例的存档：全局的 saves/ 加上每个隔离实例的 versions/&lt;id&gt;/saves。
    /// 这样上传时能看到「这个存档属于哪个版本」。
    /// </summary>
    private static List<World> FindWorlds()
    {
        // 版本列表可能还没初始化，先刷一次再扫
        if (AppState.Installed.Count == 0)
        {
            try { AppState.RefreshInstalled(); } catch { }
        }

        // 按「世界目录的绝对路径」去重。
        // 没开隔离的版本全都指向同一个全局 saves，所以装了几个版本，
        // 同一个世界就会被扫到几次——不去重的话列表里会出现好几行一样的。
        var found = new Dictionary<string, (string Dir, string Label)>(StringComparer.OrdinalIgnoreCase);

        void Scan(string root, string version)
        {
            try
            {
                if (!Directory.Exists(root)) return;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!File.Exists(Path.Combine(dir, "level.dat"))) continue;

                    var key = Path.GetFullPath(dir);

                    if (found.TryGetValue(key, out var exist))
                    {
                        // 已经被别的版本扫过了：只合并标签，不再加一行
                        found[key] = (exist.Dir, MergeLabel(exist.Label, version));
                        continue;
                    }

                    found[key] = (dir, version);
                }
            }
            catch { }
        }

        // 每个已安装版本都扫一遍：开了隔离的用自己目录，没开的用全局 saves。
        var scannedAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ver in AppState.Installed)
        {
            var inst = AppState.Settings.Instances.TryGetValue(ver.Id, out var i) ? i : null;

            var isIsolated = inst?.Isolated == true;

            var savesRoot = isIsolated
                ? Path.Combine(Paths.VersionDir(ver.Id), "saves")
                : Path.Combine(Paths.GameDir, "saves");

            // 没开隔离的版本用的是同一个全局 saves，标出来免得以为有几个独立存档
            var label = isIsolated ? ver.Id : ver.Id + "（共享）";
            Scan(savesRoot, label);
            scannedAll.Add(savesRoot);
        }

        // 没装版本、但存档目录里有东西的情况，兜一底
        var globalSaves = Path.Combine(Paths.GameDir, "saves");
        if (!scannedAll.Contains(globalSaves))
            Scan(globalSaves, "共享");

        var list = new List<World>();

        foreach (var (dir, label) in found.Values)
        {
            // 读存档自己记录的版本：高版本存档在低版本打开会损坏，
            // 列表上得标出来，不然用户不知道这世界还能不能用当前版本进。
            var levelDat = Path.Combine(dir, "level.dat");
            var saved = LevelDatReader.Read(levelDat)?.Version ?? "";
            if (string.IsNullOrEmpty(saved)) saved = "未知";

            list.Add(new World(Path.GetFileName(dir), dir, label, saved));
        }

        // 按名字排一下，多个版本共存时顺序才稳定
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// 合并两个版本标签。多个版本共用同一个 saves 目录时，
    /// 全都标成「共享」就够了——写成一长串版本号反而看不清。
    /// </summary>
    private static string MergeLabel(string a, string b)
    {
        if (a.Contains("共享") || b.Contains("共享")) return "共享";
        if (a == b) return a;
        return a + " / " + b;
    }

    /// <summary>
    /// 打包世界目录。跳过可执行文件和明显不是存档的东西，
    /// 免得用户往里面塞了奇怪文件被传到云端。
    /// </summary>
    private static string PackWorld(World world)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"linx-cloud-{Guid.NewGuid():N}.zip");

        using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);

        foreach (var file in Directory.GetFiles(world.Path, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(world.Path, file).Replace('\\', '/');

            // 过滤：这些不传
            if (rel.StartsWith("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("/.cache/")) continue;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".vbs"
                or ".scr" or ".msi" or ".com" or ".jar" or ".sh") continue;

            try
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var src = File.OpenRead(file);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
            catch { }
        }

        return tmp;
    }

    private static async Task<(bool ok, long size, string error)> UploadZipAsync(
        string world, string zipPath, Action<double, long, long>? onProgress = null)
    {
        try
        {
            var size = new FileInfo(zipPath).Length;

            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(zipPath);

            // 用带进度的 content，才能算速度和剩余时间
            var fileContent = new ProgressByteContent(bytes, (sent) => onProgress?.Invoke(
                size > 0 ? (double)sent / size : 0, sent, size));

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "file", $"{world}.zip");
            form.Add(new StringContent(world), "world");
            form.Add(new StringContent(AppState.Current?.Id ?? ""), "version");
            form.Add(new StringContent(""), "mods_hash");

            var url = RemoteService.BaseUrl + "/api/cloud/saves";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            var token = AppState.Settings.ServerToken;
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.Client.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            try { File.Delete(zipPath); } catch { }

            if (!resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var e = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : text;
                    return (false, size, e ?? "上传失败");
                }
                catch
                {
                    return (false, size, $"HTTP {(int)resp.StatusCode}");
                }
            }

            return (true, size, "");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>把字节数组分块写出去，每块回调一次进度。</summary>
    private sealed class ProgressByteContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly Action<long> _onSent;

        public ProgressByteContent(byte[] data, Action<long> onSent)
        {
            _data = data;
            _onSent = onSent;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            const int chunk = 64 * 1024;
            var sent = 0;

            while (sent < _data.Length)
            {
                var n = Math.Min(chunk, _data.Length - sent);
                await stream.WriteAsync(_data.AsMemory(sent, n));
                sent += n;
                _onSent(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }

    /* ==================== 进度显示 ==================== */

    private DateTime _uploadStart;
    private long _lastSent;

    private void UpdateUploadProgress(double fraction, long sent, long total)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UploadProgressPanel.Visibility = Visibility.Visible;
            UploadBar.Value = Math.Clamp(fraction * 100, 0, 100);

            if (_uploadStart == default)
            {
                _uploadStart = DateTime.Now;
                _lastSent = 0;
            }

            var elapsed = (DateTime.Now - _uploadStart).TotalSeconds;
            if (elapsed < 0.2 || sent <= _lastSent) return;

            var speed = sent / elapsed;   // 字节/秒
            var remain = speed > 0 ? (total - sent) / speed : 0;

            UploadText.Text = $"{FormatSize(sent)} / {FormatSize(total)}"
                              + $" · {FormatSize((long)speed)}/s"
                              + $" · 还剩 {FormatDuration(remain)}";

            _lastSent = sent;
        });
    }

    private void ResetUploadProgress()
    {
        _uploadStart = default;
        _lastSent = 0;

        DispatcherQueue.TryEnqueue(() =>
        {
            UploadBar.Value = 0;
            UploadText.Text = "";
            UploadProgressPanel.Visibility = Visibility.Collapsed;
        });
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "—";
        if (seconds < 60) return $"{seconds:F0} 秒";
        if (seconds < 3600) return $"{seconds / 60:F0} 分 {seconds % 60:F0} 秒";

        return $"{seconds / 3600:F1} 小时";
    }

    /* ==================== 下载 / 删除 ==================== */

    private async Task DownloadAsync(CloudSave save, bool skipConfirm = false)
    {
        if (skipConfirm)
        {
            // 调用方（从云端同步）已经详细确认过了，直接走覆盖
            await DoDownloadAsync(save, Path.Combine(Paths.GameDir, "saves", save.World), overwriteLocal: true);
            return;
        }

        // 先问清楚往哪放：覆盖本地，还是另存到别的文件夹
        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"从云端恢复「{save.World}」",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"云端这份是 {FormatSize(save.Size)}，{FormatTime(save.UpdatedAt)} 更新的。\n\n"
                       + "覆盖本地会把这个世界的本地存档换成云端的，本地所有修改都会丢失，且没法撤销。",
            },
            PrimaryButtonText = "覆盖本地",
            SecondaryButtonText = "另存为…",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        FontService.ApplyToDialog(ask);

        var choice = await ask.ShowAsync();
        if (choice == ContentDialogResult.None) return;

        string? target = null;

        if (choice == ContentDialogResult.Primary)
        {
            // 覆盖本地：写到全局 saves 下同名目录
            target = Path.Combine(Paths.GameDir, "saves", save.World);

            if (Directory.Exists(target))
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "确认覆盖",
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = $"本地已经有一个「{save.World}」：\n{target}\n\n"
                               + "它会被整个替换掉，里面的进度、建筑、箱子全部丢失。真的要覆盖吗？",
                    },
                    PrimaryButtonText = "覆盖本地存档",
                    CloseButtonText = "算了",
                    DefaultButton = ContentDialogButton.Close,
                };
                FontService.ApplyToDialog(confirm);
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
        }
        else
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            target = Path.Combine(folder.Path, save.World);
        }

        if (target is null) return;

        await DoDownloadAsync(save, target, choice == ContentDialogResult.Primary);
    }

    /// <summary>
    /// 真正下载并解压。overwriteLocal 为 true 时先删掉本地目录再解压，
    /// 避免新旧文件混在一起。
    /// </summary>
    private async Task DoDownloadAsync(CloudSave save, string target, bool overwriteLocal)
    {
        try
        {
            ResetUploadProgress();
            CloudSubtitle.Text = "正在下载…";

            var url = RemoteService.BaseUrl + $"/api/cloud/saves/{save.Id}/download";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = AppState.Settings.ServerToken;
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                CloudSubtitle.Text = "下载失败：HTTP " + (int)resp.StatusCode;
                Toast("下载失败：HTTP " + (int)resp.StatusCode, error: true);
                return;
            }

            var zipPath = Path.Combine(Path.GetTempPath(), $"linx-restore-{Guid.NewGuid():N}.zip");

            // 下载也报进度，大存档不至于干等
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using (var fs = File.Create(zipPath))
            await using (var src = await resp.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[64 * 1024];
                long got = 0;
                var started = DateTime.Now;

                while (true)
                {
                    var n = await src.ReadAsync(buffer);
                    if (n <= 0) break;

                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    got += n;

                    var elapsed = (DateTime.Now - started).TotalSeconds;
                    if (elapsed > 0.2 && total > 0)
                    {
                        var speed = got / elapsed;
                        var remain = speed > 0 ? (total - got) / speed : 0;
                        CloudSubtitle.Text = $"正在下载 {FormatSize(got)} / {FormatSize(total)}"
                                             + $" · {FormatSize((long)speed)}/s"
                                             + $" · 还剩 {FormatDuration(remain)}";
                    }
                }
            }

            // 覆盖本地时先整个删掉
            if (overwriteLocal && Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true);
            File.Delete(zipPath);

            CloudSubtitle.Text = overwriteLocal
                ? $"已用云端版本覆盖本地「{save.World}」"
                : $"已恢复到 {target}";

            Toast(overwriteLocal ? "本地存档已还原为云端版本" : "存档已解压到选择的目录");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            CloudSubtitle.Text = "下载失败：" + ex.Message;
            Toast("下载失败：" + ex.Message, error: true);
        }
    }

    private async Task DeleteAsync(CloudSave save)
    {
        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除云端存档",
            Content = $"「{save.World}」的云端副本会被删掉，没法恢复。确定吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
        };
        FontService.ApplyToDialog(ask);

        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var (ok, _, err) = await LinxAccountService.CallAsync(
                $"/api/cloud/saves/{save.Id}", null, default, "DELETE");

            if (!ok)
            {
                Toast("删除失败：" + err);
                return;
            }

            Toast("已删除");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Toast("删除失败：" + ex.Message);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    /* ==================== 工具 ==================== */

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        var mb = bytes / 1048576.0;
        return mb >= 1024 ? $"{mb / 1024:F2} GB" : mb >= 1 ? $"{mb:F1} MB" : $"{bytes / 1024.0:F0} KB";
    }

    private static string FormatTime(long ts)
    {
        if (ts <= 0) return "—";
        var d = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
        return d.ToString("yyyy-MM-dd HH:mm");
    }
}

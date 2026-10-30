using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using LinxLauncher.Models;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

/// <summary>mods 目录里的一个 .jar。禁用 = 文件名加 .disabled 后缀（Minecraft 只加载 .jar）。</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class ModItem
{
    // [Bindable] 会生成 setter 调用，init-only 属性编译不过，这里统一用 set
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public bool Enabled { get; set; }
    public string SizeText { get; set; } = "";

    public string Info => (Enabled ? "已启用 · " : "已禁用 · ") + SizeText;
    public string ToggleText => Enabled ? "禁用" : "启用";
}

public sealed partial class VersionsPage : Page
{
    private VersionManifest? _manifest;
    private bool _loading;
    private VersionSummary? _selected;
    private LoaderKind _loader = LoaderKind.Vanilla;
    private List<LoaderVersion> _loaderList = new();
    private int _filter; // 0=全部 1=正式版 2=快照
    private bool _installedOnly; // 从主页「管理版本」进来时只看已安装实例
    private bool _loadingInstance; // 填充实例设置时屏蔽保存回写

    public VersionsPage()
    {
        InitializeComponent();
        AppState.InstalledChanged += OnInstalledChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 主页「管理版本」带 installed 参数进来：只列已安装的实例
        _installedOnly = e.Parameter as string == "installed";
        FilterButton.Content = _installedOnly ? "已安装" : "全部";
        ListViewHost.Visibility = Visibility.Visible;
        DetailHost.Visibility = Visibility.Collapsed;

        if (_manifest is null && !_loading) _ = LoadManifestAsync();
        else ApplyFilter();
    }

    private void OnInstalledChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_manifest is not null) ApplyFilter();
        UpdateBadge();
    });

    // ================= 清单 =================
    private async Task LoadManifestAsync(bool force = false)
    {
        if (_loading) return;
        _loading = true;

        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        VersionList.Visibility = Visibility.Collapsed;

        try
        {
            var manifest = await AppState.Versions.GetManifestAsync(force, default);
            _manifest = manifest;
            ApplyFilter();

            // 不再自动选中「最新版」：进版本库就该停在列表，由用户自己点
        }
        catch (Exception ex)
        {
            Log.Error("Manifest", ex);
            ErrorText.Text = "版本清单获取失败，检查网络后重试。\n" + ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
            VersionList.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilter()
    {
        if (_manifest is null) return;

        // 「已安装」视图直接列本地实例（versions 目录里的全部目录），
        // 不能只从远程清单里筛：Fabric / Forge 这类实例本地才有，远程清单里根本没有。
        if (_installedOnly)
        {
            // 注意：这里不能调 AppState.RefreshInstalled()。
            // 它会触发 InstalledChanged → OnInstalledChanged → ApplyFilter，
            // 于是变成无限递归，界面直接卡死。
            var keyword0 = SearchBox.Text?.Trim();
            var locals = AppState.Installed
                .Where(v => string.IsNullOrEmpty(keyword0) || v.Id.Contains(keyword0, StringComparison.OrdinalIgnoreCase))
                .Select(v => new VersionSummary
                {
                    Id = v.Id,
                    Type = string.IsNullOrEmpty(v.BaseVersion) ? v.Type : "release",
                    ReleaseTime = v.LastWrite,
                    Installed = true
                })
                .ToList();

            UiList.Fill(VersionList, locals);
            VersionList.Visibility = Visibility.Visible;
            return;
        }

        var installed = AppState.Installed.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _manifest.Versions) v.Installed = installed.Contains(v.Id);

        IEnumerable<VersionSummary> q = _manifest.Versions;

        var keyword = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(v => v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var filter = _filter;
        if (filter == 1) q = q.Where(v => v.IsRelease);
        else if (filter == 2) q = q.Where(v => v.IsSnapshot);
        if (_installedOnly) q = q.Where(v => v.Installed);

        var list = q.ToList();
        UiList.Fill(VersionList, list);
        VersionList.Visibility = Visibility.Visible;

        if (_selected is not null)
        {
            var again = list.FirstOrDefault(v => v.Id == _selected.Id);
            if (again is not null) VersionList.SelectedItem = again;
            else { _selected = null; ShowList(); }
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchBox is null || _manifest is null) return;
        ApplyFilter();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            _filter = int.TryParse(tag, out var i) ? i : 0;
            _installedOnly = false;
            FilterButton.Content = item.Text;
        }

        if (!IsLoaded || _manifest is null) return;
        ApplyFilter();
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadManifestAsync(true);

    // ================= 选中 =================
    private async void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = VersionList.SelectedItem as VersionSummary;
        if (_selected is null) { ShowList(); return; }

        SwapView(ListViewHost, DetailHost);

        // 详情区是同一页里切视图，不会触发 Navigated，这里自己补一次字体刷新
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            FontService.RefreshAll);

        DetailId.Text = _selected.Id;
        DetailMeta.Text = $"{_selected.TypeLabel} · {_selected.ReleaseTime:yyyy 年 M 月 d 日}";

        // 实例真实类型以本地为准：Fabric / Forge 实例在远程清单里只是"release"，
        // 照清单显示会变成"正式版（原版）"，还会让人以为能再叠一个加载器。
        var localInstance = AppState.Installed.FirstOrDefault(v => v.Id == _selected.Id);
        var kind = localInstance?.DisplayKind ?? "原版";
        var isLoader = kind != "原版";

        if (isLoader)
        {
            var baseVersion = string.IsNullOrWhiteSpace(localInstance?.BaseVersion) ? "未知" : localInstance!.BaseVersion!;
            DetailMeta.Text = $"{kind} · 基于 {baseVersion} · 已含加载器，不能再叠加";

            SelectLoaderCard(kind switch
            {
                "Fabric" => CardFabric,
                "Forge" => CardForge,
                "NeoForge" => CardNeoForge,
                "Quilt" => CardQuilt,
                _ => CardVanilla
            });
        }

        foreach (var card in new[] { CardVanilla, CardFabric, CardForge, CardNeoForge, CardQuilt })
            card.IsEnabled = !isLoader;

        UpdateBadge();

        LoadInstance(_selected.Id);

        // 每次进详情都回到「原版」
        _loader = LoaderKind.Vanilla;
        SelectLoaderCard(CardVanilla);
        LoaderVersionPanel.Visibility = Visibility.Collapsed;
        UpdateSummary();

        await Task.CompletedTask;
    }

    /// <summary>
    /// 列表 ↔ 详情 的过场：旧视图快速淡出，新视图从 0.94 撑开 + 从下方 26 DIP 滑入 + 淡入。
    /// 就是微软商店点进详情时那种「先合上、再散开」的过渡。
    /// </summary>
    private void SwapView(Grid from, Grid to)
    {
        try
        {
            to.Opacity = 0;
            to.Visibility = Visibility.Visible;
            to.UpdateLayout();

            var width = to.ActualWidth;
            var height = to.ActualHeight;

            if (width <= 0 || height <= 0)
            {
                from.Visibility = Visibility.Collapsed;
                from.Opacity = 1;
                to.Opacity = 1;
                return;
            }

            var scale = new ScaleTransform
            {
                ScaleX = 0.94,
                ScaleY = 0.94,
                CenterX = width / 2,
                CenterY = height / 2
            };
            var move = new TranslateTransform { Y = 26 };
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(move);
            to.RenderTransform = group;

            from.Opacity = 1;
            from.RenderTransform = null;

            var sb = new Storyboard();

            var fadeIn = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(240))
            };
            Storyboard.SetTarget(fadeIn, to);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            sb.Children.Add(fadeIn);

            var zoomX = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(zoomX, scale);
            Storyboard.SetTargetProperty(zoomX, "ScaleX");
            sb.Children.Add(zoomX);

            var zoomY = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(zoomY, scale);
            Storyboard.SetTargetProperty(zoomY, "ScaleY");
            sb.Children.Add(zoomY);

            var slide = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, move);
            Storyboard.SetTargetProperty(slide, "Y");
            sb.Children.Add(slide);

            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeOut, from);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sb.Children.Add(fadeOut);

            sb.Completed += (_, _) =>
            {
                from.Visibility = Visibility.Collapsed;
                from.Opacity = 1;
                to.Opacity = 1;
                to.RenderTransform = null;
            };

            sb.Begin();
        }
        catch (Exception ex)
        {
            Log.Error("VersionTransition", ex);
            from.Visibility = Visibility.Collapsed;
            from.Opacity = 1;
            to.Visibility = Visibility.Visible;
            to.Opacity = 1;
        }
    }

    private void ShowList()
    {
        SwapView(DetailHost, ListViewHost);
        VersionList.SelectedItem = null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    /// <summary>把某张加载器卡片点亮，其余恢复常态。</summary>
    private void SelectLoaderCard(Button card)
    {
        var normalBg = (Brush)Application.Current.Resources["CardBackgroundBrush"];
        var normalBorder = (Brush)Application.Current.Resources["DividerBrush"];

        foreach (var b in new[] { CardVanilla, CardFabric, CardForge, CardNeoForge, CardQuilt })
        {
            b.Background = normalBg;
            b.BorderBrush = normalBorder;
        }

        card.Background = (Brush)Application.Current.Resources["BrandSoftBrush"];
        card.BorderBrush = (Brush)Application.Current.Resources["BrandBrush"];

        Bounce(card);
    }

    /// <summary>点卡片的回弹：0.96 → 1.02 → 1，只动 transform。</summary>
    private static void Bounce(FrameworkElement card)
    {
        var scale = new ScaleTransform
        {
            CenterX = card.ActualWidth / 2,
            CenterY = card.ActualHeight / 2
        };
        card.RenderTransform = scale;

        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280) };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.955 });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), Value = 1.018 });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), Value = 1.0 });

            Storyboard.SetTarget(anim, scale);
            Storyboard.SetTargetProperty(anim, prop);
            sb.Children.Add(anim);
        }

        sb.Begin();
    }

    // ================= 实例设置 =================

    private void LoadInstance(string id)
    {
        bool installed = AppState.Installed.Any(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        InstanceSectionTitle.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        InstancePanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        InstanceInfoTitle.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        InstanceInfoPanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        OpenModsButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        var localInstalled = AppState.Installed.FirstOrDefault(v => v.Id == _selected?.Id);
        CheckModUpdateButton.Visibility = installed && localInstalled?.DisplayKind != "原版"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepairButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        ModSectionTitle.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        ModPanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;

        // 面板默认是 Collapsed，页面级 Apply 扫不到里面的控件；
        // 显示出来之后单独补一次
        if (installed)
        {
            var dq = DispatcherQueue;
            if (dq is not null) dq.TryEnqueue(() => FontService.Apply(ModPanel));
            else FontService.Apply(ModPanel);
        }

        if (!installed) return;

        var s = AppState.Settings;
        if (!s.Instances.TryGetValue(id, out var inst))
        {
            inst = new InstanceSettings();
            s.Instances[id] = inst;
        }

        _loadingInstance = true;
        MemoryBox.Value = inst.MemoryMb ?? s.MemoryMb;
        WidthBox.Value = inst.Width ?? s.GameWidth;
        HeightBox.Value = inst.Height ?? s.GameHeight;
        FullscreenSwitch.IsOn = inst.Fullscreen ?? s.Fullscreen;
        IsolatedSwitch.IsOn = inst.Isolated;
        _loadingInstance = false;

        // 实例信息
        SizeValue.Text = GameInstaller.FormatSize(DirSize(Paths.VersionDir(id)));

        var modsDir = inst.Isolated ? Path.Combine(Paths.VersionDir(id), "mods") : Paths.ModsDir;
        ModCountValue.Text = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar").Length + " 个"
            : "0 个";

        var savesDir = inst.Isolated ? Path.Combine(Paths.VersionDir(id), "saves") : Path.Combine(Paths.GameDir, "saves");
        SavesValue.Text = Directory.Exists(savesDir)
            ? Directory.GetDirectories(savesDir).Length + " 个"
            : "0 个";

        RefreshMods();
    }

    // ================= Mod 管理 =================

    private string CurrentModsDir()
    {
        if (_selected is null) return Paths.ModsDir;

        var inst = AppState.Settings.Instances.TryGetValue(_selected.Id, out var i) ? i : null;
        return inst?.Isolated == true
            ? Path.Combine(Paths.VersionDir(_selected.Id), "mods")
            : Paths.ModsDir;
    }

    private void RefreshMods()
    {
        var dir = CurrentModsDir();
        try { Directory.CreateDirectory(dir); } catch { }

        var list = new List<ModItem>();

        foreach (var file in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(file);
            bool enabled = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            bool disabled = name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
            if (!enabled && !disabled) continue;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }

            list.Add(new ModItem
            {
                Name = enabled ? name : name[..^".disabled".Length],
                File = file,
                Enabled = enabled,
                SizeText = GameInstaller.FormatSize(size)
            });
        }

        UiList.Fill(ModList, list);
        ModEmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModCountValue.Text = list.Count(m => m.Enabled) + " 个";

        // 列表项是动态生成的，容器要到这一帧之后才存在，
        // 页面级的 Apply 扫不到它们，这里补一次
        var dq = DispatcherQueue;
        if (dq is not null) dq.TryEnqueue(() => FontService.Apply(ModPanel));
        else FontService.Apply(ModPanel);
    }

    private void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModItem item) return;

        try
        {
            if (item.Enabled) File.Move(item.File, item.File + ".disabled", true);
            else File.Move(item.File, item.File[..^".disabled".Length], true);
        }
        catch (Exception ex)
        {
            Log.Error("ModToggle", ex);
            MainWindow.Instance?.Toast("改不动这个文件：" + ex.Message.Split('\n')[0], true);
        }

        RefreshMods();
    }

    private void ModDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModItem item) return;

        try { File.Delete(item.File); }
        catch (Exception ex) { Log.Error("ModDelete", ex); }

        RefreshMods();
    }

    private void Mods_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "安装到这个实例";
        }
    }

    private async void Mods_Drop(object sender, DragEventArgs e)
    {
        // 拖放事件一返回，e.DataView 就作废了。
        // 原来在 await 之后才读 GetStorageItemsAsync()，会抛
        // 「Operation is not valid due to the current state of the object」，
        // 表现是拖进去的 jar 一个都没复制、还弹安装失败。用 Deferral 撑住。
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            var dir = CurrentModsDir();

            Directory.CreateDirectory(dir);

            var copied = 0;
            var skippedDir = 0;
            foreach (var item in items)
            {
                if (item is StorageFile file &&
                    file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file.Path, Path.Combine(dir, file.Name), true);
                    copied++;
                }
            }

            RefreshMods();
            if (copied > 0)
                MainWindow.Instance?.Toast($"已放入 {copied} 个 Mod");
            else if (skippedDir > 0)
                MainWindow.Instance?.Toast("拖的是文件夹：请把 .jar 文件本身拖进来");
            else
                MainWindow.Instance?.Toast("没找到 .jar 文件");
        }
        catch (Exception ex)
        {
            Log.Error("ModDrop", ex);
            MainWindow.Instance?.Toast("安装 Mod 失败：" + ex.Message.Split('\n')[0], true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveInstance();

    private void Toggle_Changed(object sender, RoutedEventArgs e) => SaveInstance();

    private void SaveInstance()
    {
        if (_loadingInstance || _selected is null) return;

        var s = AppState.Settings;
        if (!s.Instances.TryGetValue(_selected.Id, out var inst))
        {
            inst = new InstanceSettings();
            s.Instances[_selected.Id] = inst;
        }

        if (!double.IsNaN(MemoryBox.Value)) inst.MemoryMb = (int)MemoryBox.Value;
        if (!double.IsNaN(WidthBox.Value)) inst.Width = (int)WidthBox.Value;
        if (!double.IsNaN(HeightBox.Value)) inst.Height = (int)HeightBox.Value;
        inst.Fullscreen = FullscreenSwitch.IsOn;
        inst.Isolated = IsolatedSwitch.IsOn;

        SettingsService.Save();

        // 隔离开关会影响 mods / saves 的位置，顺手刷新统计
        if (_selected is not null) LoadInstance(_selected.Id);
    }

    /// <summary>
    /// 检查 Mod 更新：拿 mods 里每个 jar 的 sha1 去 Modrinth 反查，
    /// 对得上某个已发布版本、且该项目最新版文件又不是这一份 → 就算有更新。
    /// </summary>
    private async void CheckModUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var versionId = _selected.Id;
        var inst = AppState.Settings.Instances.TryGetValue(versionId, out var i) ? i : null;
        var modsDir = inst?.Isolated == true
            ? Path.Combine(Paths.VersionDir(versionId), "mods")
            : Paths.ModsDir;

        var local = AppState.Installed.FirstOrDefault(v => v.Id == versionId);
        var loader = ModrinthService.LoaderTag(local?.DisplayKind);
        var gameVersion = string.IsNullOrWhiteSpace(local?.BaseVersion) ? versionId : local!.BaseVersion;

        CheckModUpdateButton.IsEnabled = false;
        try
        {
            MainWindow.Instance?.Toast("正在检查 Mod 更新…");

            var updates = await Task.Run(() =>
                ModrinthService.CheckUpdatesAsync(modsDir, loader, gameVersion, default));

            if (updates.Count == 0)
            {
                MainWindow.Instance?.Toast("Mod 都是最新的");
                return;
            }

            var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
            panel.Children.Add(new TextBlock
            {
                Text = $"有 {updates.Count} 个 Mod 可以更新：",
                FontSize = 13
            });

            foreach (var u in updates)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"· {(string.IsNullOrEmpty(u.Title) ? u.LocalName : u.Title)}  →  {u.NewVersion}",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
                });
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Mod 更新",
                Content = panel,
                PrimaryButtonText = "全部更新",
                CloseButtonText = "稍后再说",
                DefaultButton = ContentDialogButton.Primary
            };
            FontService.ApplyToDialog(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            int ok = 0;
            foreach (var u in updates)
            {
                try
                {
                    var dest = Path.Combine(modsDir, u.NewFileName);
                    await Http.DownloadToFileAsync(u.NewUrl, dest, null, default);

                    // 文件名通常带版本号，装完把旧的那份删掉
                    if (!string.Equals(u.LocalFile, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(u.LocalFile))
                        File.Delete(u.LocalFile);

                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"更新 {u.LocalName} 失败：{ex.Message}");
                }
            }

            MainWindow.Instance?.Toast(ok == updates.Count
                ? $"已更新 {ok} 个 Mod"
                : $"更新完成 {ok} / {updates.Count}，其余失败了，细节看日志");
        }
        catch (Exception ex)
        {
            Log.Error("ModUpdate", ex);
            MainWindow.Instance?.Toast("检查更新失败：" + ex.Message.Split('\n')[0], true);
        }
        finally
        {
            CheckModUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>删除已安装的版本：只删这个版本的目录和它的实例配置，存档 / mods 不动。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var versionId = _selected.Id;
        var dir = Paths.VersionDir(versionId);

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除 {versionId}？",
            Content = "会删掉这个版本的整个目录（版本 JSON、客户端 jar、natives），删了就找不回来了。\n"
                      + "存档、mods、资源库是共用的，不会被删。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        FontService.ApplyToDialog(confirm);

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            });

            if (AppState.Settings.Instances.Remove(versionId))
                SettingsService.Save();

            AppState.RefreshInstalled();
            ShowList();
            MainWindow.Instance?.Toast($"已删除 {versionId}");
        }
        catch (Exception ex)
        {
            Log.Error("DeleteVersion", ex);
            MainWindow.Instance?.Toast("删除失败：" + ex.Message.Split('\n')[0], true);
        }
    }

    /// <summary>
    /// 校验并修复：重扫这个版本的客户端 / 依赖库 / 资源对象，缺哪个补哪个。
    /// 主菜单灰底、界面英文就是资源对象缺了一大片导致的。
    /// </summary>
    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var versionId = _selected.Id;
        RepairButton.IsEnabled = false;

        var job = AppState.Downloads.Begin($"{versionId} · 校验文件");
        var dialog = new Controls.DownloadProgressDialog(job) { XamlRoot = XamlRoot };
        _ = dialog.ShowAsync();

        try
        {
            var progress = new Progress<InstallProgress>(p =>
                DispatcherQueue.TryEnqueue(() => job.Apply(p)));

            var fixedCount = await Task.Run(() =>
                AppState.Installer.RepairAsync(versionId, progress, job.Cts.Token));

            AppState.Downloads.Finish(job, true);
            MainWindow.Instance?.Toast(fixedCount == 0
                ? "文件是完整的，不用修"
                : $"已补全 {fixedCount} 个缺失文件，再启动就是中文和正常背景了");
        }
        catch (Exception ex)
        {
            Log.Error("Repair", ex);
            AppState.Downloads.Finish(job, false, ex.Message);
            MainWindow.Instance?.Toast("修复失败：" + ex.Message.Split('\n')[0], true);
        }
        finally
        {
            RepairButton.IsEnabled = true;
        }
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var inst = AppState.Settings.Instances.TryGetValue(_selected.Id, out var i) ? i : new InstanceSettings();
        var dir = inst.Isolated ? Path.Combine(Paths.VersionDir(_selected.Id), "mods") : Paths.ModsDir;

        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("OpenMods", ex);
        }
    }

    private static long DirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    private void UpdateBadge()
    {
        if (_selected is null) return;
        bool installed = AppState.Installed.Any(v => v.Id.Equals(_selected.Id, StringComparison.OrdinalIgnoreCase));
        InstalledBadge.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Content = installed ? "重新安装" : "下载并安装";
    }

    // ================= 加载器 =================
    private async void LoaderCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button card || card.Tag is not string tag) return;

        SelectLoaderCard(card);

        if (!IsLoaded || _selected is null) return;

        _loader = tag switch
        {
            "forge" => LoaderKind.Forge,
            "fabric" => LoaderKind.Fabric,
            "neoforge" => LoaderKind.NeoForge,
            "quilt" => LoaderKind.Quilt,
            _ => LoaderKind.Vanilla
        };

        await RefreshLoaderVersionsAsync();
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        if (_loader == LoaderKind.Vanilla)
        {
            LoaderVersionPanel.Visibility = Visibility.Collapsed;
            UpdateSummary();
            return;
        }

        // 入场动画：从下方 12 DIP 淡入到位（TranslationTransition 自动补间）
        LoaderVersionPanel.Translation = new Vector3(0, 12, 0);
        LoaderVersionPanel.Opacity = 0;
        LoaderVersionPanel.Visibility = Visibility.Visible;
        LoaderVersionPanel.Translation = Vector3.Zero;
        LoaderVersionPanel.Opacity = 1;

        LoaderRing.Visibility = Visibility.Visible;
        LoaderRing.IsActive = true;
        LoaderHint.Visibility = Visibility.Collapsed;
        UiList.Clear(LoaderVersionBox);
        _loaderList.Clear();

        try
        {
            var list = await AppState.Loaders.GetAvailableAsync(_loader, _selected!.Id, default);
            _loaderList = list;
            UiList.Fill(LoaderVersionBox, list);
            LoaderVersionBox.DisplayMemberPath = "Name";

            if (list.Count > 0)
            {
                LoaderVersionBox.SelectedItem = list.FirstOrDefault(l => l.IsStable) ?? list[0];
            }
            else
            {
                LoaderHint.Text = $"该版本暂时没有可用的 {KindName(_loader)} 构建，换个加载器试试。";
                LoaderHint.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Loader {_loader}", ex);
            LoaderHint.Text = $"{KindName(_loader)} 版本列表获取失败：{ex.Message}";
            LoaderHint.Visibility = Visibility.Visible;
        }
        finally
        {
            LoaderRing.IsActive = false;
            LoaderRing.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }

    private void LoaderVersion_Changed(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        if (_selected is null) return;

        var ver = (LoaderVersionBox.SelectedItem as LoaderVersion)?.Name;

        if (_loader == LoaderKind.Vanilla)
        {
            InstallSummary.Text = $"将安装 {_selected.Id} 原版";
            InstallHint.Text = "客户端 / 依赖库 / 资源文件，首次约 300–600 MB";
        }
        else
        {
            InstallSummary.Text = string.IsNullOrEmpty(ver)
                ? $"将安装 {_selected.Id} + {KindName(_loader)}"
                : $"将安装 {_selected.Id} + {KindName(_loader)} {ver}";
            InstallHint.Text = _loader is LoaderKind.Forge or LoaderKind.NeoForge
                ? "会先补齐原版，再调用官方安装器补全依赖"
                : "会先补齐原版，再下载加载器依赖库";
        }

        bool needJava = _loader is LoaderKind.Forge or LoaderKind.NeoForge && !JavaService.Detect().Any();
        WarnBar.IsOpen = needJava;
        if (needJava)
            WarnBar.Message = "未检测到 Java，Forge/NeoForge 安装器需要 Java 才能运行。";
    }

    private static string KindName(LoaderKind k) => k switch
    {
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        _ => "原版"
    };

    // ================= 安装 =================
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var game = _selected.Id;
        var loader = _loader;
        var loaderVersion = (LoaderVersionBox.SelectedItem as LoaderVersion)?.Name;
        var loaderUrl = (LoaderVersionBox.SelectedItem as LoaderVersion)?.InstallerUrl;

        if (loader != LoaderKind.Vanilla && string.IsNullOrEmpty(loaderVersion))
        {
            MainWindow.Instance?.Toast("还没选好加载器版本", true);
            return;
        }

        var title = loader == LoaderKind.Vanilla ? game : $"{game} + {KindName(loader)} {loaderVersion}";
        var job = AppState.Downloads.Begin(title);

        // 应用内对话框（和「离线账号」同一个 ContentDialog 样式）；点「后台运行」关掉，安装继续。
        var dialog = new Controls.DownloadProgressDialog(job) { XamlRoot = XamlRoot };
        _ = dialog.ShowAsync();

        InstallButton.IsEnabled = false;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
                DispatcherQueue.TryEnqueue(() => job.Apply(p)));

            job.Apply(new InstallProgress { Stage = "准备", Indeterminate = true, Message = "开始…" });

            await AppState.Installer.InstallVanillaAsync(game, progress, job.Cts.Token);

            string finalId = game;
            if (loader != LoaderKind.Vanilla)
            {
                if (loader is LoaderKind.Fabric or LoaderKind.Quilt)
                    finalId = await AppState.Loaders.InstallFabricLikeAsync(loader, game, loaderVersion!, progress, job.Cts.Token);
                else
                    finalId = await AppState.Loaders.InstallForgeLikeAsync(loader, game, loaderVersion!, loaderUrl, progress, job.Cts.Token);
            }

            AppState.SelectedVersion = finalId;
            AppState.Settings.LastVersionId = finalId;
            SettingsService.Save();
            AppState.RefreshInstalled();

            AppState.Downloads.Finish(job, true);
            MainWindow.Instance?.Toast($"已安装 {finalId}");
            UpdateBadge();
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            AppState.Downloads.Finish(job, false, ex.Message);
            MainWindow.Instance?.Toast("安装失败：" + FirstLine(ex.Message), true);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault()?.Trim() ?? s;
        return line.Length > 100 ? line[..100] + "…" : line;
    }
}

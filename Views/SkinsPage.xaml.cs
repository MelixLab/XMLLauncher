using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace LinxLauncher.Views;

/// <summary>
/// 皮肤页：头像 + 正/背全身预览 + 导入 / 同步 / 上传 / 模型切换。
/// 所有预览都由 <see cref="SkinService"/> 从皮肤 PNG 现场渲染；没设皮肤时用内置的 Steve / Alex。
/// </summary>
public sealed partial class SkinsPage : Page
{

    // 3D 预览：当前旋转角度 + 拖动状态
    private double _yaw = 24;
    private double _pitch = -8;
    private bool _dragging;
    private long _lastRenderTick;
    private double _dragX, _dragY;
    private SkinRenderer3D.SkinImage? _skinPixels;
    private string? _renderedSkinPath;
    private string? _accountId;
    private bool _loading;

    public SkinsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_accountId is null) _accountId = AccountService.Current?.Id;
            BuildPicker();
            Refresh();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _accountId = e.Parameter as string ?? AccountService.Current?.Id;
    }

    // ================= 账户选择 =================

    private void BuildPicker()
    {
        _loading = true;
        AccountPicker.Items.Clear();

        foreach (var acc in AccountService.Store.Accounts)
        {
            AccountPicker.Items.Add(new ComboBoxItem
            {
                Content = $"{acc.Name} · {acc.KindText}",
                Tag = acc.Id
            });
        }

        var index = 0;
        for (var i = 0; i < AccountPicker.Items.Count; i++)
        {
            if (AccountPicker.Items[i] is ComboBoxItem item && (string?)item.Tag == _accountId)
            {
                index = i;
                break;
            }
        }

        if (AccountPicker.Items.Count > 0) AccountPicker.SelectedIndex = index;
        _loading = false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("accounts");

    private void AccountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AccountPicker.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _accountId = id;
            Refresh();
        }
    }

    // ================= 刷新 =================

    private GameAccount? Account =>
        AccountService.Store.FindById(_accountId) ?? AccountService.Current;

    private void Refresh()
    {
        var acc = Account;
        if (acc is null) return;

        _loading = true;
        ModelChoice.SelectedIndex = acc.SkinModel == "slim" ? 1 : 0;
        _loading = false;

        var defaultName = acc.SkinModel == "slim" ? "Alex" : "Steve";

        PreviewName.Text = acc.Name;
        PreviewMeta.Text = acc.IsMicrosoft
            ? $"正版账户 · {acc.ModelText}"
            : $"离线账户 · {acc.ModelText}";

        PreviewSource.Text = acc.HasSkin
            ? (acc.SkinSource == "mojang" ? "皮肤来自 Mojang 角色档案" : "皮肤来自本地导入")
            : $"默认皮肤 · {defaultName}";

        DefaultHint.Text = acc.HasSkin ? "" : $"未设置皮肤，正在用默认的 {defaultName}";

        EmptyHint.Text = acc.HasSkin
            ? ""
            : $"现在显示的是默认皮肤（{defaultName}）。导入一张 64×64 的 PNG，或者从正版角色档案同步，就会换成你的皮肤。";

        AvatarInitial.Text = string.IsNullOrWhiteSpace(acc.Name) ? "S" : acc.Name.Trim()[..1].ToUpperInvariant();

        _ = RenderAsync(acc);

        SyncButton.IsEnabled = acc.IsMicrosoft && !string.IsNullOrEmpty(acc.AccessToken);
        UploadButton.IsEnabled = acc.IsMicrosoft && acc.HasSkin;
        ClearButton.IsEnabled = acc.HasSkin;

        StatusText.Text = acc.IsMicrosoft
            ? "从角色档案同步会拉 Mojang 上这张皮肤；上传会把当前皮肤（按上面的模型）写回你的正版账号，重启游戏后生效。"
            : "离线账户的皮肤只在本机生效：启动器里的头像和预览会跟着变；装了 CustomSkinLoader 的整合包，换皮肤时会顺手写进 CustomSkinLoader/LocalSkin/skin/名字.png，进游戏也能看到。服务器若开着在线验证，别人的客户端仍然看到默认皮肤。";

        // 和账户页一样：页面是导航后才真正布局的，补刷一次字体
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FontService.Apply(this));
    }

    private async Task RenderAsync(GameAccount acc)
    {
        var path = acc.SkinPath;

        var avatar = await SkinService.RenderAvatarAsync(path, 256, acc.SkinModel);
        if (avatar is null)
        {
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarInitial.Visibility = Visibility.Visible;
        }
        else
        {
            AvatarImage.Source = avatar;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarInitial.Visibility = Visibility.Collapsed;
        }

        // 正面/背面两张小图换成可拖动的 3D 模型
        await LoadSkinModelAsync(path, acc.SkinModel);
    }

    // ================= 操作 =================

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null) return;

        var path = await PickPngAsync();
        if (path is null) return;

        ImportButton.IsEnabled = false;
        try
        {
            var error = await SkinService.ImportAsync(acc, path, default);
            if (error is not null)
            {
                MainWindow.Instance?.Toast("导入失败：" + error, true);
                return;
            }

            AccountService.Save(acc);
            AfterSkinChanged(acc);
            MainWindow.Instance?.Toast($"已给「{acc.Name}」换上这张皮肤");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null) return;

        SyncButton.IsEnabled = false;
        StatusText.Text = "正在读取角色档案…";
        try
        {
            var error = await SkinService.SyncFromOfficialAsync(acc, default);
            if (error is not null)
            {
                StatusText.Text = error;
                MainWindow.Instance?.Toast(error, true);
                return;
            }

            AccountService.Save(acc);
            AfterSkinChanged(acc);
            MainWindow.Instance?.Toast($"已同步「{acc.Name}」的正版皮肤");
        }
        finally
        {
            SyncButton.IsEnabled = true;
            Refresh();
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc?.IsMicrosoft != true || !acc.HasSkin) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "上传成正版皮肤？",
            Content = new TextBlock
            {
                Text = $"会把「{acc.Name}」当前的皮肤以 {acc.ModelText} 上传到 Mojang，替换这个账号现在的皮肤。其他人在游戏里也会看到。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "上传",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        FontService.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        UploadButton.IsEnabled = false;
        StatusText.Text = "正在上传…";
        try
        {
            var error = await SkinService.UploadToOfficialAsync(acc, default);
            StatusText.Text = error ?? "上传成功，重启游戏后生效。";
            if (error is null)
            {
                acc.SkinSource = "mojang";
                AccountService.Save(acc);
                MainWindow.Instance?.Toast("皮肤已上传到正版账号");
            }
            else
            {
                MainWindow.Instance?.Toast(error, true);
            }
        }
        finally
        {
            UploadButton.IsEnabled = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null || !acc.HasSkin) return;

        SkinService.ClearSkin(acc);
        AccountService.Save(acc);
        AfterSkinChanged(acc);
        MainWindow.Instance?.Toast($"「{acc.Name}」已恢复默认皮肤");
    }

    private void ModelChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var acc = Account;
        if (acc is null) return;

        var model = ModelChoice.SelectedIndex == 1 ? "slim" : "classic";
        if (acc.SkinModel == model) return;

        acc.SkinModel = model;
        AccountService.Save(acc);
        Refresh();
    }

    private void AfterSkinChanged(GameAccount acc)
    {
        if (acc.Id == AccountService.Current?.Id)
            MainWindow.Instance?.RefreshAccountUi();

        Refresh();
    }

    // ================= 工具 =================

    private async Task<string?> PickPngAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            if (MainWindow.Instance is not null)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));

            picker.FileTypeFilter.Add(".png");
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Log.Warn("选择皮肤失败：" + ex.Message);
            MainWindow.Instance?.Toast("打开文件选择器失败：" + ex.Message, true);
            return null;
        }
    }

    /* ==================== 3D 皮肤预览 ==================== */

    private void SkinModel_Pressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        var p = e.GetCurrentPoint(SkinModelHost);
        _dragX = p.Position.X;
        _dragY = p.Position.Y;
        SkinModelHost.CapturePointer(e.Pointer);
        if (SkinModelHint is not null) SkinModelHint.Visibility = Visibility.Collapsed;
    }

    private void SkinModel_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var p = e.GetCurrentPoint(SkinModelHost);
        var dx = p.Position.X - _dragX;
        var dy = p.Position.Y - _dragY;
        _dragX = p.Position.X;
        _dragY = p.Position.Y;

        _yaw += dx * 0.6;
        _pitch = Math.Clamp(_pitch + dy * 0.35, -35, 35);

        // 节流：软件渲染一帧要几十毫秒，指针事件比这密得多，
        // 每帧都渲染会排长队，表现就是拖一下停一下。
        var now = Environment.TickCount64;
        if (now - _lastRenderTick < 40) return;
        _lastRenderTick = now;

        _ = RenderSkinModelAsync();
    }

    private void SkinModel_Released(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        SkinModelHost.ReleasePointerCapture(e.Pointer);

        // 松手后补一张全分辨率的
        _ = RenderSkinModelAsync();
    }

    /// <summary>按当前角度重画一帧 3D。</summary>
    private async Task RenderSkinModelAsync()
    {
        if (_skinPixels is null) return;

        // 不降分辨率：之前拖动时按 0.55 渲染，再放大显示就是马赛克。
        // 卡顿改用节流解决（见 SkinModel_Moved）。
        try
        {
            var w = (int)Math.Max(180, SkinModelHost.ActualWidth);
            var h = (int)Math.Max(240, SkinModelHost.ActualHeight);

            SkinModel3D.Source = await SkinRenderer3D.RenderAsync(_skinPixels, w, h, _yaw, _pitch);
        }
        catch (Exception ex)
        {
            Log.Warn("3D 皮肤渲染失败：" + ex.Message);
        }
    }

    /// <summary>皮肤换了之后重新读像素并重画。</summary>
    private async Task LoadSkinModelAsync(string? skinPath, string model)
    {
        if (_renderedSkinPath == skinPath && _skinPixels is not null)
        {
            await RenderSkinModelAsync();
            return;
        }

        _skinPixels = await SkinService.LoadSkinImageAsync(skinPath, model);
        _renderedSkinPath = skinPath;

        if (_skinPixels is null)
        {
            SkinModel3D.Source = null;
            if (SkinModelHint is not null)
            {
                SkinModelHint.Text = "这个账户还没有皮肤";
                SkinModelHint.Visibility = Visibility.Visible;
            }
            return;
        }

        await RenderSkinModelAsync();
    }

}

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace LinxLauncher.Views;

/// <summary>
/// 皮肤页：头像 + 可旋转 3D 预览 + 导入 / 同步 / 上传 / 模型切换。
/// 所有预览都由 <see cref="SkinService"/> 从皮肤 PNG 现场渲染；没设皮肤时用内置的 Steve / Alex。
/// </summary>
public sealed partial class SkinsPage : Page
{
    private string? _accountId;
    private bool _loading;
    private int _renderVersion;
    private bool _operationBusy;
    private bool _previewActive;
    private double _yaw = 24;
    private double _pitch = -8;
    private uint? _dragPointerId;
    private double _dragX, _dragY;
    private SkinRenderer3D.SkinImage? _skinPixels;
    private CancellationTokenSource? _previewCancellation;
    private bool _frameRequested;
    private bool _frameWorkerRunning;
    private int _frameVersion;
    private long _lastRenderTick;

    private void PageContent_SizeChanged(object sender, SizeChangedEventArgs e)
        => PageContent.Padding = new Thickness(e.NewSize.Width < 720 ? 16 : 32, 28, e.NewSize.Width < 720 ? 16 : 32, 24);

    private void AccountRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 480;
        Grid.SetRow(AccountPicker, narrow ? 1 : 0);
        Grid.SetColumn(AccountPicker, narrow ? 0 : 1);
        Grid.SetColumnSpan(AccountPicker, narrow ? 3 : 1);
        Grid.SetRow(DefaultHint, narrow ? 2 : 1);
        Grid.SetColumn(DefaultHint, 0);
        Grid.SetColumnSpan(DefaultHint, 3);
    }

    private void SkinContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 600;
        PreviewColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(e.NewSize.Width < 900 ? 280 : 320);
        Grid.SetColumnSpan(PreviewCard, narrow ? 2 : 1);
        Grid.SetColumn(EditingCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(EditingCard, narrow ? 2 : 1);
        Grid.SetRow(EditingCard, narrow ? 1 : 0);
        SkinContent.ColumnSpacing = narrow ? 0 : 16;
    }

    public SkinsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopPreview();
        Loaded += (_, _) =>
        {
            _previewActive = true;
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopPreview();
        base.OnNavigatedFrom(e);
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

        if (AccountPicker.Items.Count > 0)
        {
            AccountPicker.SelectedIndex = index;
            _accountId = (AccountPicker.Items[index] as ComboBoxItem)?.Tag as string;
        }
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
        ResetPreview();
        var acc = Account;
        if (acc is null)
        {
            UpdateActionState();
            return;
        }

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
        DefaultHint.Visibility = acc.HasSkin ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = acc.HasSkin ? Visibility.Collapsed : Visibility.Visible;

        EmptyHint.Text = acc.HasSkin
            ? ""
            : $"现在显示的是默认皮肤（{defaultName}）。导入一张 64×64 的 PNG，或者从正版角色档案同步，就会换成你的皮肤。";

        AvatarInitial.Text = string.IsNullOrWhiteSpace(acc.Name) ? "S" : acc.Name.Trim()[..1].ToUpperInvariant();

        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarInitial.Visibility = Visibility.Visible;
        _ = RenderAsync(acc, _renderVersion);

        UpdateActionState();

        StatusText.Text = acc.IsMicrosoft
            ? "从角色档案同步会拉 Mojang 上这张皮肤；上传会把当前皮肤（按上面的模型）写回你的正版账号，重启游戏后生效。"
            : "离线账户的皮肤只在本机生效：启动器里的头像和预览会跟着变；装了 CustomSkinLoader 的整合包，换皮肤时会顺手写进 CustomSkinLoader/LocalSkin/skin/名字.png，进游戏也能看到。服务器若开着在线验证，别人的客户端仍然看到默认皮肤。";

        // 和账户页一样：页面是导航后才真正布局的，补刷一次字体
        var version = _renderVersion;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_previewActive && version == _renderVersion && IsViewing(acc)) FontService.Apply(this);
        });
    }

    private void UpdateActionState()
    {
        var acc = Account;
        ImportButton.IsEnabled = !_operationBusy && acc is not null;
        SyncButton.IsEnabled = !_operationBusy && acc?.IsMicrosoft == true && !string.IsNullOrEmpty(acc.AccessToken);
        UploadButton.IsEnabled = !_operationBusy && acc?.IsMicrosoft == true && acc.HasSkin;
        ClearButton.IsEnabled = !_operationBusy && acc?.HasSkin == true;
        ModelChoice.IsEnabled = !_operationBusy && acc is not null;
    }

    private bool IsViewing(GameAccount acc) => _previewActive && IsLoaded && Account?.Id == acc.Id;

    private async Task RenderAsync(GameAccount acc, int version)
    {
        var path = acc.SkinPath;
        var model = acc.SkinModel;

        var avatar = await SkinService.RenderAvatarAsync(path, 256, model);
        if (version != _renderVersion || !IsViewing(acc)) return;
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

        // 每次刷新重新加载；路径相同也可能已覆盖文件，或切换了 Classic / Slim。
        var skin = await SkinService.LoadSkinImageAsync(path, model);
        if (version != _renderVersion || !IsViewing(acc)) return;
        _skinPixels = skin;
        SkinModelHint.Text = skin is null ? "无法读取皮肤预览" : "按住拖动可以转一圈";
        SkinModelHint.Visibility = Visibility.Visible;
        RequestFrame();
    }

    // ================= 操作 =================

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null || _operationBusy) return;
        _operationBusy = true;
        UpdateActionState();
        try
        {
            var path = await PickPngAsync();
            if (path is null) return;
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
            _operationBusy = false;
            UpdateActionState();
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null || _operationBusy) return;

        _operationBusy = true;
        UpdateActionState();
        StatusText.Text = "正在读取角色档案…";
        try
        {
            var error = await SkinService.SyncFromOfficialAsync(acc, default);
            if (error is not null)
            {
                if (IsViewing(acc)) StatusText.Text = error;
                MainWindow.Instance?.Toast(error, true);
                return;
            }

            AccountService.Save(acc);
            AfterSkinChanged(acc);
            MainWindow.Instance?.Toast($"已同步「{acc.Name}」的正版皮肤");
        }
        finally
        {
            _operationBusy = false;
            UpdateActionState();
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc?.IsMicrosoft != true || !acc.HasSkin || _operationBusy) return;

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
        if (_operationBusy) return;

        _operationBusy = true;
        UpdateActionState();
        if (IsViewing(acc)) StatusText.Text = "正在上传…";
        try
        {
            var error = await SkinService.UploadToOfficialAsync(acc, default);
            if (IsViewing(acc)) StatusText.Text = error ?? "上传成功，重启游戏后生效。";
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
            _operationBusy = false;
            UpdateActionState();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var acc = Account;
        if (acc is null || !acc.HasSkin || _operationBusy) return;

        SkinService.ClearSkin(acc);
        AccountService.Save(acc);
        AfterSkinChanged(acc);
        MainWindow.Instance?.Toast($"「{acc.Name}」已恢复默认皮肤");
    }

    private void ModelChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _operationBusy) return;

        var acc = Account;
        if (acc is null) return;

        var model = ModelChoice.SelectedIndex == 1 ? "slim" : "classic";
        if (acc.SkinModel == model) return;

        acc.SkinModel = model;
        AccountService.Save(acc);
        if (IsViewing(acc)) Refresh();
    }

    private void AfterSkinChanged(GameAccount acc)
    {
        if (acc.Id == AccountService.Current?.Id)
            MainWindow.Instance?.RefreshAccountUi();

        if (IsViewing(acc)) Refresh();
    }

    // ================= 3D 预览 =================

    private void StopPreview()
    {
        _previewActive = false;
        ResetPreview();
    }

    private void ResetPreview()
    {
        ++_renderVersion;
        ++_frameVersion;
        _frameRequested = false;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = _previewActive ? new CancellationTokenSource() : null;
        // 先清掉状态，ReleasePointerCaptures 引发的 CaptureLost 不再排队。
        _dragPointerId = null;
        SkinModelHost.ReleasePointerCaptures();
        _skinPixels = null;
        SkinModel3D.Source = null;
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarInitial.Visibility = Visibility.Visible;
    }

    private void SkinModelHost_SizeChanged(object sender, SizeChangedEventArgs e) => RequestFrame();

    private void SkinModel_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewActive || _skinPixels is null || _dragPointerId is not null) return;
        var point = e.GetCurrentPoint(SkinModelHost);
        if (!point.Properties.IsLeftButtonPressed || !SkinModelHost.CapturePointer(e.Pointer)) return;
        _dragPointerId = e.Pointer.PointerId;
        _dragX = point.Position.X;
        _dragY = point.Position.Y;
        SkinModelHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void SkinModel_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(SkinModelHost);
        _yaw = (_yaw + (point.Position.X - _dragX) * 0.6) % 360;
        _pitch = Math.Clamp(_pitch + (point.Position.Y - _dragY) * 0.35, -35, 35);
        _dragX = point.Position.X;
        _dragY = point.Position.Y;
        RequestFrame();
        e.Handled = true;
    }

    private void SkinModel_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId) return;
        // 松手事件可能携带最后一次 Moved 之后的位置，先合入最终角度。
        SkinModel_Moved(sender, e);
        _dragPointerId = null;
        SkinModelHost.ReleasePointerCapture(e.Pointer);
        RequestFrame();
        e.Handled = true;
    }

    private void RequestFrame()
    {
        if (!_previewActive || !IsLoaded || _skinPixels is null) return;
        ++_frameVersion;
        _frameRequested = true;
        if (!_frameWorkerRunning) _ = RenderFramesAsync();
    }

    private async Task RenderFramesAsync()
    {
        _frameWorkerRunning = true;
        try
        {
            // 单一循环合并拖动/缩放请求；不为每次指针事件创建渲染任务。
            while (_frameRequested && _previewActive && IsLoaded && _skinPixels is not null)
            {
                var token = _previewCancellation!.Token;
                var delay = 40 - (Environment.TickCount64 - _lastRenderTick);
                if (delay > 0) await Task.Delay((int)delay, token);
                token.ThrowIfCancellationRequested();

                var skin = _skinPixels;
                if (skin is null) break;
                var version = _renderVersion;
                var frameVersion = _frameVersion;
                var accountId = Account?.Id;
                var width = (int)Math.Max(1, Math.Ceiling(SkinModelHost.ActualWidth));
                var height = (int)Math.Max(1, Math.Ceiling(SkinModelHost.ActualHeight));
                var yaw = _yaw;
                var pitch = _pitch;
                _frameRequested = false;
                _lastRenderTick = Environment.TickCount64;

                // 后台只处理托管像素；XAML 对象和 PixelBuffer 始终留在 UI 线程。
                var pixels = await Task.Run(() => SkinRenderer3D.Render(
                    skin, width, height, yaw, pitch), token);
                token.ThrowIfCancellationRequested();
                if (!_previewActive || !IsLoaded || version != _renderVersion ||
                    frameVersion != _frameVersion || Account?.Id != accountId) continue;

                var bitmap = new WriteableBitmap(width, height);
                using (var stream = bitmap.PixelBuffer.AsStream())
                    stream.Write(pixels, 0, pixels.Length);
                bitmap.Invalidate();
                SkinModel3D.Source = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // 换账户/换模型/离页取消旧帧；新一代请求由 finally 接续。
        }
        catch (Exception ex)
        {
            Log.Warn("3D 皮肤渲染失败：" + ex.Message);
        }
        finally
        {
            _frameWorkerRunning = false;
            if (_frameRequested && _previewActive && IsLoaded && _skinPixels is not null)
                _ = RenderFramesAsync();
        }
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
}

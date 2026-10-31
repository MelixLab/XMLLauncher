using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private string? _accountId;
    private bool _loading;
    private int _renderVersion;
    private bool _operationBusy;

    private void PageContent_SizeChanged(object sender, SizeChangedEventArgs e)
        => PageContent.Padding = new Thickness(e.NewSize.Width < 720 ? 16 : 32, 28, e.NewSize.Width < 720 ? 16 : 32, 24);

    private void AccountRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 800;
        Grid.SetRow(AccountPicker, narrow ? 1 : 0);
        Grid.SetColumn(AccountPicker, narrow ? 0 : 1);
        Grid.SetColumnSpan(AccountPicker, narrow ? 3 : 1);
        Grid.SetRow(DefaultHint, narrow ? 2 : 0);
        Grid.SetColumn(DefaultHint, narrow ? 0 : 2);
        Grid.SetColumnSpan(DefaultHint, narrow ? 3 : 1);
    }

    private void SkinContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 800;
        PreviewColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
        Grid.SetColumnSpan(PreviewCard, narrow ? 2 : 1);
        Grid.SetColumn(EditingCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(EditingCard, narrow ? 2 : 1);
        Grid.SetRow(EditingCard, narrow ? 1 : 0);
        SkinContent.ColumnSpacing = narrow ? 0 : 16;
    }

    public SkinsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _renderVersion++;
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

        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarInitial.Visibility = Visibility.Visible;
        BodyFront.Source = null;
        BodyBack.Source = null;
        _ = RenderAsync(acc, ++_renderVersion);

        UpdateActionState();

        StatusText.Text = acc.IsMicrosoft
            ? "从角色档案同步会拉 Mojang 上这张皮肤；上传会把当前皮肤（按上面的模型）写回你的正版账号，重启游戏后生效。"
            : "离线账户的皮肤只在本机生效：启动器里的头像和预览会跟着变；装了 CustomSkinLoader 的整合包，换皮肤时会顺手写进 CustomSkinLoader/LocalSkin/skin/名字.png，进游戏也能看到。服务器若开着在线验证，别人的客户端仍然看到默认皮肤。";

        // 和账户页一样：页面是导航后才真正布局的，补刷一次字体
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FontService.Apply(this));
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

    private bool IsViewing(GameAccount acc) => IsLoaded && Account?.Id == acc.Id;

    private async Task RenderAsync(GameAccount acc, int version)
    {
        var path = acc.SkinPath;
        var model = acc.SkinModel;

        var avatar = await SkinService.RenderAvatarAsync(path, 256, model);
        var front = await SkinService.RenderBodyAsync(path, model, false, 6);
        var back = await SkinService.RenderBodyAsync(path, model, true, 6);
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

        BodyFront.Source = front;
        BodyBack.Source = back;
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

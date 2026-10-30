using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinxLauncher.Views;

public sealed partial class SettingsAccountPage : Page
{
    private bool _loading = true;
    private bool _xmlBusy;
    private bool _registering;
    private bool _registrationCompleted;
    private bool _needsLoginCode;
    private DateTimeOffset _nextCodeSend;
    private DispatcherQueueTimer? _codeTimer;
    private ContentDialog? _dialog;
    private readonly TextBox _userBox = new() { Header = "用户名", MaxLength = 24, PlaceholderText = "至少 3 个字符" };
    private readonly PasswordBox _passBox = new() { Header = "密码", PlaceholderText = "至少 6 个字符" };
    private readonly TextBox _emailBox = new() { Header = "邮箱", PlaceholderText = "QQ / 163 / 126 等国内邮箱" };
    private readonly TextBox _codeBox = new() { Header = "邮箱验证码", MaxLength = 6 };
    private readonly Button _sendCodeButton = new() { Content = "发送验证码" };
    private readonly TextBlock _dialogStatus = new() { TextWrapping = TextWrapping.Wrap };

    public SettingsAccountPage()
    {
        InitializeComponent();
        TelemetrySwitch.IsOn = AppState.Settings.TelemetryEnabled;
        _loading = false;
        Loaded += (_, _) => RefreshXmlAccount();
        _sendCodeButton.Click += SendCode_Click;
        _userBox.TextChanged += (_, _) => ResetLoginChallenge();
        _passBox.PasswordChanged += (_, _) => ResetLoginChallenge();
    }

    private void ResetLoginChallenge()
    {
        if (_xmlBusy || _registering) return;
        _needsLoginCode = false;
        _codeBox.Text = "";
        _codeBox.Visibility = Visibility.Collapsed;
        _dialogStatus.Text = "";
    }

    private void RefreshXmlAccount()
    {
        bool logged = LinxAccountService.IsLoggedIn;
        XmlSignedOut.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;
        XmlSignedIn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
        XmlNameText.Text = logged ? $"已登录：{LinxAccountService.DisplayName}" : "未登录";
    }

    private void SetXmlStatus(string text, bool error = false)
    {
        XmlStatusText.Text = text;
        _dialogStatus.Text = text;
        var brush = ThemeService.Brush(error ? "NoticeTextBrush" : "SecondaryTextBrush");
        XmlStatusText.Foreground = brush;
        _dialogStatus.Foreground = brush;
    }

    private void Telemetry_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.TelemetryEnabled = TelemetrySwitch.IsOn;
        SettingsService.Save();
        if (TelemetrySwitch.IsOn) Telemetry.Start();
        else Telemetry.Stop();
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs e) => await ShowAccountDialogAsync(false);
    private async void OpenRegister_Click(object sender, RoutedEventArgs e) => await ShowAccountDialogAsync(true);

    private async Task ShowAccountDialogAsync(bool registering)
    {
        if (_dialog is not null) return;
        _registering = registering;
        _registrationCompleted = false;
        _needsLoginCode = false;
        _codeBox.Text = "";
        _emailBox.Visibility = registering ? Visibility.Visible : Visibility.Collapsed;
        _codeBox.Visibility = registering ? Visibility.Visible : Visibility.Collapsed;
        _sendCodeButton.Visibility = registering ? Visibility.Visible : Visibility.Collapsed;
        _dialogStatus.Text = "";
        var form = new StackPanel { Spacing = 14, MaxWidth = 420 };
        form.Children.Add(_userBox);
        form.Children.Add(_passBox);
        form.Children.Add(_emailBox);
        form.Children.Add(_codeBox);
        form.Children.Add(_sendCodeButton);
        form.Children.Add(_dialogStatus);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = registering ? "注册 XML 账户" : "登录 XML 账户",
            PrimaryButtonText = registering ? "注册并登录" : "登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        _dialog = dialog;
        dialog.PrimaryButtonClick += SubmitAccount;
        dialog.Closing += (_, args) => args.Cancel = _xmlBusy;
        FontService.ApplyToDialog(dialog);
        UpdateCodeCooldown();
        _codeTimer ??= DispatcherQueue.CreateTimer();
        _codeTimer.Interval = TimeSpan.FromSeconds(1);
        _codeTimer.Tick -= CodeTimer_Tick;
        _codeTimer.Tick += CodeTimer_Tick;
        _codeTimer.Start();
        try { await dialog.ShowAsync(); }
        finally
        {
            _codeTimer.Stop();
            _dialog = null;
            // 保留用户名、邮箱草稿；密码和验证码不跨对话框保留。
            form.Children.Clear();
            _passBox.Password = "";
            _codeBox.Text = "";
            RefreshXmlAccount();
        }
    }

    private void CodeTimer_Tick(DispatcherQueueTimer sender, object args) => UpdateCodeCooldown();

    private void UpdateCodeCooldown()
    {
        int seconds = Math.Max(0, (int)Math.Ceiling((_nextCodeSend - DateTimeOffset.UtcNow).TotalSeconds));
        _sendCodeButton.IsEnabled = seconds == 0 && !_xmlBusy;
        _sendCodeButton.Content = seconds > 0 ? $"重发（{seconds}s）" : "发送验证码";
    }

    private void SetBusy(bool busy)
    {
        _xmlBusy = busy;
        if (_dialog is not null) _dialog.IsPrimaryButtonEnabled = !busy;
        _userBox.IsEnabled = _passBox.IsEnabled = _emailBox.IsEnabled = _codeBox.IsEnabled = !busy;
        UpdateCodeCooldown();
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        if (_xmlBusy || DateTimeOffset.UtcNow < _nextCodeSend) return;
        string email = _emailBox.Text.Trim();
        if (!email.Contains('@')) { SetXmlStatus("先填写有效邮箱。", true); return; }
        SetBusy(true);
        SetXmlStatus("正在发送验证码…");
        try
        {
            var (message, debug) = await LinxAccountService.SendEmailCodeAsync(email, "register", default);
            _nextCodeSend = DateTimeOffset.UtcNow.AddSeconds(60);
            SetXmlStatus(debug is null ? message : $"测试服验证码：{debug}");
        }
        catch (Exception ex) { SetXmlStatus("发送失败：" + ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async void SubmitAccount(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_xmlBusy) return;
        string user = _userBox.Text.Trim(), pass = _passBox.Password;
        if (user.Length < 3 || pass.Length < 6)
        { SetXmlStatus("用户名至少 3 位，密码至少 6 位。", true); return; }
        if (_registering && (!_emailBox.Text.Contains('@') || _codeBox.Text.Trim().Length < 4))
        { SetXmlStatus("请填写邮箱并输入收到的验证码。", true); return; }
        if (_needsLoginCode && _codeBox.Text.Trim().Length < 4)
        { SetXmlStatus("请输入邮件中的验证码。", true); return; }

        var deferral = args.GetDeferral();
        SetBusy(true);
        try
        {
            if (_registering)
            {
                SetXmlStatus("正在注册…");
                await LinxAccountService.RegisterAsync(user, pass, _emailBox.Text.Trim(), _codeBox.Text.Trim(), default);
                // 注册已成功后切换为登录，重试时不会重复注册。
                _registering = false;
                _registrationCompleted = true;
                sender.Title = "登录 XML 账户";
                sender.PrimaryButtonText = "登录";
                _emailBox.Visibility = _sendCodeButton.Visibility = Visibility.Collapsed;
                _codeBox.Text = "";
                _codeBox.Visibility = Visibility.Collapsed;
            }
            SetXmlStatus(_registrationCompleted ? "注册成功，正在登录…" : "正在登录…");
            var result = await LinxAccountService.LoginAsync(user, pass, _needsLoginCode ? _codeBox.Text.Trim() : null, default);
            if (result.NeedCode)
            {
                // 在同一对话框完成二次验证，避免嵌套 ContentDialog。
                _needsLoginCode = true;
                _codeBox.Visibility = Visibility.Visible;
                SetXmlStatus((_registrationCompleted ? "注册成功。" : "") + result.Hint);
                _codeBox.Focus(FocusState.Programmatic);
                return;
            }
            if (!result.LoggedIn) { SetXmlStatus("登录未完成，请重试。", true); return; }
            args.Cancel = false;
            SetXmlStatus("登录成功。");
            MainWindow.Instance?.RefreshAccountUi();
            MainWindow.Instance?.Toast($"已登录 XML 账户「{LinxAccountService.DisplayName}」");
        }
        catch (Exception ex)
        {
            SetXmlStatus((_registrationCompleted ? "注册已成功，登录未完成：" : "操作失败：") + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
            if (_needsLoginCode) _codeBox.Focus(FocusState.Programmatic);
            deferral.Complete();
        }
    }

    private async void XmlAvatar_Click(object sender, RoutedEventArgs e)
    {
        var window = MainWindow.Instance;
        if (window is null) return;

        var path = await window.PickImageAsync("server-avatar");
        if (path is null) return;

        AppState.Settings.ServerAvatarPath = path;
        SettingsService.Save();
        window.RefreshAccountUi();

        SetXmlStatus("头像已换好，正在同步到服务器…");
        var uploaded = await LinxAccountService.UploadAvatarAsync(path, default);
        SetXmlStatus(
            uploaded ? "头像已同步，网页端和别的设备都能看到。" : "头像已保存在本机，同步到服务器失败。",
            !uploaded);
    }

    private void XmlManage_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenServerWeb();

    private void XmlLogout_Click(object sender, RoutedEventArgs e)
    {
        LinxAccountService.Logout();
        RefreshXmlAccount();
        MainWindow.Instance?.RefreshAccountUi();
        SetXmlStatus("已退出。要联机或者发帖的话，重新登录一次。");
        MainWindow.Instance?.Toast("已退出 XML 账号");
    }

}

using System;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LinxLauncher.Views;

/// <summary>
/// 设置页的「XML 账号」一节（站内账号）。
///
/// 背景：账户页重构成「卡包」之后，旧账号面板里的 XML 登录 / 注册整段被留在了
/// MainWindow.BuildAccountDialogAsync 里，而那个对话框已经没有入口，等于把
/// 站内账号的登录、注册、换头像全弄丢了。
///
/// 这里把它们放回设置页：登录（含邮箱二次验证）、注册（邮箱验证码）、换头像、
/// 管理账号、退出。接口全部走 <see cref="LinxAccountService"/>，没有新协议。
/// </summary>
public sealed partial class SettingsPage
{
    private DispatcherQueueTimer? _xmlCodeTimer;
    private int _xmlCodeCooldown;
    private bool _xmlBusy;

    // ================= 状态 =================

    private void RefreshXmlAccount()
    {
        var logged = LinxAccountService.IsLoggedIn;

        XmlSignedOut.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;
        XmlSignedIn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
        XmlNameText.Text = logged ? $"已登录：{LinxAccountService.DisplayName}" : "";
    }

    private void SetXmlStatus(string text, bool error = false)
    {
        XmlStatusText.Text = text;
        XmlStatusText.Foreground = error
            ? ResourceBrush("NoticeTextBrush", 0xC5, 0x05, 0x00)
            : ResourceBrush("SecondaryTextBrush", 0x60, 0x60, 0x60);
    }

    private void XmlInput_Changed(object sender, TextChangedEventArgs e)
    {
        // 输入改了就把上一条提示擦掉，免得旧错误一直挂着误导人
        if (!_xmlBusy && XmlStatusText.Text.Length > 0)
            SetXmlStatus("");
    }

    // ================= 登录 =================

    private async void XmlLogin_Click(object sender, RoutedEventArgs e)
    {
        var window = MainWindow.Instance;
        if (window is null) return;

        var user = XmlUserBox.Text.Trim();
        var pass = XmlPassBox.Password;

        if (user.Length < 3 || pass.Length < 6)
        {
            SetXmlStatus("用户名至少 3 位，密码至少 6 位。", true);
            return;
        }

        _xmlBusy = true;
        XmlLoginButton.IsEnabled = false;
        XmlLoginButton.Content = "登录中…";
        SetXmlStatus("正在登录…");

        try
        {
            // 走主窗口这个入口：服务端开了邮箱二次验证时它会弹验证码框，
            // 这里不用再拼一遍弹框。
            var ok = await window.ServerLoginAsync(user, pass);
            if (!ok)
            {
                SetXmlStatus("登录没有完成。这个账号如果开了邮箱验证，点「登录」后把邮件里的验证码填进弹框再试一次。", true);
                return;
            }

            XmlPassBox.Password = "";
            XmlCodeBox.Text = "";
            SetXmlStatus("");
            RefreshXmlAccount();
            window.RefreshAccountUi();
            window.Toast($"已登录 XML 账号「{LinxAccountService.DisplayName}」");
        }
        catch (Exception ex)
        {
            SetXmlStatus("登录失败：" + ex.Message, true);
        }
        finally
        {
            _xmlBusy = false;
            XmlLoginButton.IsEnabled = true;
            XmlLoginButton.Content = "登录";
        }
    }

    // ================= 注册 =================

    private async void XmlSendCode_Click(object sender, RoutedEventArgs e)
    {
        var email = XmlEmailBox.Text.Trim();
        if (!email.Contains('@'))
        {
            SetXmlStatus("先填一个邮箱，QQ / 163 / 126 这类国内邮箱都行。", true);
            return;
        }

        XmlSendCodeButton.IsEnabled = false;
        SetXmlStatus("正在发送验证码…");

        try
        {
            var (message, debug) = await LinxAccountService.SendEmailCodeAsync(email, "register", default);

            // 测试服（mail_debug）不发信，直接把码返回；正式服只能靠收信
            SetXmlStatus(debug is null ? message : $"测试服验证码：{debug}");
            StartXmlCodeCooldown();
        }
        catch (Exception ex)
        {
            SetXmlStatus("发送失败：" + ex.Message, true);
            XmlSendCodeButton.IsEnabled = true;
        }
    }

    private async void XmlRegister_Click(object sender, RoutedEventArgs e)
    {
        var window = MainWindow.Instance;
        if (window is null) return;

        var user = XmlUserBox.Text.Trim();
        var pass = XmlPassBox.Password;
        var email = XmlEmailBox.Text.Trim();
        var code = XmlCodeBox.Text.Trim();

        if (user.Length < 3 || pass.Length < 6)
        {
            SetXmlStatus("用户名至少 3 位，密码至少 6 位。", true);
            return;
        }

        if (!email.Contains('@'))
        {
            SetXmlStatus("注册要填邮箱（只支持国内邮箱）。", true);
            return;
        }

        if (code.Length < 4)
        {
            SetXmlStatus("先点「发送验证码」，把收到的 6 位验证码填上。", true);
            return;
        }

        _xmlBusy = true;
        XmlRegisterButton.IsEnabled = false;
        SetXmlStatus("正在注册…");

        try
        {
            await LinxAccountService.RegisterAsync(user, pass, email, code, default);

            // 服务端的注册接口只建号、不发令牌，所以注册完要再登录一次。
            SetXmlStatus("注册成功，正在登录…");
            var ok = await window.ServerLoginAsync(user, pass);
            if (!ok)
            {
                SetXmlStatus("注册成功，但自动登录没完成。直接用上面的用户名和密码登录一次就行。", true);
                return;
            }

            XmlPassBox.Password = "";
            XmlCodeBox.Text = "";
            SetXmlStatus("");
            RefreshXmlAccount();
            window.RefreshAccountUi();
            window.Toast($"注册成功，已登录「{LinxAccountService.DisplayName}」");
        }
        catch (Exception ex)
        {
            SetXmlStatus("注册失败：" + ex.Message, true);
        }
        finally
        {
            _xmlBusy = false;
            XmlRegisterButton.IsEnabled = true;
        }
    }

    private void StartXmlCodeCooldown()
    {
        if (_xmlCodeTimer is null)
        {
            _xmlCodeTimer = DispatcherQueue.CreateTimer();
            _xmlCodeTimer.Interval = TimeSpan.FromSeconds(1);
            _xmlCodeTimer.IsRepeating = true;
            _xmlCodeTimer.Tick += (_, _) => TickXmlCodeCooldown();
        }

        // 服务端冷却就是 60 秒，按钮跟着一起数，免得点了被 429 打回来
        _xmlCodeCooldown = 60;
        XmlSendCodeButton.Content = "重发(60s)";
        _xmlCodeTimer.Start();
    }

    private void TickXmlCodeCooldown()
    {
        _xmlCodeCooldown = Math.Max(0, _xmlCodeCooldown - 1);

        if (_xmlCodeCooldown == 0)
        {
            _xmlCodeTimer?.Stop();
            XmlSendCodeButton.IsEnabled = true;
            XmlSendCodeButton.Content = "发送验证码";
            return;
        }

        XmlSendCodeButton.Content = $"重发({_xmlCodeCooldown}s)";
    }

    // ================= 已登录后的操作 =================

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

    // ================= 工具 =================

    private static Brush ResourceBrush(string key, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch { }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }
}

using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LinxLauncher.Services;

namespace LinxLauncher.Controls;

/// <summary>
/// 首次登录向导。全部用 WinUI 自带控件，不自绘对话框：
///   · 每一步设置 = ContentDialog（和启动器其它对话框长得一模一样）
///   · 页面介绍   = TeachingTip（系统自带的气泡，带指向箭头，就是「VOS3 那种气泡」）
///
/// 流程：欢迎 → 登录方式（离线 / 正版）→ XML 账号 → 六个页面的气泡巡览 → 完成。
/// 全程不下载任何东西；只有用户主动点登录时才联网。
/// </summary>
public sealed class OnboardingFlow
{
    private sealed record TourStep(string Tag, string Title, string Body, string Label);

    /// <summary>巡览步骤：点「下一步」自动切到对应页面，气泡指到侧栏那一项上。</summary>
    private static readonly TourStep[] TourSteps =
    {
        new("overview", "概览：一屏看清家底",
            "六张卡片分别是启动器、加载器、下载设置、已装版本、Java 和下载任务。点卡片上的按钮就能直接跳过去。",
            "概览"),
        new("versions", "版本库：管理装好的版本",
            "本机所有实例都在这里。要装 Fabric / Forge 就在这一页选加载器，启动器会自动补齐依赖；每个版本还能单独设内存和版本隔离。",
            "版本库"),
        new("downloads", "下载：先把原版装上",
            "第一次玩要先下载一个原版版本。进度会出现在「下载中」，可以随时取消，不会留下半截文件。",
            "下载"),
        new("mods", "Mod 下载：从 Modrinth 找模组",
            "搜索、看简介、一键装进当前版本。装之前留意模组支持的版本和加载器要跟你的实例对得上，否则游戏起不来。",
            "Mod 下载"),
        new("relay", "联机：和朋友玩同一个存档",
            "你开局域网、把联机码发给朋友，对方输码就能进来；1.20 以上会自动直接进服。这一步需要登录 XML 账号。",
            "联机"),
        new("settings", "设置：内存和 Java",
            "内存建议 4096 MB 起（模组多就往上加）；Java 一般自动选，起不来时到这里换一个。下载镜像通常不用动。",
            "设置"),
    };

    private readonly MainWindow _window;

    public OnboardingFlow(MainWindow window) => _window = window;

    private XamlRoot Root => _window.Content.XamlRoot;

    public async Task RunAsync()
    {
        // 已经设好的步骤不重复问：这是「重新开始向导」最常见的用法
        if (LinxAccountService.IsLoggedIn)
        {
            await RunTourAsync();
            await ShowDoneAsync();
            return;
        }

        if (AccountService.IsMicrosoft)
        {
            await ShowXmlAsync();
            await RunTourAsync();
            await ShowDoneAsync();
            return;
        }

        if (!await ShowWelcomeAsync()) return;

        await ShowAccountAsync();
        await ShowXmlAsync();
        await RunTourAsync();
        await ShowDoneAsync();
    }

    // ================= 1. 欢迎 =================

    private async Task<bool> ShowWelcomeAsync()
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 440 };
        panel.Children.Add(Text(
            "接下来会问你两件事：用什么身份进游戏、要不要登录 XML 账号，然后带你认一遍左侧的六个页面。设置随时能改，也能在「设置 → 新手向导」里重新开始。",
            13.5, 21));
        panel.Children.Add(Banner(
            "这个向导不会下载任何文件：不装游戏、不装 Java、不动你已有的版本库。只有你主动点登录时才会联网。"));

        // 用户体验改进计划：默认开启，这一页就能取消
        var telemetryBox = new CheckBox
        {
            Content = "参与用户体验改进计划",
            IsChecked = AppState.Settings.TelemetryEnabled,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(telemetryBox);
        panel.Children.Add(Text(
            "上报启动器的启动、开游戏、异常退出这些事件，附带版本号、系统信息和随机设备号；不含账号密码、存档内容和文件路径。随时能在「设置」里关掉。",
            12.5, 18));

        var dialog = Dialog("欢迎使用 XML 启动器", panel, primary: "开始设置", close: "跳过");
        var result = await SafeShowAsync(dialog);

        AppState.Settings.TelemetryEnabled = telemetryBox.IsChecked == true;
        SettingsService.Save();
        if (AppState.Settings.TelemetryEnabled) Telemetry.Start();
        else Telemetry.Stop();

        return result == ContentDialogResult.Primary;
    }

    // ================= 2. 登录方式 =================

    private async Task ShowAccountAsync()
    {
        var choices = new RadioButtons { SelectedIndex = 0 };
        choices.Items.Add(Choice(
            "离线模式",
            "不用任何账号，名字自己起。单机、局域网和大部分服务器都能进。"));
        choices.Items.Add(Choice(
            "Microsoft 正版账号",
            "已购买 Minecraft: Java Edition 就用这个。会打开网页让你输一串代码，登录完自动继续。"));

        var nameBox = new TextBox
        {
            Header = "游戏里的名字",
            Text = AppState.Settings.PlayerName,
            MaxLength = 16,
            PlaceholderText = "1–16 个字符，可以中文",
            Margin = new Thickness(0, 8, 0, 0)
        };

        var panel = new StackPanel { Spacing = 10, MaxWidth = 440 };
        panel.Children.Add(StepLabel("第 1 步 / 共 3 步"));
        panel.Children.Add(Text("这一步只决定游戏里显示的名字、以及能不能进正版服务器。以后点侧栏左下角的头像随时能切换。", 13, 20));
        panel.Children.Add(choices);
        panel.Children.Add(nameBox);

        choices.SelectionChanged += (_, _) =>
            nameBox.Visibility = choices.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

        var dialog = Dialog("用什么身份进游戏？", panel, primary: "继续", close: "跳过");
        var result = await SafeShowAsync(dialog);
        if (result != ContentDialogResult.Primary) return;

        if (choices.SelectedIndex == 1)
        {
            // 微软登录自己要开一个 ContentDialog：两个对话框不能同时开，隔一下
            await Task.Delay(260);
            await _window.MicrosoftLoginAsync();
            // 登录对话框收起也有动画，等它走完再开下一步的对话框
            await Task.Delay(350);
            return;
        }

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = "Steve";

        AppState.Settings.PlayerName = name;
        if (AccountService.IsMicrosoft) AccountService.UseOffline();
        SettingsService.Save();
        _window.RefreshAccountUi();
    }

    // ================= 3. XML 账号 =================

    private async Task ShowXmlAsync()
    {
        bool loggedIn = false;

        var userBox = new TextBox { Header = "用户名", PlaceholderText = "3–24 位", MaxLength = 24 };
        var passBox = new PasswordBox { Header = "密码", PlaceholderText = "至少 6 位" };

        var fields = new Grid { ColumnSpacing = 8 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.Children.Add(userBox);
        Grid.SetColumn(passBox, 1);
        fields.Children.Add(passBox);

        // 服务端注册强制邮箱验证，向导里也得能发码
        var emailBox = new TextBox { Header = "邮箱（注册用）", PlaceholderText = "QQ / 163 / 126 这类国内邮箱" };
        var codeBox = new TextBox { Header = "验证码", PlaceholderText = "6 位数字", MaxLength = 6 };
        var sendBtn = new Button { Content = "发送验证码", VerticalAlignment = VerticalAlignment.Bottom };

        var emailRow = new Grid { ColumnSpacing = 8 };
        emailRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        emailRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        emailRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        emailRow.Children.Add(emailBox);
        Grid.SetColumn(codeBox, 1);
        emailRow.Children.Add(codeBox);
        Grid.SetColumn(sendBtn, 2);
        emailRow.Children.Add(sendBtn);

        var status = Text("", 12.5, 18);
        status.TextWrapping = TextWrapping.Wrap;
        status.Visibility = Visibility.Collapsed;

        var loginButton = new Button { Content = "登录", Style = AccentStyle, MinWidth = 88 };
        var registerButton = new Button { Content = "注册新账号" };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(loginButton);
        actions.Children.Add(registerButton);

        var panel = new StackPanel { Spacing = 10, MaxWidth = 440 };
        panel.Children.Add(StepLabel("第 2 步 / 共 3 步"));
        panel.Children.Add(Text(
            "XML 账号是本站账号：联机开房间、同步头像、在网页端管理都用它。登录后功能最全，我们强烈推荐登录；不登录也能玩单机，只是联机用不了。",
            13, 20));
        panel.Children.Add(Banner(
            "登录以后可以：和朋友联机（各输一个联机码进同一个存档）、头像在网页端同步、公告与版本管理走同一个账号。"));
        panel.Children.Add(fields);
        panel.Children.Add(emailRow);
        panel.Children.Add(actions);
        panel.Children.Add(status);

        var dialog = Dialog("再登录一个 XML 账号", panel, close: "先跳过");
        FontService.ApplyToDialog(dialog);

        async Task DoAuthAsync(bool register)
        {
            var user = userBox.Text.Trim();
            var pass = passBox.Password;
            var email = emailBox.Text.Trim();
            var code = codeBox.Text.Trim();

            if (user.Length < 3 || pass.Length < 6)
            {
                SetStatus("用户名至少 3 位，密码至少 6 位。", error: true);
                return;
            }
            if (register && (!email.Contains('@') || code.Length < 4))
            {
                SetStatus("注册要填邮箱并点「发送验证码」，把邮件里的 6 位数字填进来。", error: true);
                return;
            }

            loginButton.IsEnabled = false;
            registerButton.IsEnabled = false;
            SetStatus(register ? "正在注册…" : "正在登录…");

            try
            {
                if (register)
                    SetStatus(await LinxAccountService.RegisterAsync(user, pass, email, code, default));

                // 向导里已经有对话框开着，不能再弹验证码框：开了二次验证的账号提示去设置页登录
                if (!await _window.ServerLoginAsync(user, pass, status, allowPrompt: false))
                {
                    SetStatus("这个账号开了邮箱二次验证，请先跳过向导，在「设置 → XML 账号」里登录。", error: true);
                    return;
                }

                loggedIn = true;
                SetStatus($"已登录「{user}」，正在继续…");
                _window.RefreshAccountUi();

                await Task.Delay(600);
                dialog.Hide();
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, error: true);
            }
            finally
            {
                loginButton.IsEnabled = true;
                registerButton.IsEnabled = true;
            }
        }

        var cooldown = 0;
        var codeTimer = dialog.DispatcherQueue.CreateTimer();
        codeTimer.Interval = TimeSpan.FromSeconds(1);
        codeTimer.IsRepeating = true;
        codeTimer.Tick += (_, _) =>
        {
            cooldown--;
            if (cooldown <= 0)
            {
                cooldown = 0;
                codeTimer.Stop();
                sendBtn.IsEnabled = true;
                sendBtn.Content = "发送验证码";
            }
            else sendBtn.Content = $"重发({cooldown}s)";
        };

        sendBtn.Click += async (_, _) =>
        {
            var email = emailBox.Text.Trim();
            if (!email.Contains('@'))
            {
                SetStatus("先填一个邮箱（只支持国内邮箱）。", error: true);
                return;
            }

            sendBtn.IsEnabled = false;
            SetStatus("正在发送验证码…");
            try
            {
                var (message, debug) = await LinxAccountService.SendEmailCodeAsync(email, "register", default);
                if (!string.IsNullOrEmpty(debug)) codeBox.Text = debug;
                SetStatus(string.IsNullOrEmpty(debug) ? message : message + "（测试服验证码：" + debug + "）");
                cooldown = 60;
                codeTimer.Start();
            }
            catch (Exception ex)
            {
                sendBtn.IsEnabled = true;
                SetStatus(ex.Message, error: true);
            }
        };

        loginButton.Click += async (_, _) => await DoAuthAsync(register: false);
        registerButton.Click += async (_, _) => await DoAuthAsync(register: true);

        await SafeShowAsync(dialog);
        _ = loggedIn; // 是否登录成功不影响后面：失败也能继续往下走

        void SetStatus(string text, bool error = false)
        {
            status.Text = text;
            status.Foreground = Res(error ? "NoticeTextBrush" : "SecondaryTextBrush");
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ================= 4. 巡览（系统 TeachingTip 气泡） =================

    private async Task RunTourAsync()
    {
        int index = 0;
        while (index >= 0 && index < TourSteps.Length)
        {
            int move = await ShowTourStepAsync(index);
            if (move == 0) return;     // 跳过导览
            index += move;
        }
    }

    /// <summary>返回 1 = 下一步，-1 = 上一步，0 = 结束。</summary>
    private Task<int> ShowTourStepAsync(int index)
    {
        var step = TourSteps[index];
        var completion = new TaskCompletionSource<int>();

        _window.NavigateTo(step.Tag);

        var tip = new TeachingTip
        {
            Target = _window.GetNavItemAnchor(step.Tag),
            PreferredPlacement = TeachingTipPlacementMode.Right,
            // TeachingTip 的 Title / Subtitle 都是纯文本，进度写在标题前缀里
            Title = $"{index + 1} / {TourSteps.Length} · {step.Title}",
            Subtitle = step.Body,
            IsLightDismissEnabled = false,
            ActionButtonContent = index == TourSteps.Length - 1 ? "看完了" : "下一步",
            CloseButtonContent = "跳过导览"   // 系统气泡只有一个次要按钮位，写「上一步」容易被当成关闭
        };

        tip.ActionButtonClick += (_, _) => completion.TrySetResult(1);
        tip.CloseButtonClick += (_, _) => completion.TrySetResult(0);

        // 收起动画走完再从视觉树里摘掉，不然气泡会「啪」地消失
        tip.Closed += (_, _) =>
        {
            _window.RemoveTransientLayer(tip);
            completion.TrySetResult(0);
        };

        _window.AddTransientLayer(tip);

        // 气泡是系统控件，默认用系统字体；这里让它跟「设置 → 外观」的字体开关一致，
        // 等这一帧布局完再打开，否则 Target 还没量出位置。
        tip.FontFamily = FontService.BodyFont;
        tip.Loaded += (_, _) =>
        {
            FontService.Apply(tip);
            tip.IsOpen = true;
        };

        return AwaitStepAsync();

        async Task<int> AwaitStepAsync()
        {
            int move = await completion.Task;
            try { tip.IsOpen = false; } catch { }
            await Task.Delay(180);
            return move;
        }
    }

    // ================= 5. 完成 =================

    private async Task ShowDoneAsync()
    {
        var summary = new StackPanel { Spacing = 10, MaxWidth = 440 };
        summary.Children.Add(StepLabel("第 3 步 / 共 3 步"));
        summary.Children.Add(Text(
            "下面是这次的设置结果。之后想改，点侧栏左下角的头像，或者去「设置 → 新手向导」重新走一遍。",
            13, 20));
        summary.Children.Add(SummaryRow(
            "游戏身份",
            AccountService.IsMicrosoft ? $"正版 · {AppState.Settings.MsName}" : $"离线 · {AppState.Settings.PlayerName}"));
        summary.Children.Add(SummaryRow(
            "XML 账号",
            LinxAccountService.IsLoggedIn
                ? $"已登录 · {LinxAccountService.DisplayName}"
                : "未登录 · 联机和头像同步用不了，随时能在头像里登"));
        summary.Children.Add(SummaryRow(
            "已装版本",
            AppState.Installed.Count > 0
                ? $"{AppState.Installed.Count} 个，可以直接启动"
                : "还没有 · 先去「下载」页装一个原版"));

        var dialog = Dialog("可以开始了", summary, primary: "进入启动器", secondary: "再看一遍页面介绍");
        if (await SafeShowAsync(dialog) == ContentDialogResult.Secondary)
            await RunTourAsync();
    }

    // ================= 小工具 =================

    private ContentDialog Dialog(string title, object content, string? primary = null,
        string? secondary = null, string? close = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary
        };

        if (!string.IsNullOrEmpty(primary)) dialog.PrimaryButtonText = primary;
        if (!string.IsNullOrEmpty(secondary)) dialog.SecondaryButtonText = secondary;
        if (!string.IsNullOrEmpty(close)) dialog.CloseButtonText = close;

        // 字体跟着「设置 → 外观」里的正文开关走：标题、按钮这些不是 TextBlock，
        // 只靠 FontService.Apply 遍历不到，所以整只对话框先继承一次字体。
        dialog.FontFamily = FontService.BodyFont;
        FontService.ApplyToDialog(dialog);
        return dialog;
    }

    /// <summary>
    /// 开对话框。正常情况下不会失败；万一撞上「只能同时开一个对话框」，
    /// 也不该把整个向导带崩 —— 记一条日志，当作这一步被跳过。
    /// </summary>
    private static async Task<ContentDialogResult> SafeShowAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("OnboardingDialog", ex);
            await Task.Delay(420);
            return ContentDialogResult.None;
        }
    }

    private static Style AccentStyle => (Style)Application.Current.Resources["AccentButtonStyle"];

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Text(string text, double size, double lineHeight) => new()
    {
        Text = text,
        FontSize = size,
        LineHeight = lineHeight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Res("SecondaryTextBrush")
    };

    private static TextBlock StepLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("BrandBrush")
    };

    private static Border Banner(string text) => new()
    {
        Background = Res("BrandSoftBrush"),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10, 12, 10),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("PrimaryTextBrush")
        }
    };

    private static StackPanel Choice(string title, string description)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = Res("PrimaryTextBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("SecondaryTextBrush")
        });
        return panel;
    }

    private static UIElement SummaryRow(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Res("SecondaryTextBrush")
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("PrimaryTextBrush")
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);

        return grid;
    }
}

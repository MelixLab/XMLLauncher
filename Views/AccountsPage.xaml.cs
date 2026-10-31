using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

/// <summary>
/// 账户页：卡包。
///
/// 结构：当前账户是一张 140 高的大卡，其余账户压成 68 高的信息条，
/// 紧贴在大卡下面、彼此重叠 6px —— 看上去就是一摞卡，最上面那张抽出来展开。
///
/// 切换时 Height 和 Canvas.Top 一起做 240ms 的动画（高度动画会走布局，
/// 所以这里给 Storyboard 开了 EnableDependentAnimation；卡片只有几张，开销可以忽略）。
/// 折叠条上标题和类型都完整可见，不再靠「遮住一半」来省空间。
/// </summary>
public sealed partial class AccountsPage : Page
{
    private const double CardHeight = 140;
    private const double StripHeight = 80;
    private const double StripOverlap = 22;

    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(240);

    private sealed class CardRefs
    {
        public required string AccountId { get; init; }
        public required Border Root { get; init; }

        // 展开态
        public required Grid Expanded { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Kind { get; init; }
        public required TextBlock Uuid { get; init; }
        public required TextBlock Skin { get; init; }
        public required Border KindBadge { get; init; }
        public required StackPanel Actions { get; init; }
        public required Button RefreshToken { get; init; }
        public required Image Avatar { get; init; }
        public required TextBlock Initial { get; init; }

        // 折叠态
        public required Grid Compact { get; init; }
        public required TextBlock CompactName { get; init; }
        public required TextBlock CompactMeta { get; init; }
        public required Image CompactAvatar { get; init; }
        public required TextBlock CompactInitial { get; init; }
    }

    private readonly Dictionary<string, CardRefs> _cards = new();
    private readonly List<string> _order = new();

    public AccountsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void DeckHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Canvas 不会自动拉伸子元素；卡片宽度跟随卡包实际可用宽度。
        foreach (var card in _cards.Values) card.Root.Width = e.NewSize.Width;
    }

    private void PageHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 720;
        Grid.SetColumn(HeaderActions, narrow ? 0 : 1);
        Grid.SetRow(HeaderActions, narrow ? 1 : 0);
        HeaderActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PageHeader.ColumnSpacing = narrow ? 0 : 24;
        PageHeader.RowSpacing = narrow ? 16 : 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccountStore.Instance.Changed -= OnStoreChanged;
        AccountStore.Instance.Changed += OnStoreChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AccountStore.Instance.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged() => DispatcherQueue.TryEnqueue(Refresh);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    // ================= 刷新 =================

    private void Refresh()
    {
        var accounts = AccountService.Store.Accounts.ToList();
        var current = AccountService.Current;

        // 账户增删才重建卡片；单纯切换只做动画，卡片实例不动
        var needRebuild = accounts.Count != _cards.Count ||
                          accounts.Any(a => !_cards.ContainsKey(a.Id));

        if (needRebuild) BuildCards(accounts);

        foreach (var account in accounts)
        {
            if (_cards.TryGetValue(account.Id, out var refs))
                UpdateCard(refs, account, account.Id == current?.Id);
        }

        // 目标顺序：当前账户第一，其余保持原有相对顺序
        var target = new List<string>();
        if (current is not null && _cards.ContainsKey(current.Id)) target.Add(current.Id);
        target.AddRange(_order.Where(id => id != current?.Id && _cards.ContainsKey(id)));

        LayoutDeck(target, animate: !needRebuild);

        _order.Clear();
        _order.AddRange(target);

        CountText.Text = accounts.Count == 1 ? "1 个账户" : $"{accounts.Count} 个账户";

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FontService.Apply(this));
    }

    private void BuildCards(List<GameAccount> accounts)
    {
        DeckHost.Children.Clear();
        _cards.Clear();
        _order.Clear();

        foreach (var account in accounts)
        {
            var refs = BuildCard(account);
            DeckHost.Children.Add(refs.Root);
            _cards[account.Id] = refs;
            _order.Add(account.Id);
        }
    }

    private void LayoutDeck(List<string> order, bool animate)
    {
        double cursor = 0;
        double total = 0;

        for (var i = 0; i < order.Count; i++)
        {
            var refs = _cards[order[i]];
            var isCurrent = i == 0;
            var height = isCurrent ? CardHeight : StripHeight;

            // 展开/折叠两套内容
            refs.Expanded.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            refs.Compact.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;
            refs.Actions.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;

            Canvas.SetLeft(refs.Root, 0);
            Canvas.SetZIndex(refs.Root, 1000 - i);

            if (animate) AnimateCard(refs.Root, cursor, height);
            else
            {
                Canvas.SetTop(refs.Root, cursor);
                refs.Root.Height = height;
            }

            cursor += height;

            // 每张都和下一张重叠一段：看上去就是一摞卡
            if (i < order.Count - 1) cursor -= StripOverlap;

            total = cursor;
        }

        DeckHost.Height = Math.Max(CardHeight, total);

        if (animate) Log.Info($"账户卡包：{order.Count} 张，总高 {DeckHost.Height:0}");
    }

    /// <summary>高度 + 位置的同步动画。位置用 Canvas.Top（会走布局，所以两张都要开 dependent animation）。</summary>
    private static void AnimateCard(Border card, double top, double height)
    {
        try
        {
            var storyboard = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var move = new DoubleAnimation
            {
                To = top,
                Duration = AnimDuration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(move, card);
            Storyboard.SetTargetProperty(move, "(Canvas.Top)");
            storyboard.Children.Add(move);

            var grow = new DoubleAnimation
            {
                To = height,
                Duration = AnimDuration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(grow, card);
            Storyboard.SetTargetProperty(grow, "Height");
            storyboard.Children.Add(grow);

            storyboard.Begin();
        }
        catch (Exception ex)
        {
            Log.Warn("账户卡片动画失败：" + ex.Message);
            Canvas.SetTop(card, top);
            card.Height = height;
        }
    }

    // ================= 单张卡片 =================

    private CardRefs BuildCard(GameAccount account)
    {
        var secondary = ThemeService.Brush("SecondaryTextBrush");
        var primary = ThemeService.Brush("PrimaryTextBrush");

        // ---------- 展开态 ----------
        var initial = new TextBlock
        {
            Text = FirstLetter(account.Name),
            FontSize = 24,
            FontFamily = FontService.BodyFont,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var avatar = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };

        var avatarHost = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(6),
            Background = ThemeService.Brush("CardHoverBackgroundBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Grid { Children = { initial, avatar } }
        };

        var name = new TextBlock
        {
            Text = account.Name,
            FontSize = 19,
            FontFamily = FontService.BodyFont,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var kindText = new TextBlock
        {
            Text = account.KindText,
            FontSize = 12,
            FontFamily = FontService.BodyFont,
            Foreground = ThemeService.Brush("LinkTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var kindBadge = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(3),
            Background = ThemeService.Brush("BrandSoftBrush"),
            Child = kindText
        };

        var uuid = new TextBlock
        {
            Text = FormatUuid(account.EffectiveUuid),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center
        };

        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { kindBadge, uuid }
        };

        var skin = new TextBlock
        {
            Text = "",
            FontSize = 11,
            FontFamily = FontService.BodyFont,
            Foreground = secondary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var texts = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { name, metaRow, skin }
        };

        var skinButton = SmallButton("皮肤");
        skinButton.Click += (_, _) => MainWindow.Instance?.NavigateTo("skins", account.Id);

        var refreshButton = SmallButton("续期");
        refreshButton.Click += async (_, _) => await RefreshTokenAsync(account.Id);
        refreshButton.Visibility = account.IsMicrosoft ? Visibility.Visible : Visibility.Collapsed;

        var removeButton = SmallButton("删除");
        removeButton.Click += async (_, _) => await RemoveAsync(account.Id);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { skinButton, refreshButton, removeButton }
        };

        var expandedRow = new Grid { ColumnSpacing = 18 };
        expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        expandedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expandedRow.Children.Add(avatarHost);
        Grid.SetColumn(texts, 1);
        expandedRow.Children.Add(texts);
        Grid.SetColumn(actions, 2);
        expandedRow.Children.Add(actions);

        var expanded = new Grid
        {
            Padding = new Thickness(20, 14, 20, 14),
            Children = { expandedRow }
        };

        // ---------- 折叠态：一条 68 高的信息条 ----------
        var compactInitial = new TextBlock
        {
            Text = FirstLetter(account.Name),
            FontSize = 17,
            FontFamily = FontService.BodyFont,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var compactAvatar = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };

        var compactAvatarHost = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(5),
            Background = ThemeService.Brush("CardHoverBackgroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { compactInitial, compactAvatar } }
        };

        var compactName = new TextBlock
        {
            Text = account.Name,
            FontSize = 15,
            FontFamily = FontService.BodyFont,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var compactMeta = new TextBlock
        {
            Text = $"{account.KindText} · {account.ModelText}",
            FontSize = 10,
            FontFamily = FontService.BodyFont,
            Foreground = secondary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var compactTexts = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { compactName, compactMeta }
        };

        var compactRow = new Grid { ColumnSpacing = 12 };
        compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compactRow.Children.Add(compactAvatarHost);
        Grid.SetColumn(compactTexts, 1);
        compactRow.Children.Add(compactTexts);

        var compact = new Grid
        {
            // 顶部留出 26px：上面那张卡会盖住信息条的头 22px，内容要躲开
            Padding = new Thickness(18, 26, 18, 6),
            Visibility = Visibility.Collapsed,
            Children = { compactRow }
        };

        // ---------- 卡片本体 ----------
        var root = new Border
        {
            Width = DeckHost.ActualWidth,
            Height = CardHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(12),
            Background = ThemeService.Brush("CardBackgroundBrush"),
            BorderBrush = ThemeService.Brush("DividerBrush"),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { expanded, compact } }
        };

        var refs = new CardRefs
        {
            AccountId = account.Id,
            Root = root,
            Expanded = expanded,
            Name = name,
            Kind = kindText,
            Uuid = uuid,
            Skin = skin,
            KindBadge = kindBadge,
            Actions = actions,
            RefreshToken = refreshButton,
            Avatar = avatar,
            Initial = initial,
            Compact = compact,
            CompactName = compactName,
            CompactMeta = compactMeta,
            CompactAvatar = compactAvatar,
            CompactInitial = compactInitial
        };

        root.Tapped += (_, args) =>
        {
            if (IsFromButton(args.OriginalSource)) return;
            if (AccountService.Current?.Id == refs.AccountId) return;

            AccountService.SetCurrent(refs.AccountId);
            MainWindow.Instance?.RefreshAccountUi();
            Refresh();
        };

        return refs;
    }

    private void UpdateCard(CardRefs refs, GameAccount account, bool isCurrent)
    {
        refs.Name.Text = account.Name;
        refs.Kind.Text = account.KindText;
        refs.Uuid.Text = FormatUuid(account.EffectiveUuid);

        var skinText = account.HasSkin
            ? $"皮肤 · {(account.SkinSource == "mojang" ? "正版档案" : "本地导入")} · {account.ModelText}"
            : $"默认皮肤 · {(account.SkinModel == "slim" ? "Alex" : "Steve")}";

        refs.Skin.Text = isCurrent ? skinText + " · 使用中" : skinText;

        refs.CompactName.Text = account.Name;
        refs.CompactMeta.Text = $"{account.KindText} · {account.ModelText}";

        refs.RefreshToken.Visibility = account.IsMicrosoft ? Visibility.Visible : Visibility.Collapsed;

        refs.KindBadge.Background = account.IsMicrosoft
            ? ThemeService.Brush("BrandSoftBrush")
            : ThemeService.Brush("CardHoverBackgroundBrush");

        refs.Root.BorderBrush = isCurrent
            ? ThemeService.Brush("BrandBrush")
            : ThemeService.Brush("DividerBrush");
        refs.Root.BorderThickness = new Thickness(isCurrent ? 1.5 : 1);

        _ = UpdateAvatarsAsync(refs, account);
    }

    private static async Task UpdateAvatarsAsync(CardRefs refs, GameAccount account)
    {
        var source = await SkinService.RenderAvatarAsync(account.SkinPath, 104, account.SkinModel);

        if (source is null)
        {
            refs.Avatar.Visibility = Visibility.Collapsed;
            refs.Initial.Visibility = Visibility.Visible;
            refs.CompactAvatar.Visibility = Visibility.Collapsed;
            refs.CompactInitial.Visibility = Visibility.Visible;
            return;
        }

        refs.Avatar.Source = source;
        refs.Avatar.Visibility = Visibility.Visible;
        refs.Initial.Visibility = Visibility.Collapsed;

        refs.CompactAvatar.Source = source;
        refs.CompactAvatar.Visibility = Visibility.Visible;
        refs.CompactInitial.Visibility = Visibility.Collapsed;
    }

    // ================= 操作 =================

    private async void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            PlaceholderText = "1–16 个字符，中英文都行",
            MaxLength = 16,
            Text = "Steve",
            FontFamily = FontService.BodyFont
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加离线账户",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "离线账户不需要密码，名字就是进游戏里的名字。它也可以挂一张本地皮肤。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        FontFamily = FontService.BodyFont,
                        Foreground = ThemeService.Brush("SecondaryTextBrush")
                    },
                    box
                }
            },
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            MainWindow.Instance?.Toast("名字不能为空。", true);
            return;
        }

        var account = AccountService.AddOffline(name);
        MainWindow.Instance?.RefreshAccountUi();
        Refresh();
        MainWindow.Instance?.Toast($"已添加离线账户「{account.Name}」");
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null) return;

        MsLoginButton.IsEnabled = false;
        try
        {
            var ok = await MainWindow.Instance.MicrosoftLoginAsync();
            if (ok)
            {
                Refresh();
                MainWindow.Instance.RefreshAccountUi();
            }
        }
        finally
        {
            MsLoginButton.IsEnabled = true;
        }
    }

    private async Task RefreshTokenAsync(string accountId)
    {
        var account = AccountService.Store.FindById(accountId);
        if (account?.IsMicrosoft != true) return;

        if (string.IsNullOrEmpty(account.RefreshToken))
        {
            MainWindow.Instance?.Toast("这个账户没有刷新令牌，请重新登录一次。", true);
            return;
        }

        try
        {
            var refreshed = await MsAuthService.RefreshAsync(account.RefreshToken!, default);
            if (refreshed is null)
            {
                MainWindow.Instance?.Toast("续期失败：刷新令牌可能已经失效，请重新登录。", true);
                return;
            }

            account.Name = refreshed.Name;
            account.Uuid = GameAccount.NormalizeUuid(refreshed.Uuid);
            account.AccessToken = refreshed.AccessToken;
            account.RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? account.RefreshToken : refreshed.RefreshToken;
            account.ExpiresAt = refreshed.ExpiresAt;
            AccountService.Save(account);

            Refresh();
            MainWindow.Instance?.Toast("正版令牌已续期。");
        }
        catch (Exception ex)
        {
            Log.Warn("手动续期失败：" + ex.Message);
            MainWindow.Instance?.Toast("续期失败：" + ex.Message, true);
        }
    }

    private async Task RemoveAsync(string accountId)
    {
        var account = AccountService.Store.FindById(accountId);
        if (account is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除账户「{account.Name}」？",
            Content = new TextBlock
            {
                Text = account.IsMicrosoft
                    ? "这个账户的登录令牌和本地皮肤会一起删掉。微软账号本身不会受影响，之后还能重新登录。"
                    : "这个离线账户和它的本地皮肤会从本机删掉。",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = FontService.BodyFont
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AccountService.Remove(accountId);
        MainWindow.Instance?.RefreshAccountUi();
        Refresh();
        MainWindow.Instance?.Toast($"已删除账户「{account.Name}」");
    }

    // ================= 工具 =================

    private static Button SmallButton(string text)
    {
        return new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["CardActionButtonStyle"],
            FontFamily = FontService.BodyFont,
            FontSize = 12,
            MinWidth = 72,
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0)
        };
    }

    private static string FirstLetter(string name) =>
        string.IsNullOrWhiteSpace(name) ? "S" : name.Trim()[..1].ToUpperInvariant();

    private static string FormatUuid(string uuid)
    {
        if (string.IsNullOrEmpty(uuid) || uuid.Length != 32) return uuid;
        return $"{uuid[..8]}…{uuid[^4..]}";
    }

    private static bool IsFromButton(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is ButtonBase) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

}

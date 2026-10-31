using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace LinxLauncher.Views;

/// <summary>
/// 社区页：广场 + 私信。
///
/// 控件全部用 WinUI 自带的：SelectorBar 做页签、ListView 承载列表、PersonPicture 显示头像、
/// 按钮用默认外观 / AccentButtonStyle —— 不自己画按钮和卡片。
/// 列表数据走 ObservableCollection，DataTemplate 里只用 {Binding}（不用 x:Bind，见 XAML 注释）。
/// </summary>
public sealed partial class CommunityPage : Page
{
    private readonly ObservableCollection<CommunityPost> _posts = new();
    private readonly ObservableCollection<DmThread> _threads = new();
    private readonly ObservableCollection<DmMessage> _messages = new();
    private readonly List<ToggleButton> _tagButtons = new();
    private static readonly string[] DefaultTagLabels = { "闲聊", "求助", "服务器", "作品", "资源" };

    private CancellationTokenSource _cts = new();
    private CommunityMeta? _meta;
    private string _tag = "";
    private string _sort = "new";
    private int _page;
    private bool _loading;
    private bool _hasMore;
    private bool _loaded;
    private bool _threadsBusy;

    private long _activeThreadId;
    private long _lastMessageId;
    private bool _refreshingThreads;
    private DispatcherTimer? _chatTimer;

    public CommunityPage()
    {
        InitializeComponent();

        // 三个列表分别兜一层：AOT 下如果封送失败，至少要能说清是哪个崩的
        try { PostList.ItemsSource = _posts; }
        catch (Exception ex) { Log.Error("社区页 · 帖子列表绑定失败", ex); }

        try { ThreadList.ItemsSource = _threads; }
        catch (Exception ex) { Log.Error("社区页 · 会话列表绑定失败", ex); }

        try { MessageList.ItemsSource = _messages; }
        catch (Exception ex) { Log.Error("社区页 · 消息列表绑定失败", ex); }

        Loaded += Page_Loaded;
        Unloaded += (_, _) =>
        {
            _chatTimer?.Stop();
            try { _cts.Cancel(); } catch { }
        };
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();

        if (!_loaded)
        {
            _loaded = true;
            BuildTagRow();
            UpdateSortVisuals();
        }

        await RefreshAllAsync(silent: _posts.Count > 0);
    }

    private async Task RefreshAllAsync(bool silent)
    {
        await LoadMetaAsync();
        await LoadPostsAsync(reset: true, silent: silent);
        await LoadThreadsAsync();
        UpdateLoginState();
    }

    // ==================== 页签 ====================

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var plaza = sender.SelectedItem == PlazaTab;

        PlazaView.Visibility = plaza ? Visibility.Visible : Visibility.Collapsed;
        DmView.Visibility = plaza ? Visibility.Collapsed : Visibility.Visible;

        FadeIn(plaza ? PlazaView : DmView);

        if (plaza)
        {
            _chatTimer?.Stop();
        }
        else
        {
            _ = LoadThreadsAsync();
            if (_activeThreadId > 0) StartChatTimer();
        }
    }

    /// <summary>内容切换的淡入。只动 Opacity，不触发布局。</summary>
    private static void FadeIn(UIElement element)
    {
        element.Opacity = 0;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ==================== 概况与筛选 ====================

    private async Task LoadMetaAsync()
    {
        try
        {
            _meta = await CommunityService.GetMetaAsync(_cts.Token);
            MetaText.Text = $"共 {_meta.Posts} 帖 · {_meta.Users} 位玩家";
            BuildTagRow();
        }
        catch (OperationCanceledException) { }
        catch
        {
            MetaText.Text = "";
        }
    }

    private void BuildTagRow()
    {
        TagRow.Children.Clear();
        _tagButtons.Clear();

        AddTagButton("全部", "");
        if (_meta?.Tags.Count > 0)
        {
            foreach (var (key, label) in _meta.Tags)
                AddTagButton(label, key);
        }
        else
        {
            // 分类名称属于页面结构；接口暂不可用时仍保留，不猜测服务端的筛选键。
            foreach (var label in DefaultTagLabels)
            {
                var button = new ToggleButton
                {
                    Content = label,
                    IsEnabled = false,
                    Padding = new Thickness(12, 4, 12, 4),
                    MinHeight = 30,
                    CornerRadius = new CornerRadius(15),
                };
                CardAppearance.SetIsCard(button, true);
                TagRow.Children.Add(button);
            }
        }
    }

    private void AddTagButton(string label, string key)
    {
        // ToggleButton 是原生控件：选中/悬停样式、键盘操作都不用自己画
        var button = new ToggleButton
        {
            Content = label,
            Tag = key,
            IsChecked = key == _tag,
            Padding = new Thickness(12, 4, 12, 4),
            MinHeight = 30,
            CornerRadius = new CornerRadius(15),
        };
        CardAppearance.SetIsCard(button, true);
        button.Click += Tag_Click;
        _tagButtons.Add(button);
        TagRow.Children.Add(button);
    }

    private void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag) return;

        _tag = tag;
        foreach (var other in _tagButtons)
        {
            if (!ReferenceEquals(other, button)) other.IsChecked = false;
        }

        _ = LoadPostsAsync(reset: true);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string sort) return;

        if (sort == "mine" && !LinxAccountService.IsLoggedIn)
        {
            Toast("先在左下角登录 XML 账号，才能看自己的帖子。");
            return;
        }

        _sort = sort;
        UpdateSortVisuals();
        _ = LoadPostsAsync(reset: true);
    }

    private void UpdateSortVisuals()
    {
        var selected = ThemeService.Brush("BrandBrush");
        var normal = ThemeService.Brush("SecondaryTextBrush");

        foreach (var (button, key) in new[]
                 {
                     (SortNewButton, "new"), (SortHotButton, "hot"), (SortMineButton, "mine"),
                 })
        {
            var on = key == _sort;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            button.Foreground = on ? selected : normal;
        }
    }

    private void UpdateLoginState()
    {
        var logged = LinxAccountService.IsLoggedIn;
        SortMineButton.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;

        if (!logged)
        {
            DmHint.Text = "登录后可以给广场里的人发私信。点左下角头像登录。";
            SetUnreadBadge(0);
        }
        else if (_threads.Count == 0)
        {
            DmHint.Text = "还没有会话。点「新对话」，输入对方用户名。";
        }
        else
        {
            DmHint.Text = "";
        }
    }

    // ==================== 广场 ====================

    private async Task LoadPostsAsync(bool reset, bool silent = false)
    {
        if (_loading) return;
        _loading = true;

        if (reset) _page = 0;
        HideState();
        if (!silent) SetLoading(true);

        try
        {
            var items = await CommunityService.GetPostsAsync(_sort, _tag, _page, _cts.Token);

            if (reset) _posts.Clear();
            foreach (var item in items) _posts.Add(item);

            _hasMore = items.Count >= 20;
            MoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;

            if (_posts.Count == 0) ShowState(EmptyText(), withRetry: false);
        }
        catch (OperationCanceledException) { }
        catch (CommunityException ex)
        {
            ShowState(ex.Message, withRetry: true);
        }
        catch (Exception ex)
        {
            ShowState("连不上服务器：" + ex.Message, withRetry: true);
        }
        finally
        {
            _loading = false;
            SetLoading(false);
        }
    }

    private string EmptyText()
    {
        if (_sort == "mine") return "你还没发过帖子。第一个问题写具体点：版本、加载器、报错原文，别人才答得上来。";
        if (!string.IsNullOrEmpty(_tag)) return "这个分类下还没有帖子。要不你来开第一楼？";
        if (!LinxAccountService.IsLoggedIn) return "广场还是空的。登录服务器账号后，可以发第一帖。";
        return "广场还是空的。发个问题、贴个存档截图，或者约一局。";
    }

    private void PostList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommunityPost post) _ = ShowPostAsync(post.Id);
    }

    private void Comment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is long id) _ = ShowPostAsync(id);
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not long id) return;
        if (!RequireLogin("点赞")) return;

        var post = _posts.FirstOrDefault(p => p.Id == id);
        if (post is null) return;

        try
        {
            var (liked, likes) = await CommunityService.ToggleLikeAsync(id, _cts.Token);
            post.Liked = liked;
            post.Likes = likes;
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is long id) _ = ReportAsync("post", id);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !_hasMore) return;
        _page++;
        _ = LoadPostsAsync(reset: false);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAllAsync(silent: true);

    private void StateAction_Click(object sender, RoutedEventArgs e) => _ = RefreshAllAsync(silent: false);

    private void SetLoading(bool on)
    {
        LoadingRing.IsActive = on;
        LoadingRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowState(string text, bool withRetry)
    {
        StateText.Text = text;
        StateAction.Visibility = withRetry ? Visibility.Visible : Visibility.Collapsed;
        StateHost.Visibility = Visibility.Visible;
    }

    private void HideState() => StateHost.Visibility = Visibility.Collapsed;

    // ==================== 发帖 ====================

    private void Compose_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLogin("发帖")) return;
        _ = ComposeAsync();
    }

    private async Task ComposeAsync()
    {
        var tags = _meta?.Tags.Count > 0
            ? _meta.Tags
            : new List<(string Key, string Label)> { ("chat", "闲聊") };

        var title = new TextBox { Header = "标题", MaxLength = 80, PlaceholderText = "一句话说清楚，例如：1.21 进世界后方块全黑" };
        var body = new TextBox
        {
            Header = "正文",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            MaxLength = 4000,
            PlaceholderText = "版本、加载器、报错原文、已经试过什么 —— 写得越具体，回得越快。",
        };
        var tag = new ComboBox
        {
            Header = "分类",
            ItemsSource = tags.Select(t => t.Label).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 12, Width = 520 };
        panel.Children.Add(title);
        panel.Children.Add(body);
        panel.Children.Add(tag);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "发帖",
            Content = panel,
            PrimaryButtonText = "发布",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var titleText = title.Text.Trim();
        var bodyText = body.Text.Trim();
        if (titleText.Length < 4) { Toast("标题至少 4 个字。", true); return; }
        if (bodyText.Length == 0) { Toast("正文不能为空。", true); return; }

        try
        {
            var key = tags[Math.Clamp(tag.SelectedIndex, 0, tags.Count - 1)].Key;
            var created = await CommunityService.CreatePostAsync(titleText, bodyText, key, _cts.Token);
            _sort = "new";
            UpdateSortVisuals();
            await LoadPostsAsync(reset: true);
            Toast(created.Status == 1 ? "已提交，审核通过后其他人才能看到。" : "已发布。");
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
    }

    // ==================== 帖子详情 ====================

    private async Task ShowPostAsync(long id)
    {
        CommunityPost post;
        List<CommunityComment> comments;

        try
        {
            (post, comments) = await CommunityService.GetPostAsync(id, _cts.Token);
        }
        catch (CommunityException ex) { Toast(ex.Message, true); return; }
        catch (OperationCanceledException) { return; }

        var root = new StackPanel { Spacing = 12, Width = 560 };

        root.Children.Add(new TextBlock
        {
            Text = post.Title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("PrimaryTextBrush"),
        });

        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        metaRow.Children.Add(new TextBlock { Text = post.Author, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("PrimaryTextBrush") });
        metaRow.Children.Add(new TextBlock { Text = post.TimeText, FontSize = 12, Foreground = Res("SecondaryTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        metaRow.Children.Add(new TextBlock { Text = "#" + post.TagLabel, FontSize = 12, Foreground = Res("LinkTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        if (post.Status != 0)
        {
            var status = new Border
            {
                Background = post.StatusBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = post.StatusLabel, FontSize = 10.5, Foreground = post.StatusForeground },
            };
            CardAppearance.SetIsCard(status, true);
            metaRow.Children.Add(status);
        }
        root.Children.Add(metaRow);

        root.Children.Add(new TextBlock
        {
            Text = post.Body,
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Res("PrimaryTextBrush"),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var likeButton = new Button
        {
            Padding = new Thickness(8, 2, 8, 2),
            Content = LikeContent(post),
        };
        likeButton.Click += async (_, _) =>
        {
            if (!RequireLogin("点赞")) return;
            try
            {
                var (liked, likes) = await CommunityService.ToggleLikeAsync(post.Id, _cts.Token);
                post.Liked = liked;
                post.Likes = likes;
                likeButton.Content = LikeContent(post);

                var cached = _posts.FirstOrDefault(p => p.Id == post.Id);
                if (cached is not null) { cached.Liked = liked; cached.Likes = likes; }
            }
            catch (CommunityException ex) { Toast(ex.Message, true); }
        };
        actions.Children.Add(likeButton);

        if (LinxAccountService.IsLoggedIn)
        {
            var report = new Button { Content = "举报", Padding = new Thickness(10, 2, 10, 2) };
            report.Click += async (_, _) => await ReportAsync("post", post.Id);
            actions.Children.Add(report);
        }

        if (CanDeletePost(post))
        {
            var delete = new Button { Content = "删除", Padding = new Thickness(10, 2, 10, 2) };
            delete.Click += async (_, _) => await DeletePostAsync(post);
            actions.Children.Add(delete);
        }

        root.Children.Add(actions);
        root.Children.Add(new Border { Height = 1, Background = Res("DividerBrush"), Margin = new Thickness(0, 4, 0, 4) });

        var countText = new TextBlock
        {
            Text = $"评论 {comments.Count}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("PrimaryTextBrush"),
        };
        root.Children.Add(countText);

        var commentHost = new StackPanel { Spacing = 14 };
        foreach (var comment in comments)
            commentHost.Children.Add(BuildCommentRow(comment, commentHost, countText, post));
        root.Children.Add(commentHost);

        var input = new TextBox
        {
            PlaceholderText = "写下你的回复…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 62,
            MaxLength = 1000,
        };

        var send = new Button
        {
            Content = "发送评论",
            Style = (Style)Resources["BrandButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var inputRow = new Grid { ColumnSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(input);
        Grid.SetColumn(send, 1);
        inputRow.Children.Add(send);
        root.Children.Add(inputRow);

        send.Click += async (_, _) =>
        {
            if (!RequireLogin("评论")) return;
            var text = input.Text.Trim();
            if (text.Length == 0) return;

            send.IsEnabled = false;
            try
            {
                var created = await CommunityService.AddCommentAsync(post.Id, text, _cts.Token);
                created.CanDelete = true;
                commentHost.Children.Add(BuildCommentRow(created, commentHost, countText, post));
                countText.Text = $"评论 {commentHost.Children.Count}";
                input.Text = "";

                post.Comments = commentHost.Children.Count;
            }
            catch (CommunityException ex) { Toast(ex.Message, true); }
            catch (OperationCanceledException) { }
            finally { send.IsEnabled = true; }
        };

        var scroll = new ScrollViewer { Content = root, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Content = scroll,
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        FontService.ApplyToDialog(dialog);

        await dialog.ShowAsync();
    }

    private static StackPanel LikeContent(CommunityPost post) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        Children =
        {
            new FontIcon
            {
                Glyph = "\uE8E1",
                FontSize = 13,
                Foreground = post.Liked
                    ? ThemeService.Brush("BrandBrush")
                    : ThemeService.Brush("SecondaryTextBrush"),
            },
            new TextBlock
            {
                Text = post.LikeText,
                FontSize = 12,
                Foreground = post.Liked
                    ? ThemeService.Brush("BrandBrush")
                    : ThemeService.Brush("SecondaryTextBrush"),
            },
        },
    };

    private async Task DeletePostAsync(CommunityPost post)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这帖？",
            Content = "删除后广场上不再显示，也无法恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await CommunityService.DeletePostAsync(post.Id, _cts.Token);
            var cached = _posts.FirstOrDefault(p => p.Id == post.Id);
            if (cached is not null) _posts.Remove(cached);
            Toast("已删除。");
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
    }

    private FrameworkElement BuildCommentRow(CommunityComment comment, Panel host, TextBlock countText, CommunityPost post)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new PersonPicture
        {
            Width = 28,
            Height = 28,
            DisplayName = comment.Author,
            ProfilePicture = string.IsNullOrEmpty(comment.AvatarUrl) ? null : new BitmapImage(new Uri(comment.AvatarUrl)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(avatar);

        var body = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = comment.Author,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("PrimaryTextBrush"),
        });
        head.Children.Add(new TextBlock
        {
            Text = comment.TimeText,
            FontSize = 11,
            Foreground = Res("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = comment.Body,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Res("PrimaryTextBrush"),
        });

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        if (comment.CanDelete)
        {
            var delete = new Button
            {
                Content = "删除",
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Top,
            };
            delete.Click += async (_, _) =>
            {
                try
                {
                    await CommunityService.DeleteCommentAsync(comment.Id, _cts.Token);
                    host.Children.Remove(grid);
                    countText.Text = $"评论 {host.Children.Count}";
                    post.Comments = host.Children.Count;
                }
                catch (CommunityException ex) { Toast(ex.Message, true); }
                catch (OperationCanceledException) { }
            };
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);
        }

        return grid;
    }

    private static bool CanDeletePost(CommunityPost post)
        => LinxAccountService.IsLoggedIn &&
           string.Equals(post.Author, LinxAccountService.DisplayName, StringComparison.OrdinalIgnoreCase);

    private async Task ReportAsync(string kind, long targetId)
    {
        var box = new TextBox
        {
            PlaceholderText = "说明原因（可选，最多 300 字）",
            MaxLength = 300,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "举报这条内容",
            Content = box,
            PrimaryButtonText = "提交举报",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await CommunityService.ReportAsync(kind, targetId, box.Text.Trim(), _cts.Token);
            Toast("已收到举报，管理员会尽快处理。");
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
    }

    // ==================== 私信 ====================

    private async Task LoadThreadsAsync()
    {
        if (_threadsBusy) return;

        if (!LinxAccountService.IsLoggedIn)
        {
            _threads.Clear();
            SetUnreadBadge(0);
            UpdateLoginState();
            return;
        }

        _threadsBusy = true;
        try
        {
            var threads = await CommunityService.GetThreadsAsync(_cts.Token);

            _refreshingThreads = true;
            _threads.Clear();
            foreach (var thread in threads) _threads.Add(thread);

            if (_activeThreadId > 0)
            {
                var current = _threads.FirstOrDefault(t => t.Id == _activeThreadId);
                if (current is not null) ThreadList.SelectedItem = current;
            }
            _refreshingThreads = false;

            SetUnreadBadge(threads.Sum(t => t.Unread));
            UpdateLoginState();
        }
        catch (OperationCanceledException) { }
        catch (CommunityException ex)
        {
            DmHint.Text = "私信加载失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            DmHint.Text = "私信加载失败：" + ex.Message;
        }
        finally
        {
            _threadsBusy = false;
        }
    }

    private void SetUnreadBadge(int unread)
        => DmTab.Text = unread > 0 ? $"私信 · {unread}" : "私信";

    private void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingThreads) return;
        if (ThreadList.SelectedItem is DmThread thread)
            _ = OpenThreadAsync(thread);
    }

    private async Task OpenThreadAsync(DmThread thread)
    {
        _activeThreadId = thread.Id;
        _lastMessageId = 0;
        _messages.Clear();

        ChatEmpty.Visibility = Visibility.Collapsed;
        ChatHeader.Visibility = Visibility.Visible;
        ChatHeaderDivider.Visibility = Visibility.Visible;
        ChatComposer.Visibility = Visibility.Visible;

        ChatName.Text = thread.Peer;
        ChatSub.Text = "消息会保存在服务器上";
        ChatAvatar.DisplayName = thread.Peer;
        ChatAvatar.ProfilePicture = string.IsNullOrEmpty(thread.AvatarUrl)
            ? null
            : new BitmapImage(new Uri(thread.AvatarUrl));

        StartChatTimer();
        await PollMessagesAsync(quiet: false);
    }

    private void StartChatTimer()
    {
        _chatTimer?.Stop();
        _chatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chatTimer.Tick += async (_, _) => await PollMessagesAsync(quiet: true);
        _chatTimer.Start();
    }

    private async Task PollMessagesAsync(bool quiet)
    {
        if (_activeThreadId <= 0) return;

        try
        {
            var items = await CommunityService.GetMessagesAsync(_activeThreadId, _lastMessageId, _cts.Token);
            foreach (var item in items)
            {
                _messages.Add(item);
                _lastMessageId = Math.Max(_lastMessageId, item.Id);
            }

            if (items.Count > 0)
            {
                ScrollChatToEnd();
                _ = LoadThreadsAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (CommunityException ex)
        {
            if (!quiet) Toast(ex.Message, true);
        }
    }

    private async Task SendChatAsync()
    {
        if (_activeThreadId <= 0) return;

        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;

        ChatSend.IsEnabled = false;
        try
        {
            var message = await CommunityService.SendMessageAsync(_activeThreadId, text, _cts.Token);
            _messages.Add(message);
            _lastMessageId = Math.Max(_lastMessageId, message.Id);
            ChatInput.Text = "";
            ScrollChatToEnd();
            await LoadThreadsAsync();
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
        finally { ChatSend.IsEnabled = true; }
    }

    private void ScrollChatToEnd()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_messages.Count > 0) MessageList.ScrollIntoView(_messages[^1]);
        });
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private void NewDm_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLogin("发私信")) return;
        _ = NewDmAsync();
    }

    private async Task NewDmAsync()
    {
        var box = new TextBox
        {
            Header = "对方用户名",
            PlaceholderText = "完整用户名，例如 Steve",
            MaxLength = 24,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新对话",
            Content = box,
            PrimaryButtonText = "打开会话",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        FontService.ApplyToDialog(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;

        try
        {
            var threadId = await CommunityService.OpenDmAsync(name, _cts.Token);
            await LoadThreadsAsync();

            var thread = _threads.FirstOrDefault(t => t.Id == threadId);
            if (thread is not null)
            {
                CommunityTabs.SelectedItem = DmTab;
                ThreadList.SelectedItem = thread;
            }
        }
        catch (CommunityException ex) { Toast(ex.Message, true); }
        catch (OperationCanceledException) { }
    }

    // ==================== 小工具 ====================

    private bool RequireLogin(string action)
    {
        if (LinxAccountService.IsLoggedIn) return true;
        Toast($"先在左下角登录 XML 账号，才能{action}。");
        return false;
    }

    private static Brush Res(string key) => ThemeService.Brush(key);

    private static void Toast(string message, bool error = false) => MainWindow.Instance?.Toast(message, error);
}

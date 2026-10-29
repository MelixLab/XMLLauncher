using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LinxLauncher.Services;

/// <summary>
/// 社区接口封装（帖子 / 评论 / 点赞 / 私信 / 举报）。
///
/// 设计上的几个取舍：
/// 1. 全部用 JsonNode / JsonDocument 手动解析，不新增 [JsonSerializable] 类型 ——
///    Native AOT 版不会因为漏登记模型而在运行时炸掉。
/// 2. 错误统一抛 <see cref="CommunityException"/>，页面层只负责把 message 显示出来。
/// 3. 服务端返回的正文一律当纯文本用（TextBlock），不解析 HTML / Markdown ——
///    这是防 XSS 的最后一道，也是最省事的一道。
/// </summary>
public static class CommunityService
{
    // 模型类见文件末尾（顶级类型，XAML 的 x:DataType 才能引用）

    // ==================== 公开方法 ====================

    public static async Task<CommunityMeta> GetMetaAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/meta", null, ct, "GET");
        var root = doc.RootElement;
        var meta = new CommunityMeta
        {
            Posts = (int)Num(root, "posts"),
            Users = (int)Num(root, "users"),
        };
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
                meta.Tags.Add((Str(t, "key"), Str(t, "label")));
        }
        return meta;
    }

    /// <summary>sort = new / hot / mine。未登录只能看 new / hot。</summary>
    public static async Task<List<CommunityPost>> GetPostsAsync(string sort, string tag, int page, CancellationToken ct)
    {
        var query = $"?sort={Uri.EscapeDataString(sort)}&page={page}&limit=20";
        if (!string.IsNullOrEmpty(tag)) query += "&tag=" + Uri.EscapeDataString(tag);

        using var doc = await CallAsync("/api/community/posts" + query, null, ct, "GET");
        return ParsePosts(doc.RootElement);
    }

    public static async Task<(CommunityPost Post, List<CommunityComment> Comments)> GetPostAsync(long id, CancellationToken ct)
    {
        using var doc = await CallAsync($"/api/community/posts/{id}", null, ct, "GET");
        var root = doc.RootElement;
        var post = ParsePost(root.GetProperty("post"));
        var comments = new List<CommunityComment>();

        if (root.TryGetProperty("comments", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                comments.Add(new CommunityComment
                {
                    Id = Num(c, "id"),
                    PostId = Num(c, "post_id"),
                    Author = Str(c, "author"),
                    AuthorId = Num(c, "author_id"),
                    HasAvatar = Bool(c, "has_avatar"),
                    AvatarUrl = Bool(c, "has_avatar") ? CommunityService.AvatarUrl(Num(c, "author_id")) : "",
                    Body = Str(c, "body"),
                    CreatedAt = Num(c, "created_at"),
                    CanDelete = CanDelete(Str(c, "author"), Num(c, "author_id")),
                });
            }
        }
        return (post, comments);
    }

    public static async Task<CommunityPost> CreatePostAsync(string title, string body, string tag, CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/posts", new JsonObject
        {
            ["title"] = title,
            ["body"] = body,
            ["tag"] = tag,
        }, ct);
        return ParsePost(doc.RootElement.GetProperty("post"));
    }

    public static async Task DeletePostAsync(long id, CancellationToken ct)
    {
        using var _ = await CallAsync($"/api/community/posts/{id}", null, ct, "DELETE");
    }

    public static async Task<(bool Liked, int Likes)> ToggleLikeAsync(long id, CancellationToken ct)
    {
        using var doc = await CallAsync($"/api/community/posts/{id}/like", new JsonObject(), ct);
        return (Bool(doc.RootElement, "liked"), (int)Num(doc.RootElement, "likes"));
    }

    public static async Task<CommunityComment> AddCommentAsync(long postId, string body, CancellationToken ct)
    {
        using var doc = await CallAsync($"/api/community/posts/{postId}/comments", new JsonObject
        {
            ["body"] = body,
        }, ct);

        var c = doc.RootElement.GetProperty("comment");
        return new CommunityComment
        {
            Id = Num(c, "id"),
            PostId = Num(c, "post_id"),
            Author = Str(c, "author"),
            AuthorId = Num(c, "author_id"),
            HasAvatar = Bool(c, "has_avatar"),
            AvatarUrl = Bool(c, "has_avatar") ? CommunityService.AvatarUrl(Num(c, "author_id")) : "",
            Body = Str(c, "body"),
            CreatedAt = Num(c, "created_at"),
            CanDelete = true,
        };
    }

    public static async Task DeleteCommentAsync(long id, CancellationToken ct)
    {
        using var _ = await CallAsync($"/api/community/comments/{id}", null, ct, "DELETE");
    }

    public static async Task<MyStats> GetMeAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/me", null, ct, "GET");
        var root = doc.RootElement;
        var stats = new MyStats
        {
            Posts = (int)Num(root, "posts"),
            LikesReceived = (int)Num(root, "likes_received"),
            UnreadDm = (int)Num(root, "unread_dm"),
        };
        if (root.TryGetProperty("user", out var u))
            stats.Role = Str(u, "role", "user");
        return stats;
    }

    public static async Task<List<CommunityUser>> SearchUsersAsync(string keyword, CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/users?q=" + Uri.EscapeDataString(keyword), null, ct, "GET");
        var items = new List<CommunityUser>();
        if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in arr.EnumerateArray())
                items.Add(new CommunityUser { Id = Num(u, "id"), Username = Str(u, "username") });
        }
        return items;
    }

    public static async Task<List<DmThread>> GetThreadsAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/dm/threads", null, ct, "GET");
        var items = new List<DmThread>();
        if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                items.Add(new DmThread
                {
                    Id = Num(t, "id"),
                    PeerId = Num(t, "peer_id"),
                    HasAvatar = Bool(t, "has_avatar"),
                    AvatarUrl = Bool(t, "has_avatar") ? CommunityService.AvatarUrl(Num(t, "peer_id")) : "",
                    Peer = Str(t, "peer"),
                    Last = Str(t, "last"),
                    UpdatedAt = Num(t, "updated_at"),
                    Unread = (int)Num(t, "unread"),
                });
            }
        }
        return items;
    }

    public static async Task<long> OpenDmAsync(string username, CancellationToken ct)
    {
        using var doc = await CallAsync("/api/community/dm/open", new JsonObject
        {
            ["username"] = username,
        }, ct);
        return Num(doc.RootElement.GetProperty("thread"), "id");
    }

    public static async Task<List<DmMessage>> GetMessagesAsync(long threadId, long after, CancellationToken ct)
    {
        using var doc = await CallAsync($"/api/community/dm/{threadId}?after={after}", null, ct, "GET");
        var items = new List<DmMessage>();
        if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                items.Add(new DmMessage
                {
                    Id = Num(m, "id"),
                    Mine = Bool(m, "mine"),
                    Body = Str(m, "body"),
                    CreatedAt = Num(m, "created_at"),
                });
            }
        }
        return items;
    }

    public static async Task<DmMessage> SendMessageAsync(long threadId, string body, CancellationToken ct)
    {
        using var doc = await CallAsync($"/api/community/dm/{threadId}", new JsonObject
        {
            ["body"] = body,
        }, ct);

        var m = doc.RootElement.GetProperty("message");
        return new DmMessage
        {
            Id = Num(m, "id"),
            Mine = true,
            Body = Str(m, "body"),
            CreatedAt = Num(m, "created_at"),
        };
    }

    public static async Task MarkReadAsync(long threadId, long lastId, CancellationToken ct)
    {
        using var _ = await CallAsync($"/api/community/dm/{threadId}/read", new JsonObject
        {
            ["last_id"] = lastId,
        }, ct);
    }

    public static async Task ReportAsync(string kind, long targetId, string reason, CancellationToken ct)
    {
        using var _ = await CallAsync("/api/community/report", new JsonObject
        {
            ["kind"] = kind,
            ["target_id"] = targetId,
            ["reason"] = reason,
        }, ct);
    }

    /// <summary>服务器上的头像地址。没传过头像的用户这个地址会 404，所以先看 has_avatar。</summary>
    public static string AvatarUrl(long userId) => $"{RemoteService.BaseUrl}/api/avatar/{userId}";

    // ==================== 内部 ====================

    private static async Task<JsonDocument> CallAsync(string path, JsonObject? payload, CancellationToken ct, string method = "POST")
    {
        var (ok, doc, error) = await LinxAccountService.CallAsync(path, payload, ct, method);
        if (!ok)
        {
            doc?.Dispose();
            throw new CommunityException(error);
        }
        return doc ?? throw new CommunityException("服务器返回了无法解析的内容。");
    }

    private static List<CommunityPost> ParsePosts(JsonElement root)
    {
        var list = new List<CommunityPost>();
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            list.AddRange(arr.EnumerateArray().Select(ParsePost));
        return list;
    }

    private static CommunityPost ParsePost(JsonElement e) => new()
    {
        Id = Num(e, "id"),
        Title = Str(e, "title"),
        Body = Str(e, "body"),
        Excerpt = Str(e, "excerpt"),
        Tag = Str(e, "tag", "chat"),
        TagLabel = Str(e, "tag_label", "闲聊"),
        Author = Str(e, "author"),
        AuthorId = Num(e, "author_id"),
        HasAvatar = Bool(e, "has_avatar"),
        CreatedAt = Num(e, "created_at"),
        Likes = (int)Num(e, "likes"),
        Comments = (int)Num(e, "comments"),
        Views = (int)Num(e, "views"),
        Liked = Bool(e, "liked"),
        Pinned = Bool(e, "pinned"),
        Status = (int)Num(e, "status"),
        AvatarUrl = Bool(e, "has_avatar") ? CommunityService.AvatarUrl(Num(e, "author_id")) : "",
    };

    private static string Str(JsonElement e, string name, string fallback = "")
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static long Num(JsonElement e, string name, long fallback = 0)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n)
            ? n
            : fallback;

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>能不能删这条内容：自己发的，或者是管理员。</summary>
    private static bool CanDelete(string author, long authorId)
    {
        if (!LinxAccountService.IsLoggedIn) return false;
        if (string.Equals(author, LinxAccountService.DisplayName, StringComparison.OrdinalIgnoreCase)) return true;
        // 管理员判断交给服务端（role 在 /me 里），这里只在本地名字对上时提前显示删除入口
        return false;
    }

    internal static string InitialOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var trimmed = name.Trim();
        return trimmed.Substring(0, 1).ToUpperInvariant();
    }

    /// <summary>「刚刚 / 12 分钟前 / 3 小时前 / 昨天 / 9 月 3 日」。</summary>
    public static string TimeTextOf(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        var span = DateTimeOffset.Now - time;

        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24 && time.Date == DateTimeOffset.Now.Date) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalHours < 48) return "昨天";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} 天前";
        return time.Month + " 月 " + time.Day + " 日";
    }
}

/// <summary>社区接口的人话错误（页面直接显示 Message）。</summary>
public sealed class CommunityException : Exception
{
    public CommunityException(string message) : base(message) { }
}

// ==================== 模型 ====================

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class CommunityPost : ModelBase
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string Tag { get; set; } = "chat";
    public string TagLabel { get; set; } = "闲聊";
    public string Author { get; set; } = "";
    public long AuthorId { get; set; }
    public bool HasAvatar { get; set; }
    public long CreatedAt { get; set; }
    public bool Pinned { get; set; }
    public Visibility PinnedVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>服务器头像地址；没传过头像就是空串，PersonPicture 会显示首字母。</summary>
    public string AvatarUrl { get; set; } = "";

    /// <summary>0 已发布 / 1 待审 / 2 未通过。只有作者自己能看到非 0 的内容。</summary>
    public int Status { get; set; }
    public bool Pending => Status == 1;
    public bool Rejected => Status == 2;
    public string StatusLabel => Status == 1 ? "审核中" : Status == 2 ? "未通过" : "";
    public Visibility StatusVisibility => Status == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Brush StatusBackground => Status == 1
        ? (Brush)Application.Current.Resources["UpdateBannerBackgroundBrush"]
        : (Brush)Application.Current.Resources["BrandSoftBrush"];
    public Brush StatusForeground => Status == 1
        ? (Brush)Application.Current.Resources["UpdateBannerTextBrush"]
        : (Brush)Application.Current.Resources["NoticeTextBrush"];

    private int _likes;
    public int Likes { get => _likes; set { if (_likes != value) { _likes = value; Raise(nameof(Likes)); Raise(nameof(LikeText)); } } }

    private bool _liked;
    public bool Liked
    {
        get => _liked;
        set
        {
            if (_liked == value) return;
            _liked = value;
            Raise(nameof(Liked));
            Raise(nameof(LikeText));
            Raise(nameof(LikeBackground));
        }
    }

    private int _comments;
    public int Comments
    {
        get => _comments;
        set
        {
            if (_comments == value) return;
            _comments = value;
            Raise(nameof(Comments));
            Raise(nameof(CommentText));
        }
    }

    public int Views { get; set; }

    /// <summary>不显示 0，省得一屏都是「评论 0」。</summary>
    public string LikeText => Likes <= 0 && !Liked ? "赞" : $"赞 {Likes}";
    public string CommentText => Comments <= 0 ? "评论" : $"评论 {Comments}";
    public string ViewText => Views <= 0 ? "" : $"{Views} 次浏览";
    public string TimeText => CommunityService.TimeTextOf(CreatedAt);
    public string Initial => CommunityService.InitialOf(Author);
    public bool HasViews => Views > 0;

    public Brush LikeBackground => Liked
        ? (Brush)Application.Current.Resources["BrandSoftBrush"]
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class CommunityComment
{
    public long Id { get; set; }
    public long PostId { get; set; }
    public string Author { get; set; } = "";
    public long AuthorId { get; set; }
    public bool HasAvatar { get; set; }
    public string AvatarUrl { get; set; } = "";
    public string Body { get; set; } = "";
    public long CreatedAt { get; set; }
    public string TimeText => CommunityService.TimeTextOf(CreatedAt);
    public string Initial => CommunityService.InitialOf(Author);
    public bool CanDelete { get; set; }
}

public sealed class CommunityUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Initial => CommunityService.InitialOf(Username);
}

public sealed class CommunityMeta
{
    public int Posts { get; set; }
    public int Users { get; set; }
    public List<(string Key, string Label)> Tags { get; } = new();
}

public sealed class MyStats
{
    public int Posts { get; set; }
    public int LikesReceived { get; set; }
    public int UnreadDm { get; set; }
    public string Role { get; set; } = "user";
    public bool IsAdmin => Role == "admin";
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DmThread
{
    public long Id { get; set; }
    public long PeerId { get; set; }
    public bool HasAvatar { get; set; }
    public string Peer { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string Last { get; set; } = "";
    public long UpdatedAt { get; set; }
    public int Unread { get; set; }
    public string Initial => CommunityService.InitialOf(Peer);
    public string TimeText => CommunityService.TimeTextOf(UpdatedAt);
    public string Preview => string.IsNullOrWhiteSpace(Last) ? "还没有消息" : Last.Replace("\n", " ");
    public string UnreadText => Unread > 0 ? (Unread > 99 ? "99+" : Unread.ToString()) : "";
    public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DmMessage
{
    public long Id { get; set; }
    public bool Mine { get; set; }
    public string Body { get; set; } = "";
    public long CreatedAt { get; set; }
    public string TimeText => CommunityService.TimeTextOf(CreatedAt);
    public HorizontalAlignment Align => Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush BubbleBackground => Mine
        ? (Brush)Application.Current.Resources["BrandBrush"]
        : (Brush)Application.Current.Resources["CardHoverBackgroundBrush"];
    public Brush BubbleForeground => Mine
        ? (Brush)Application.Current.Resources["OnBrandTextBrush"]
        : (Brush)Application.Current.Resources["PrimaryTextBrush"];
    public Thickness BubbleMargin => Mine ? new Thickness(56, 3, 0, 3) : new Thickness(0, 3, 56, 3);
}

/// <summary>极简的属性变更通知（只给点赞状态用，不值得为一个属性引入完整 MVVM）。</summary>
public abstract class ModelBase : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

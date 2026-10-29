using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>微软设备码登录的第一步返回。</summary>
public sealed class DeviceCodeInfo
{
    public string UserCode { get; init; } = "";
    public string DeviceCode { get; init; } = "";
    public string VerificationUri { get; init; } = "https://www.microsoft.com/link";
    public int Interval { get; init; } = 5;
    public int ExpiresIn { get; init; } = 900;
}

/// <summary>一个正版账号（Minecraft 服务令牌 + 刷新令牌）。</summary>
public sealed class MsAccount
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }

    /// <summary>角色档案里 skins[0].url（Mojang 的皮肤纹理地址），没设置皮肤时为空。</summary>
    public string? SkinUrl { get; set; }

    /// <summary>"classic" 或 "slim"。</summary>
    public string? SkinVariant { get; set; }

    public bool IsUsable => !string.IsNullOrEmpty(AccessToken) && ExpiresAt > DateTime.Now.AddMinutes(2);
}

/// <summary>
/// 微软正版登录。链路与 PCL2 一致：
///   设备码(login.live.com) → Xbox Live 用户令牌 → XSTS 授权 → Minecraft 服务令牌 → 角色档案。
/// PCL2 的 client_id 放在私有配置里，这里用微软给 Xbox 应用留的公开客户端 ID（很多启动器都用它）。
/// </summary>
public static class MsAuthService
{
    private const string ClientId = "00000000402b5328";
    private const string Scope = "XboxLive.signin offline_access";

    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private static HttpClient Client => Http.Client;

    // ================= 1. 设备码 =================

    public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code"
        });

        using var resp = await Client.PostAsync(DeviceCodeUrl, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new IOException("获取设备码失败：" + Explain(text));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        return new DeviceCodeInfo
        {
            UserCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() ?? "" : "",
            DeviceCode = root.TryGetProperty("device_code", out var dc) ? dc.GetString() ?? "" : "",
            VerificationUri = root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? "https://www.microsoft.com/link" : "https://www.microsoft.com/link",
            Interval = root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i) ? Math.Max(3, i) : 5,
            ExpiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e) ? e : 900
        };
    }

    // ================= 2. 轮询拿微软令牌 =================

    public static async Task<(string accessToken, string refreshToken)> PollDeviceCodeAsync(
        DeviceCodeInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresIn);
        int interval = info.Interval;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = info.DeviceCode
            });

            using var resp = await Client.PostAsync(TokenUrl, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
                var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(access)) throw new IOException("微软没有返回访问令牌。");
                return (access, refresh);
            }

            // authorization_pending / slow_down 都是「还没点完」，继续等
            if (text.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("正在等待你在网页上完成登录…");
                continue;
            }

            if (text.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += 5;
                continue;
            }

            if (text.Contains("authorization_declined", StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("你在网页上取消了登录。");

            if (text.Contains("expired_token", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("code_expired", StringComparison.OrdinalIgnoreCase))
                throw new IOException("设备码已过期，请重新登录。");

            throw new IOException("登录失败：" + Explain(text));
        }

        throw new IOException("等待登录超时，请重新登录。");
    }

    // ================= 3. 串令牌链路 =================

    public static async Task<MsAccount> ChainAsync(string msAccessToken, string refreshToken, CancellationToken ct)
    {
        // --- Xbox Live 用户令牌 ---
        // ★ 用 JsonObject 而不是匿名类型：匿名类型在 Native AOT 下没法做 JSON 序列化
        //   （源生成拿不到它的元数据），登录会直接抛异常。
        var xblBody = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msAccessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };

        var (xblToken, uhs) = await PostXboxAsync("https://user.auth.xboxlive.com/user/authenticate", xblBody, ct,
            "Xbox Live 登录失败");

        // --- XSTS 授权 ---
        var xstsBody = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(JsonValue.Create(xblToken))
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        };

        var (xstsToken, _) = await PostXboxAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsBody, ct,
            "Xbox 授权失败");

        // --- Minecraft 服务令牌 ---
        using (var body = JsonContent(new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" }))
        using (var resp = await Client.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", body, ct))
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new IOException("获取 Minecraft 令牌失败：" + Explain(text));

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var mcToken = root.TryGetProperty("access_token", out var t) ? t.GetString() ?? "" : "";
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 86400;

            // --- 角色档案 ---
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            using var profResp = await Client.SendAsync(req, ct);
            var profText = await profResp.Content.ReadAsStringAsync(ct);

            if (profResp.StatusCode == HttpStatusCode.NotFound)
                throw new IOException("这个微软账号还没有 Minecraft: Java 版（或者没有买断）。请用买过游戏的账号登录。");

            if (!profResp.IsSuccessStatusCode)
                throw new IOException("读取角色档案失败：" + Explain(profText));

            using var profDoc = JsonDocument.Parse(profText);
            var prof = profDoc.RootElement;

            var name = prof.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var uuid = prof.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uuid))
                throw new IOException("这个账号还没有创建游戏角色，去 minecraft.net 建一个再来。");

            // 角色档案里带着当前皮肤：state=ACTIVE 的那条就是要用的纹理
            var skins = ParseSkins(prof);

            return new MsAccount
            {
                Name = name,
                Uuid = uuid,
                AccessToken = mcToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.Now.AddSeconds(Math.Max(300, expiresIn - 60)),
                SkinUrl = skins.url,
                SkinVariant = skins.variant
            };
        }
    }

    /// <summary>从角色档案 JSON 里取当前皮肤地址和模型（没有皮肤就返回空）。</summary>
    public static (string? url, string? variant) ParseSkins(JsonElement profile)
    {
        try
        {
            if (!profile.TryGetProperty("skins", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (null, null);

            string? url = null, variant = null;
            foreach (var skin in arr.EnumerateArray())
            {
                var state = skin.TryGetProperty("state", out var st) ? st.GetString() : null;
                var u = skin.TryGetProperty("url", out var ue) ? ue.GetString() : null;
                var v = skin.TryGetProperty("variant", out var ve) ? ve.GetString() : null;

                if (string.IsNullOrEmpty(u)) continue;

                if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase) || url is null)
                {
                    url = u;
                    variant = v;
                    if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase)) break;
                }
            }

            return (url, variant);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>用 Minecraft 令牌读一次角色档案（换皮肤后刷新头像、同步皮肤用）。</summary>
    public static async Task<MsAccount?> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(text);
        var prof = doc.RootElement;

        var name = prof.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var uuid = prof.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uuid)) return null;

        var (skinUrl, variant) = ParseSkins(prof);

        return new MsAccount
        {
            Name = name,
            Uuid = uuid,
            AccessToken = accessToken,
            SkinUrl = skinUrl,
            SkinVariant = variant
        };
    }

    /// <summary>
    /// 把一张 64×64（或 64×32）PNG 传成这个正版账号的皮肤。
    /// variant 只接受 "classic" / "slim"。成功返回 true。
    /// </summary>
    public static async Task<bool> UploadSkinAsync(string accessToken, byte[] png, string variant, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken) || png.Length == 0) return false;

        var model = variant == "slim" ? "slim" : "classic";

        using var form = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(png);
        filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(filePart, "file", "skin.png");
        form.Add(new StringContent(model), "variant");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")
        {
            Content = form
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("上传皮肤失败：" + Explain(text));
            return false;
        }

        return true;
    }

    /// <summary>用刷新令牌续期（启动器每次启动/启动游戏前都可能用到）。</summary>
    public static async Task<MsAccount?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) return null;

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        });

        using var resp = await Client.PostAsync(TokenUrl, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("刷新正版令牌失败：" + Explain(text));
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? refreshToken : refreshToken;

        if (string.IsNullOrEmpty(access)) return null;

        return await ChainAsync(access, refresh, ct);
    }

    // ================= 工具 =================

    private static async Task<(string token, string uhs)> PostXboxAsync(string url, JsonObject payload, CancellationToken ct, string failText)
    {
        using var body = JsonContent(payload);
        using var resp = await Client.PostAsync(url, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new IOException(failText + "：" + ExplainXbox(text));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var token = root.TryGetProperty("Token", out var t) ? t.GetString() ?? "" : "";
        var uhs = "";

        if (root.TryGetProperty("DisplayClaims", out var claims) &&
            claims.TryGetProperty("xui", out var xui) &&
            xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0 &&
            xui[0].TryGetProperty("uhs", out var uhsEl))
        {
            uhs = uhsEl.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(token))
            throw new IOException(failText + "：服务器没有返回令牌");

        return (token, uhs);
    }

    private static StringContent JsonContent(JsonObject payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>把微软/Xbox 的错误翻成人话。</summary>
    private static string ExplainXbox(string text)
    {
        if (text.Contains("2148916233"))
            return "这个微软账号还没注册过 Xbox 账号。先去 xbox.com 用同一个账号登录一次（会免费创建）再回来。";

        if (text.Contains("2148916235"))
            return "你的账号所在地区不支持 Xbox Live。";

        if (text.Contains("2148916238"))
            return "这是儿童账号，需要家长在 Microsoft 家庭里添加并同意，才能登录。";

        if (text.Contains("2148916227"))
            return "这个 Xbox 账号已被封禁。";

        return Explain(text);
    }

    private static string Explain(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("error_description", out var d) && d.GetString() is { Length: > 0 } desc)
                return desc.Split('\n')[0];
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? text;
            if (root.TryGetProperty("Message", out var m) && m.GetString() is { Length: > 0 } msg)
                return msg;
        }
        catch { }

        return text.Length > 200 ? text[..200] : text;
    }
}

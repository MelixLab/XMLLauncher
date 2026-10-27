using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 服务器账号（和启动器的离线/正版账号是两码事）：
/// 只用来证明"你是本站用户"，联机房间、流量额度都挂在它上面。
/// </summary>
public static class LinxAccountService
{
    public static bool IsLoggedIn =>
        !string.IsNullOrEmpty(AppState.Settings.ServerToken) &&
        !string.IsNullOrEmpty(AppState.Settings.ServerUser);

    public static string DisplayName => AppState.Settings.ServerUser ?? "";

    /// <summary>
    /// 注册。服务端现在强制邮箱验证（LINX_REQUIRE_EMAIL 默认开），
    /// 必须带 email + 邮箱验证码，否则直接被拒。
    /// </summary>
    public static async Task<string> RegisterAsync(string username, string password, string email, string code, CancellationToken ct)
    {
        var (ok, body) = await PostAsync("/api/account/register", new JsonObject
        {
            ["username"] = username,
            ["password"] = password,
            ["email"] = email,
            ["code"] = code
        }, ct);

        if (!ok) throw new IOException2(ErrorMessage(body));
        return GetString(body, "message", "注册成功。");
    }

    /// <summary>
    /// 发邮箱验证码。测试服会把码直接返回（debug_code），正式服要靠收信。
    /// 返回 (给用户看的提示, 可能的测试码)。
    /// </summary>
    public static async Task<(string Message, string? DebugCode)> SendEmailCodeAsync(string email, string purpose, CancellationToken ct)
    {
        var (ok, body) = await PostAsync("/api/account/email/send", new JsonObject
        {
            ["email"] = email,
            ["purpose"] = purpose
        }, ct);

        if (!ok) throw new IOException2(ErrorMessage(body));

        var message = GetString(body, "message", "验证码已发送，请查收邮件（垃圾箱也看一眼）。");
        string? debug = null;
        try
        {
            if (body is not null &&
                body.RootElement.TryGetProperty("debug_code", out var dc) &&
                dc.ValueKind == JsonValueKind.String)
                debug = dc.GetString();
        }
        catch { }

        return (message, debug);
    }

    /// <summary>登录结果。NeedCode=true 表示服务端开了邮箱二次验证，得再补一次验证码。</summary>
    public readonly record struct LoginResult(bool LoggedIn, bool NeedCode, string Hint);

    /// <summary>
    /// 登录。code 为空时如果服务端要求邮箱二次验证，会返回 NeedCode 并触发一次发码。
    /// </summary>
    public static async Task<LoginResult> LoginAsync(string username, string password, string? code, CancellationToken ct)
    {
        var payload = new JsonObject { ["username"] = username, ["password"] = password };
        if (!string.IsNullOrWhiteSpace(code)) payload["code"] = code;

        var (ok, body) = await PostAsync("/api/account/login", payload, ct);
        if (!ok) throw new IOException2(ErrorMessage(body));

        var needCode = false;
        try
        {
            needCode = body is not null &&
                       body.RootElement.TryGetProperty("need_code", out var n) &&
                       n.ValueKind == JsonValueKind.True;
        }
        catch { }

        if (needCode)
            return new LoginResult(false, true, GetString(body, "hint", "验证码已发送到邮箱，请查收。"));

        var token = GetString(body, "token");
        if (string.IsNullOrEmpty(token)) throw new IOException2("服务器没有返回登录令牌。");

        AppState.Settings.ServerToken = token;
        AppState.Settings.ServerUser = username;
        SettingsService.Save();
        return new LoginResult(true, false, "");
    }

    /// <summary>把本地头像上传到服务器（网页端和别的设备都能看到）。</summary>
    public static async Task<bool> UploadAvatarAsync(string filePath, CancellationToken ct)
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(filePath, cts.Token);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "file", Path.GetFileName(filePath));

            using var request = new HttpRequestMessage(HttpMethod.Post, RemoteService.BaseUrl + "/api/account/avatar")
            {
                Content = form
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppState.Settings.ServerToken);

            using var resp = await Http.Client.SendAsync(request, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("上传头像失败：" + (int)resp.StatusCode);
                return false;
            }

            Log.Info("头像已同步到服务器");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("上传头像失败：" + ex.Message);
            return false;
        }
    }

    public static void Logout()
    {
        AppState.Settings.ServerToken = null;
        AppState.Settings.ServerUser = null;
        SettingsService.Save();
    }

    /// <summary>带鉴权头调用服务器（联机接口用）。</summary>
    public static async Task<(bool ok, JsonDocument? doc, string error)> CallAsync(
        string path, JsonObject? payload, CancellationToken ct, string method = "POST")
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(new HttpMethod(method), RemoteService.BaseUrl + path);

            var token = AppState.Settings.ServerToken;
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (payload is not null && method != "GET")
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await Http.Client.SendAsync(request, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); } catch { /* 非 JSON 响应 */ }

            if (!resp.IsSuccessStatusCode)
            {
                var message = doc is null ? $"服务器返回 {resp.StatusCode}" : ErrorMessage(doc);
                return (false, doc, message);
            }

            return (true, doc, "");
        }
        catch (OperationCanceledException)
        {
            return (false, null, "连接服务器超时。");
        }
        catch (Exception ex)
        {
            return (false, null, "连接服务器失败：" + ex.Message);
        }
    }

    // ================= 内部 =================

    private static async Task<(bool ok, JsonDocument? doc)> PostAsync(string path, JsonObject payload, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.Client.PostAsync(RemoteService.BaseUrl + path, content, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); } catch { }

            return (resp.IsSuccessStatusCode, doc);
        }
        catch (Exception ex)
        {
            throw new IOException2("连接服务器失败：" + ex.Message);
        }
    }

    private static string ErrorMessage(JsonDocument? doc)
    {
        if (doc is null) return "服务器返回了无法解析的内容。";
        return GetString(doc.RootElement, "error", "操作失败。");
    }

    private static string GetString(JsonDocument? doc, string name, string fallback = "")
        => doc is null ? fallback : GetString(doc.RootElement, name, fallback);

    private static string GetString(JsonElement element, string name, string fallback = "")
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
}

/// <summary>带人话消息的异常（避免到处 catch Exception 再拼字符串）。</summary>
public sealed class IOException2 : Exception
{
    public IOException2(string message) : base(message) { }
}

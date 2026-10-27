using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>搜索结果里的一个 Mod。</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class ModHit
{
    // 注意：这些属性必须是 set 而不是 init ——
    // [Bindable] 会让 XAML 编译器生成 setter 调用，init-only 属性会直接编译失败。
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public long Downloads { get; set; }
    public string? IconUrl { get; set; }

    public string DownloadsText => Downloads >= 100_000_000
        ? $"{Downloads / 100_000_000.0:0.#} 亿"
        : Downloads >= 10_000
            ? $"{Downloads / 10_000.0:0.#} 万"
            : Downloads.ToString("N0");

    public string AuthorText => string.IsNullOrEmpty(Author) ? "" : "by " + Author;
}

/// <summary>某个 Mod 在指定加载器 + 游戏版本下的一个可下载文件。</summary>
public sealed class ModFile
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public string Sha1 { get; init; } = "";
    public string VersionName { get; init; } = "";
    public List<string> RequiredProjectIds { get; init; } = new();
    public List<ModFile> Dependencies { get; init; } = new();
}

/// <summary>
/// Modrinth 公开 API（不需要密钥）。PCL2 也是这么接的：搜索带 facets，
/// 装的时候按加载器 + 游戏版本找最新 file，再把 required 依赖一起装上。
/// </summary>
public static class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";

    private static HttpClient Client
    {
        get
        {
            // Modrinth 要求带一个能识别身份的 UA
            if (!Http.Client.DefaultRequestHeaders.UserAgent.Any())
            {
                Http.Client.DefaultRequestHeaders.UserAgent.ParseAdd("XMLLauncher/1.0 (Minecraft launcher)");
            }
            return Http.Client;
        }
    }

    /// <summary>加载器名映射到 Modrinth 的 loader 标识。</summary>
    public static string? LoaderTag(string? displayKind) => displayKind switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "Quilt" => "quilt",
        _ => null
    };

    public static async Task<(List<ModHit> hits, int total)> SearchAsync(
        string query, string? loader, string? gameVersion, string sort, int offset, int limit, CancellationToken ct)
    {
        var url = new StringBuilder($"{Api}/search?query={Uri.EscapeDataString(query ?? "")}&limit={limit}&offset={offset}");

        sort = sort switch
        {
            "下载量" => "downloads",
            "最近更新" => "updated",
            "最新发布" => "newest",
            _ => "relevance"
        };
        url.Append("&index=").Append(sort);

        var facets = new List<string>();
        if (!string.IsNullOrEmpty(loader)) facets.Add($"[\"categories:{loader}\"]");
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (facets.Count > 0) url.Append("&facets=").Append(Uri.EscapeDataString("[" + string.Join(",", facets) + "]"));

        using var resp = await Client.GetAsync(url.ToString(), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new IOException("搜索失败：" + Explain(text));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var total = root.TryGetProperty("total_hits", out var t) && t.TryGetInt32(out var n) ? n : 0;
        var hits = new List<ModHit>();

        if (root.TryGetProperty("hits", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in arr.EnumerateArray())
            {
                hits.Add(new ModHit
                {
                    ProjectId = Str(h, "project_id"),
                    Slug = Str(h, "slug"),
                    Title = Str(h, "title"),
                    Description = Str(h, "description"),
                    Author = Str(h, "author"),
                    Downloads = h.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dd) ? dd : 0,
                    IconUrl = h.TryGetProperty("icon_url", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null
                });
            }
        }

        return (hits, total);
    }

    /// <summary>
    /// 找这个 Mod 在（加载器 + 游戏版本）下最新的一个文件，并把 required 依赖也解析出来。
    /// </summary>
    public static async Task<ModFile?> ResolveAsync(string projectId, string? loader, string? gameVersion, CancellationToken ct)
    {
        var file = await GetLatestFileAsync(projectId, loader, gameVersion, ct);
        if (file is null) return null;

        foreach (var depId in file.RequiredProjectIds.Take(8))
        {
            try
            {
                var dep = await GetLatestFileAsync(depId, loader, gameVersion, ct);
                if (dep is not null) file.Dependencies.Add(dep);
            }
            catch (Exception ex)
            {
                Log.Warn($"解析依赖 {depId} 失败：{ex.Message}");
            }
        }

        return file;
    }

    private static async Task<ModFile?> GetLatestFileAsync(string projectId, string? loader, string? gameVersion, CancellationToken ct)
    {
        var url = new StringBuilder($"{Api}/project/{Uri.EscapeDataString(projectId)}/version");
        var query = new List<string>();
        if (!string.IsNullOrEmpty(loader)) query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
        if (!string.IsNullOrEmpty(gameVersion)) query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (query.Count > 0) url.Append('?').Append(string.Join("&", query));

        using var resp = await Client.GetAsync(url.ToString(), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new IOException("获取文件列表失败：" + Explain(text));

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        // 取最新一个（Modrinth 已经按时间倒序）
        var v = doc.RootElement[0];

        if (!v.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            return null;

        // 优先选 primary 文件
        JsonElement chosen = files[0];
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True) { chosen = f; break; }
        }

        var deps = new List<string>();
        if (v.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depArr.EnumerateArray())
            {
                var type = Str(d, "dependency_type");
                var pid = Str(d, "project_id");
                if (type == "required" && !string.IsNullOrEmpty(pid) && pid != projectId) deps.Add(pid);
            }
        }

        return new ModFile
        {
            ProjectId = projectId,
            Url = Str(chosen, "url"),
            FileName = Str(chosen, "filename"),
            Size = chosen.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
            VersionName = Str(v, "version_number"),
            RequiredProjectIds = deps,
            Sha1 = chosen.TryGetProperty("hashes", out var hashes) ? Str(hashes, "sha1") : ""
        };
    }

    // ================= 更新检查 =================

    /// <summary>一个可以更新的 Mod。</summary>
    public sealed class ModUpdate
    {
        public string ProjectId { get; init; } = "";
        public string Title { get; init; } = "";
        public string LocalFile { get; init; } = "";
        public string LocalName { get; init; } = "";
        public string OldVersion { get; init; } = "";
        public string NewVersion { get; init; } = "";
        public string NewFileName { get; init; } = "";
        public string NewUrl { get; init; } = "";
        public string NewSha1 { get; init; } = "";
        public long NewSize { get; init; }
    }

    /// <summary>拿本地 jar 的 sha1 反查 Modrinth，返回 文件sha1 → 项目id。</summary>
    public static async Task<Dictionary<string, string>> ResolveFileHashesAsync(
        IReadOnlyList<string> sha1s, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0) return map;

        // 接口一次最多 1000 个，分批更稳
        foreach (var chunk in sha1s.Chunk(200))
        {
            // hashes 是数组，用 JsonArray 拼；匿名类型在 AOT 下没法序列化
            var hashArray = new JsonArray();
            foreach (var sha in chunk) hashArray.Add((JsonNode)JsonValue.Create(sha)!);
            var payload = new JsonObject
            {
                ["hashes"] = hashArray,
                ["algorithm"] = "sha1"
            }.ToJsonString();
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Client.PostAsync($"{Api}/version_files", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new IOException("查询文件失败：" + Explain(text));

            using var doc = JsonDocument.Parse(text);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var projectId = Str(prop.Value, "project_id");
                if (!string.IsNullOrEmpty(projectId)) map[prop.Name] = projectId;
            }
        }

        return map;
    }

    /// <summary>
    /// 扫一遍 mods 目录，找出哪些 Mod 在 Modrinth 上有更新的版本。
    /// 判断依据是文件 sha1：本地文件能对上某个已发布版本 → 再看这个项目当前最新版是不是同一份。
    /// </summary>
    public static async Task<List<ModUpdate>> CheckUpdatesAsync(
        string modsDir, string? loader, string? gameVersion, CancellationToken ct)
    {
        var result = new List<ModUpdate>();
        if (!Directory.Exists(modsDir)) return result;

        var jars = Directory.GetFiles(modsDir, "*.jar")
            .Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (jars.Count == 0) return result;

        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in jars)
        {
            try
            {
                var sha = Http.ComputeSha1(jar);
                byHash[jar] = sha;
                hashOf[jar] = sha;
            }
            catch (Exception ex)
            {
                Log.Warn($"计算 hash 失败 {jar}: {ex.Message}");
            }
        }

        var resolved = await ResolveFileHashesAsync(byHash.Values.Distinct().ToList(), ct);

        // 项目 → 本地文件
        var projectOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (jar, sha) in byHash)
        {
            if (resolved.TryGetValue(sha, out var projectId) && !projectOf.ContainsKey(projectId))
                projectOf[projectId] = jar;
        }

        if (projectOf.Count == 0) return result;

        foreach (var (projectId, jar) in projectOf)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var latest = await GetLatestFileAsync(projectId, loader, gameVersion, ct);
                if (latest is null || string.IsNullOrEmpty(latest.Url)) continue;

                var localSha = byHash[jar];
                if (string.Equals(latest.Sha1, localSha, StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new ModUpdate
                {
                    ProjectId = projectId,
                    LocalFile = jar,
                    LocalName = Path.GetFileName(jar),
                    OldVersion = "",
                    NewFileName = latest.FileName,
                    NewUrl = latest.Url,
                    NewSha1 = latest.Sha1,
                    NewSize = latest.Size,
                    NewVersion = latest.VersionName
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"检查更新失败 {projectId}: {ex.Message}");
            }
        }

        // 补上项目名（一次请求拿全部）
        if (result.Count > 0)
        {
            try
            {
                var ids = result.Select(r => r.ProjectId).Distinct().ToList();
                var idsJson = "[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]";
                var url = $"{Api}/projects?ids={Uri.EscapeDataString(idsJson)}";
                using var resp = await Client.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var proj in doc.RootElement.EnumerateArray())
                        titles[Str(proj, "id")] = Str(proj, "title");

                    for (int i = 0; i < result.Count; i++)
                    {
                        if (titles.TryGetValue(result[i].ProjectId, out var title) && !string.IsNullOrEmpty(title))
                        {
                            var r = result[i];
                            result[i] = new ModUpdate
                            {
                                ProjectId = r.ProjectId,
                                Title = title,
                                LocalFile = r.LocalFile,
                                LocalName = r.LocalName,
                                OldVersion = r.OldVersion,
                                NewVersion = r.NewVersion,
                                NewFileName = r.NewFileName,
                                NewUrl = r.NewUrl,
                                NewSha1 = r.NewSha1,
                                NewSize = r.NewSize
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("获取 Mod 名称失败：" + ex.Message);
            }
        }

        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Explain(string text) =>
        text.Length > 200 ? text[..200] : text;
}

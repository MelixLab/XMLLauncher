using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>汉化结果。</summary>
public sealed class TranslateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int Translated { get; set; }
    public string OutputPath { get; set; } = "";
    public List<(string Key, string En, string Zh)> Entries { get; set; } = new();
}

/// <summary>
/// 模组汉化：把 jar 里的 en_us.json 翻成中文，写成 zh_cn.json 塞回去。
///
/// 翻译走本机 Ollama（http://127.0.0.1:11434）。没装的话退化成
/// 「MC 常用词替换 + 保留原文」，并在结果里说清楚。
/// </summary>
public static class ModTranslator
{
    private const string OllamaUrl = "http://127.0.0.1:11434";

    public static async Task<TranslateResult> TranslateAsync(string jarPath, Action<string> status)
    {
        var result = new TranslateResult();

        try
        {
            if (!File.Exists(jarPath))
                return Fail("文件不存在。");

            status("正在读取模组…");

            // 找 lang 文件：assets/<modid>/lang/en_us.json
            var entries = new List<(string Key, string En)>();
            var langEntries = new List<string>();   // zip 内部路径

            using (var zip = ZipFile.OpenRead(jarPath))
            {
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    if (!name.EndsWith("/lang/en_us.json", StringComparison.OrdinalIgnoreCase)) continue;

                    using var s = e.Open();
                    using var reader = new StreamReader(s, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync();

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var v = prop.Value.GetString();
                        // 只翻能看出是英文的，已经是中文的跳过
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (ContainsCjk(v)) continue;
                        entries.Add((prop.Name, v));
                    }

                    langEntries.Add(name);
                }
            }

            if (entries.Count == 0)
                return Fail("这个模组里没找到英文语言文件（en_us.json）。");

            status($"找到 {entries.Count} 条文本，正在翻译…");

            var (translated, usedOllama) = await TranslateBatchAsync(entries, status);

            // 写成 zh_cn.json
            status("正在写回语言文件…");
            var output = Path.Combine(
                Path.GetDirectoryName(jarPath) ?? ".",
                Path.GetFileNameWithoutExtension(jarPath) + "_zh.jar");

            File.Copy(jarPath, output, true);

            var zhJson = JsonSerializer.Serialize(
                translated.ToDictionary(t => t.Key, t => t.Zh),
                new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                foreach (var langPath in langEntries)
                {
                    var zhPath = langPath[..^"en_us.json".Length] + "zh_cn.json";

                    var old = zip.GetEntry(zhPath);
                    old?.Delete();

                    var entry = zip.CreateEntry(zhPath, CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    await w.WriteAsync(zhJson);
                }
            }

            result.Success = true;
            result.Translated = translated.Count;
            result.OutputPath = output;
            result.Entries = translated.Select(t => (t.Key, t.En, t.Zh)).ToList();
            result.Message = usedOllama ? "已用本地模型翻译" : "未检测到本地模型，用了词表替换";
            return result;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        TranslateResult Fail(string msg) => new() { Success = false, Message = msg };
    }

    /// <summary>分批送翻译，避免一次塞太多把模型上下文撑爆。</summary>
    private static async Task<(List<(string Key, string En, string Zh)> list, bool usedOllama)>
        TranslateBatchAsync(List<(string Key, string En)> entries, Action<string> status)
    {
        var output = new List<(string, string, string)>();
        var ollama = await HasOllamaAsync();

        const int batch = 30;

        for (var i = 0; i < entries.Count; i += batch)
        {
            var slice = entries.Skip(i).Take(batch).ToList();
            status($"翻译中 {Math.Min(i + batch, entries.Count)}/{entries.Count}…");

            if (ollama)
            {
                var map = await TranslateWithOllamaAsync(slice);
                foreach (var e in slice)
                {
                    var zh = map.TryGetValue(e.Key, out var v) && !string.IsNullOrWhiteSpace(v)
                        ? v
                        : Fallback(e.En);
                    output.Add((e.Key, e.En, zh));
                }
            }
            else
            {
                foreach (var e in slice) output.Add((e.Key, e.En, Fallback(e.En)));
            }
        }

        return (output, ollama);
    }

    private static async Task<bool> HasOllamaAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var res = await http.GetAsync($"{OllamaUrl}/api/tags");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Dictionary<string, string>> TranslateWithOllamaAsync(
        List<(string Key, string En)> slice)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var prompt = new StringBuilder();
        prompt.AppendLine("把下面的 Minecraft 模组文本翻译成简体中文，保持术语一致（如 Block=方块、Item=物品、Chest=箱子）。");
        prompt.AppendLine("只输出 JSON 对象，键保持原样，值填中文译文，不要任何解释：");
        prompt.AppendLine("{");
        foreach (var (key, en) in slice)
        {
            prompt.AppendLine($"  {JsonSerializer.Serialize(key)}: {JsonSerializer.Serialize(en)},");
        }
        prompt.AppendLine("}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var body = JsonSerializer.Serialize(new
            {
                model = "qwen2.5:7b",
                prompt = prompt.ToString(),
                stream = false,
                format = "json",
            });

            var res = await http.PostAsync(
                $"{OllamaUrl}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"));

            if (!res.IsSuccessStatusCode) return map;

            var text = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return map;

            var raw = resp.GetString() ?? "";
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return map;

            using var inner = JsonDocument.Parse(raw[start..(end + 1)]);
            if (inner.RootElement.ValueKind != JsonValueKind.Object) return map;

            foreach (var p in inner.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
            }
        }
        catch
        {
            // 模型跑挂了就用词表兜底
        }

        return map;
    }

    /// <summary>没有模型时的兜底：只替换最常见的几个 MC 术语。</summary>
    private static string Fallback(string en)
    {
        var s = en;
        foreach (var (k, v) in CommonTerms)
        {
            s = s.Replace(k, v, StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    private static readonly (string, string)[] CommonTerms =
    {
        ("Block", "方块"), ("Item", "物品"), ("Chest", "箱子"), ("Ingot", "锭"),
        ("Ore", "矿石"), ("Sword", "剑"), ("Pickaxe", "镐"), ("Axe", "斧"),
        ("Shovel", "锹"), ("Helmet", "头盔"), ("Chestplate", "胸甲"),
        ("Leggings", "护腿"), ("Boots", "靴子"), ("Machine", "机器"),
        ("Cable", "线缆"), ("Energy", "能量"), ("Upgrade", "升级"),
        ("Recipe", "配方"), ("Crafting", "合成"), ("Furnace", "熔炉"),
    };

    /// <summary>判断是不是已经含中文，含了就跳过不翻。</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }
        return false;
    }
}

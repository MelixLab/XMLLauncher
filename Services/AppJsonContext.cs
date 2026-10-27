using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

/// <summary>
/// Native AOT 用的 JSON 元数据。
///
/// 反射式 System.Text.Json 在 AOT 下会直接失败（日志里那句
/// 「JsonPropertyInfo`1[Nullable`1[DateTime]] is missing native code or metadata」就是它），
/// 所以这里把**所有会被序列化/反序列化的类型**登记进来，让编译器提前生成元数据。
///
/// ★ 以后新增一个要存盘 / 走接口的模型，记得在这里补一行 [JsonSerializable]，
///   否则 AOT 版会在运行时抛 NotSupportedException（非 AOT 版靠反射兜底，看不出来）。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(InstanceSettings))]
[JsonSerializable(typeof(Dictionary<string, InstanceSettings>))]
[JsonSerializable(typeof(List<MetaEntry>))]
[JsonSerializable(typeof(List<ForgeEntry>))]
[JsonSerializable(typeof(List<NeoEntry>))]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(VersionDetail))]
[JsonSerializable(typeof(AssetIndexFile))]
[JsonSerializable(typeof(LocalVersion))]
[JsonSerializable(typeof(Announcement))]
[JsonSerializable(typeof(UpdateInfo))]
[JsonSerializable(typeof(MsAccount))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

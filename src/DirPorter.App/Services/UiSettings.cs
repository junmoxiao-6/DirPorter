using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DirPorter.Core.Config;
using DirPorter.Core.Links;

namespace DirPorter.App.Services;

/// <summary>
/// UI 偏好与常用项的持久化（需求：目标根目录「记忆上次选择」、扫描根目录预置与自定义、主题/语言）。
///
/// 存放位置跟随 <see cref="DirPorterPaths.DataRoot"/>（同样受 <c>DIRPORTER_DATA_ROOT</c> 覆盖），
/// 与迁移数据同处一地，便携模式下一并搬走。
///
/// 读取失败一律回退到默认值并保留文件（不删除用户文件），损坏时也不会让程序起不来。
/// </summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("language")] public string Language { get; set; } = LocalizationService.Languages.Chinese;
    [JsonPropertyName("theme")] public string Theme { get; set; } = ThemeService.Light;
    [JsonPropertyName("lastTargetRoot")] public string? LastTargetRoot { get; set; }
    [JsonPropertyName("defaultThreads")] public int DefaultThreads { get; set; } = 8;
    [JsonPropertyName("backupRetentionDays")] public int BackupRetentionDays { get; set; } = 7;

    /// <summary>更新清单地址（JSON：version/url/sha256/notes）。为空表示未配置更新源。</summary>
    [JsonPropertyName("updateFeedUrl")] public string UpdateFeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// 迁移/重建链接时默认使用的建链类型（junction / symlink-dir）。
    /// 默认 <see cref="LinkKind.Junction"/>：兼容性最好且普通权限即可创建。
    /// 迁移页、扫描页的「一键迁移并建链」都以此为默认值，用户可逐次改选。
    /// </summary>
    [JsonPropertyName("defaultLinkKind")] public string DefaultLinkKind { get; set; } = LinkKinds.ToStorageString(LinkKind.Junction);

    /// <summary><see cref="DefaultLinkKind"/> 的强类型视图；设置文件被手改坏时回退到目录联接。</summary>
    [JsonIgnore] public LinkKind DefaultLinkKindValue => LinkKinds.ParseOr(DefaultLinkKind, LinkKind.Junction);

    /// <summary>用户自定义的扫描根目录（预置项由界面动态生成，不写入此处）。</summary>
    [JsonPropertyName("scanRoots")] public List<string> ScanRoots { get; set; } = new();

    public static string FilePath => Path.Combine(DirPorterPaths.DataRoot, "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UiSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UiSettings>(json, Options) ?? new UiSettings();
        }
        catch
        {
            // 损坏或不可读：用默认值继续（不阻断启动）。
            return new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 存偏好失败不应影响主流程。
        }
    }
}

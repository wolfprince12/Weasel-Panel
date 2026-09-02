//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  面板**自身**的设置（目前只有界面语言）。
//
//  ⚠️ 落盘位置必须是 %APPDATA%\WeaselPanel\，**绝不能写进 Rime 用户目录**
//  （%APPDATA%\Rime）。理由：
//    1. Rime 用户目录是「同步目录 + 备份目录」的双重身份，BackupsManager 会把
//       里面的东西一起打进备份。面板自己的 UI 偏好混进去，备份就会带上与
//       输入法无关的噪音，恢复时还可能把用户的语言偏好改回旧值。
//    2. 该目录下的 .yaml 会被部署器扫描解析；多一个非 Rime 文件就是多一份
//       部署失败的风险。
//  这条与 macOS 侧「用户数据必须落在 ~/Library/Rime/」不矛盾：那边说的是
//  **输入法配置**，这边说的是**面板自己的偏好**。
//
//  GPL-3.0。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaselPanel.Core.Config;

public sealed class PanelSettings
{
    /// <summary>
    /// 界面语言。null 或 "auto" = 跟随系统（首次启动的默认行为）。
    /// 取值见 <c>WeaselPanel.App.Localization.L10n.SupportedLanguages</c>。
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>面板设置目录。Windows：%APPDATA%\WeaselPanel。</summary>
    public static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeaselPanel");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static PanelSettings Load() => LoadFrom(SettingsPath);

    /// <summary>从指定路径读取。读不到 / 解析失败一律返回默认设置 —— 设置文件
    /// 损坏不该让面板起不来，最坏的结果只是"回到跟随系统"。</summary>
    public static PanelSettings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PanelSettings();

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            return JsonSerializer.Deserialize<PanelSettings>(json, options) ?? new PanelSettings();
        }
        catch
        {
            return new PanelSettings();
        }
    }

    public void Save() => SaveTo(SettingsPath);

    /// <summary>写入指定路径。写失败静默吞掉：语言偏好存不下不影响使用这一次的面板。</summary>
    public void SaveTo(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions) + Environment.NewLine);
        }
        catch
        {
            // 设置写不进去就算了，绝不能因此打断用户正在做的操作
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

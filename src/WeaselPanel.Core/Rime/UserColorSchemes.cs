//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  自定义配色方案：模型、注册表（JSON）与注入 weasel.custom.yaml。
//  功能上对标 macOS 鼠须管面板的 UserColorSchemes.swift，但**不照搬其存储模型**。
//
//  ── 与 macOS 版的三处刻意差异（改之前先读）────────────────────────────
//
//  1) 可编辑通道是 22 个，不是 macOS 的 18~19 个。
//     依据：上游 RimeWithWeasel.cpp 的 _UpdateUIStyleColor() 逐个读取的键
//     （ColorSchemeResolver 已直译，顺序一致，本文件的 ColorKeys 与之逐条对应）。
//     macOS 侧的 preedit_* / label_back_color / label_candidate_* 在小狼毫里
//     **根本不存在**，写了不报错也不生效 —— 界面上多出几个滑块只会让人以为生效了。
//
//  2) 不做 macOS 的「最多同时启用 3 套」。
//     那是 macOS 版把配色同时写进 squirrel.custom.yaml 的多个槽位导致的产物；
//     小狼毫只有 style/color_scheme 一个当前方案，没有「启用集合」的概念。
//     本页只负责**定义**方案，选中哪一套是「外观」页的职责。
//     ⚠️ 因此本页绝不写 style/color_scheme —— 项目铁律：两个页面不得抢同一 YAML 键。
//
//  3) 方案 id 一律 ASCII。
//     macOS 版允许 CJK 进 id（正则含 一-龥）。这里收紧为纯 ASCII：
//     本机的 librime 是 Windows 构建，非 ASCII 配置路径（preset_color_schemes/墨色）
//     在本项目当前阶段**无法真机验证**，而中文显示名由 name 字段承载、不受影响。
//     拿不可验证的编码风险去换一个更短的 id，不划算。
//
//  ── 数据落点 ──────────────────────────────────────────────────────────
//     <用户目录>/user_color_schemes.json   ← 编辑器的唯一数据源（方案定义）
//     <用户目录>/weasel.custom.yaml        ← 派生产物：patch/preset_color_schemes/<id>
//  JSON 是「源」，YAML 是「编译产物」。删掉 YAML 里的条目没用 —— 下次应用会再写回来；
//     要删请在面板里删，让两边的 managedIds 一起收敛。
//
//  ── 为什么需要 appliedIds ─────────────────────────────────────────────
//  用户删掉一套方案时，光从 JSON 里移除是不够的：weasel.custom.yaml 里那条
//  preset_color_schemes/<id> 还在，小狼毫会一直读到它。appliedIds 记录
//  「上一次落盘写进去的 id 集合」，本次应用时用它做差集，把消失的方案从 YAML 里摘掉。
//  同时它也划清了责任边界：不是我们写进去的 preset_color_schemes/* 一律不动。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

/// <summary>
/// 一套配色里本面板可编辑的字段。
/// </summary>
public static class ColorSchemeFields
{
    /// <summary>
    /// 路径前缀：注入到 patch 下的 <c>preset_color_schemes/&lt;id&gt;</c>。
    /// </summary>
    public const string Node = "preset_color_schemes";

    public const string NameKey = "name";
    public const string AuthorKey = "author";
    public const string FormatKey = "color_format";
    public const string SpaceKey = "color_space";

    /// <summary>
    /// 22 个可编辑颜色通道。
    /// ⚠️ 顺序与 <see cref="ColorSchemeResolver"/> 的解析顺序严格一致（回退链依赖前序结果），
    /// 界面上的编辑行顺序也直接取自本数组 —— 改顺序会同时改变 UI 排布与语义。
    /// 测试「可编辑通道与解析器一一对应」守着这一点。
    /// </summary>
    public static readonly string[] ColorKeys =
    {
        "back_color",
        "shadow_color",
        "prevpage_color",
        "nextpage_color",
        "text_color",
        "candidate_text_color",
        "candidate_back_color",
        "border_color",
        "hilited_text_color",
        "hilited_back_color",
        "hilited_candidate_text_color",
        "hilited_candidate_back_color",
        "hilited_candidate_shadow_color",
        "hilited_shadow_color",
        "candidate_shadow_color",
        "candidate_border_color",
        "hilited_candidate_border_color",
        "label_color",
        "hilited_label_color",
        "comment_text_color",
        "hilited_comment_text_color",
        "hilited_mark_color",
    };

    public static bool IsColorKey(string key) => ColorKeys.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// 编辑器里的分组。22 个通道平铺成一列没人看得下去，按「画面上的哪个部位」分四组。
    /// </summary>
    /// <remarks>
    /// ⚠️ 分组放在 Core 而不是 App 层，就是为了让测试能守住它 ——
    /// 分组漏掉某个键，编辑器里那一行会静默消失，用户有一个颜色永远改不动，
    /// 而这种 bug 在 App 层无法用单测发现。
    /// 各组键的顺序决定界面行序，与 <see cref="ColorKeys"/> 的相对顺序无关。
    /// </remarks>
    public static readonly (string Group, string[] Keys)[] Groups =
    {
        ("Frame", new[]
        {
            "back_color",
            "border_color",
            "shadow_color"
        }),
        ("Text", new[]
        {
            "text_color",
            "candidate_text_color",
            "candidate_back_color",
            "candidate_shadow_color",
            "candidate_border_color"
        }),
        ("Hilite", new[]
        {
            "hilited_text_color",
            "hilited_back_color",
            "hilited_shadow_color",
            "hilited_candidate_text_color",
            "hilited_candidate_back_color",
            "hilited_candidate_shadow_color",
            "hilited_candidate_border_color"
        }),
        ("Extra", new[]
        {
            "label_color",
            "hilited_label_color",
            "comment_text_color",
            "hilited_comment_text_color",
            "prevpage_color",
            "nextpage_color",
            "hilited_mark_color"
        })
    };

    /// <summary>
    /// 生成一套方案在 YAML 里的定义节点。
    /// 与出厂默认相同的元数据一律不写（项目设计铁律），只留下真正有别于默认的键。
    /// </summary>
    public static Dictionary<string, object?> PresetDefinition(UserColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        var def = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [NameKey] = scheme.DisplayName
        };

        if (!string.IsNullOrWhiteSpace(scheme.Author)) def[AuthorKey] = scheme.Author.Trim();

        // 默认 abgr / srgb 不落盘：写出来除了让补丁变长没有任何作用，
        // 而且以后上游改了默认值，这条冗余补丁还会把它盖住。
        if (scheme.Format != RimeColorFormat.Abgr) def[FormatKey] = scheme.Format.ToConfigName();
        if (scheme.ColorSpace != RimeColorSpace.Srgb) def[SpaceKey] = scheme.ColorSpace.ToConfigName();

        // 只写用户显式设过的通道；没写的交给 Rime 的回退链，
        // 这样「我只想改高亮色」的人不会得到一个被 22 个硬编码值钉死的方案。
        foreach (var key in ColorKeys)
        {
            if (scheme.Colors.TryGetValue(key, out var literal) &&
                !string.IsNullOrWhiteSpace(literal))
            {
                def[key] = literal.Trim();
            }
        }

        return def;
    }
}

/// <summary>
/// 一套自定义配色方案。
/// </summary>
public sealed class UserColorScheme
{
    public required string Id { get; init; }

    /// <summary>显示名（写进 YAML 的 name 字段）。为空时显示 id。</summary>
    public string Name { get; set; } = "";

    public string Author { get; set; } = "";

    /// <summary>
    /// 本方案的字节序。小狼毫是 **per-scheme** 开关（preset_color_schemes/&lt;id&gt;/color_format），
    /// 不是全局的 style/color_format —— 每套方案可以各不相同。
    /// </summary>
    public RimeColorFormat Format { get; set; } = RimeColorFormat.Abgr;

    public RimeColorSpace ColorSpace { get; set; } = RimeColorSpace.Srgb;

    /// <summary>
    /// 只存「用户显式设过」的通道：键为 <see cref="ColorSchemeFields.ColorKeys"/> 之一，
    /// 值为 Rime 颜色字面量（如 "0xFFFFFF"）。缺失的键交给回退链。
    /// </summary>
    public Dictionary<string, string> Colors { get; init; } = new(StringComparer.Ordinal);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

    public UserColorScheme Clone() => CloneAs(Id);

    /// <summary>
    /// 复制一份并换成新 id（用于「以此为基础新建」与「复制」）。
    /// </summary>
    /// <remarks>
    /// Id 之所以不加公开 setter：就地改名等于「删旧 id + 加新 id」，
    /// 而旧 id 在 weasel.custom.yaml 里的那条要靠 appliedIds 差集才能摘掉。
    /// 允许就地改名会绕过整套清理机制，留下一堆谁也不认得的孤儿条目。
    /// 所以复制必须显式走这个方法，让调用方想清楚新 id 从哪来。
    /// </remarks>
    public UserColorScheme CloneAs(string newId, string? newName = null) => new()
    {
        Id = newId,
        Name = newName ?? Name,
        Author = Author,
        Format = Format,
        ColorSpace = ColorSpace,
        Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal)
    };

    /// <summary>
    /// 从一套已解析的配色生成「22 个通道全部显式写明」的方案。
    /// 用于「以内置方案为模板新建」：展开后的值与原方案渲染结果完全一致，
    /// 用户在编辑器里看到的每一格都是真实生效的颜色，而不是回退算出来的。
    /// </summary>
    public static UserColorScheme FromResolved(string id, string name, ResolvedColorScheme resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var scheme = new UserColorScheme { Id = id, Name = name };
        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            if (resolved.AbgrForKey(key) is not uint abgr) continue;
            scheme.Colors[key] = RimeColor.FromAbgr(abgr).Literal(scheme.Format);
        }
        return scheme;
    }

    /// <summary>
    /// 解析出本方案的最终配色（套用回退链）。供预览使用 —— 预览必须走同一条链，
    /// 否则「面板所见」与「候选窗所得」会不一致。
    /// </summary>
    public ResolvedColorScheme Resolve() =>
        ColorSchemeResolver.Resolve(key => key switch
        {
            // color_format / color_space 是 per-scheme 元数据，存在方案字段里而不是
            // Colors 里。不喂给解析器的话，一套 argb 方案的每个色值都会被当成 abgr 解析
            // —— 红蓝颠倒，而且只在这一套方案上出错，很难联想到是字节序漏了。
            ColorSchemeFields.FormatKey => Format.ToConfigName(),
            ColorSchemeFields.SpaceKey => ColorSpace.ToConfigName(),
            _ => Colors.TryGetValue(key, out var v) ? v : null
        });
}

/// <summary>
/// 从 Rime 补丁节点里抽取自定义配色定义。
/// 面板需要从 weasel.custom.yaml 读回自己写过的（以及用户手写的）方案，
/// 既用于编辑器的「已注入」状态显示，也用于让「外观」页的下拉框能看到自定义方案。
/// </summary>
public static class PresetColorSchemes
{
    /// <summary>
    /// 抽取 <c>preset_color_schemes/*</c>。同时兼容扁平写法（preset_color_schemes/foo）
    /// 与嵌套写法（preset_color_schemes: { foo: {...} }）—— 用户手改过文件时两种都可能出现。
    /// </summary>
    public static Dictionary<string, Dictionary<string, object?>> Extract(
        IReadOnlyDictionary<string, object?> patch)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        // 扁平：preset_color_schemes/<id>
        var prefix = ColorSchemeFields.Node + "/";
        foreach (var kv in patch)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || kv.Key.Length == prefix.Length)
                continue;
            if (kv.Value is not Dictionary<string, object?> body) continue;
            result[kv.Key.Substring(prefix.Length)] = body;
        }

        // 嵌套：preset_color_schemes: { <id>: {...} }
        if (patch.TryGetValue(ColorSchemeFields.Node, out var node) &&
            node is Dictionary<string, object?> map)
        {
            foreach (var kv in map)
            {
                if (kv.Value is not Dictionary<string, object?> body) continue;
                result[kv.Key] = body;
            }
        }

        return result;
    }
}

/// <summary>
/// 自定义配色注册表：<c>&lt;用户目录&gt;/user_color_schemes.json</c>。
/// 纯读写层，不做任何 YAML 操作（注入由 <see cref="UserColorSchemeStore"/> 负责）。
/// </summary>
public sealed class UserColorSchemeRegistry
{
    public const int CurrentVersion = 1;
    public const string FileName = "user_color_schemes.json";

    public string FilePath { get; }

    /// <summary>
    /// 方案列表。对外只读 —— 增删一律走 <see cref="Add"/> / <see cref="Remove"/>，
    /// 免得调用方一个 <c>Schemes.Clear()</c> 就把 appliedIds 与列表改得互不一致。
    /// </summary>
    public List<UserColorScheme> Schemes { get; private set; } = new();

    /// <summary>
    /// 上一次落盘写进 weasel.custom.yaml 的方案 id。用于算「本次要删除哪些」。
    /// 见文件头「为什么需要 appliedIds」。
    /// </summary>
    public List<string> AppliedIds { get; private set; } = new();

    /// <summary>注册表文件是否损坏（JSON 解析失败）。损坏时进入只读，绝不覆写。</summary>
    public bool IsCorrupt { get; private set; }
    public string? LoadError { get; private set; }

    private UserColorSchemeRegistry(string filePath) => FilePath = filePath;

    public static UserColorSchemeRegistry Load(string filePath)
    {
        var registry = new UserColorSchemeRegistry(filePath);
        registry.Reload();
        return registry;
    }

    public void Reload()
    {
        Schemes = new List<UserColorScheme>();
        AppliedIds = new List<string>();
        IsCorrupt = false;
        LoadError = null;

        if (!File.Exists(FilePath)) return;

        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (Exception ex) { IsCorrupt = true; LoadError = ex.Message; return; }

        try
        {
            var dto = JsonSerializer.Deserialize<RegistryDto>(text, JsonOptions.Read);
            if (dto is null) return;

            foreach (var s in dto.Schemes ?? new List<SchemeDto>())
            {
                if (string.IsNullOrWhiteSpace(s.Id)) continue;
                var scheme = new UserColorScheme
                {
                    Id = s.Id,
                    Name = s.Name ?? "",
                    Author = s.Author ?? "",
                    Format = RimeColorFormatExtensions.FromName(s.ColorFormat),
                    ColorSpace = RimeColorSpaceExtensions.FromName(s.ColorSpace)
                };
                foreach (var kv in s.Colors ?? new Dictionary<string, string>())
                {
                    if (!ColorSchemeFields.IsColorKey(kv.Key)) continue;   // 未知键一律丢弃
                    if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                    scheme.Colors[kv.Key] = kv.Value;
                }
                Schemes.Add(scheme);
            }

            AppliedIds = (dto.AppliedIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            IsCorrupt = true;
            LoadError = ex.Message;
        }
    }

    public void Save()
    {
        // 注册表解析失败时拒绝覆写 —— 与 CustomYamlFile 同一条底线：
        // 「用户手写的配置一个字都不能弄丢」。文件坏了就让它坏着，
        // 由调用方提示用户去看，而不是用一份空清单把它盖掉。
        if (IsCorrupt)
            throw PanelException.RefusedToOverwrite(Path.GetFileName(FilePath));

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var dto = new RegistryDto
        {
            Version = CurrentVersion,
            Schemes = Schemes.Select(s => new SchemeDto
            {
                Id = s.Id,
                Name = s.Name,
                Author = s.Author,
                // 与出厂一致的元数据存成 null，让 JSON 保持干净可读
                ColorFormat = s.Format == RimeColorFormat.Abgr ? null : s.Format.ToConfigName(),
                ColorSpace = s.ColorSpace == RimeColorSpace.Srgb ? null : s.ColorSpace.ToConfigName(),
                Colors = s.Colors
            }).ToList(),
            AppliedIds = AppliedIds
        };

        var text = JsonSerializer.Serialize(dto, JsonOptions.Write);
        File.WriteAllText(FilePath, text);
    }

    public UserColorScheme? Get(string id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public bool Contains(string id) => Get(id) is not null;

    public void Add(UserColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        if (!Contains(scheme.Id)) Schemes.Add(scheme);
    }

    /// <summary>移除方案；id 不存在时返回 false（调用方据此判断要不要提示）。</summary>
    public bool Remove(string id)
    {
        var scheme = Get(id);
        return scheme is not null && Schemes.Remove(scheme);
    }

    public void Clear() => Schemes.Clear();

    /// <summary>记录本次真正落盘的 id 集合。只能在写盘成功之后调用。</summary>
    public void MarkApplied(IEnumerable<string> ids) =>
        AppliedIds = ids.Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

    /// <summary>清空 appliedIds（供「全部清除」使用）。</summary>
    public void ClearApplied() => AppliedIds = new List<string>();

    /// <summary>
    /// 生成不与现有 id 冲突的新 id。基于 <paramref name="preferred"/> 清洗，
    /// 冲突时追加 -2 / -3 …… 保证「新建」永远能成功，不会因为重名静默覆盖已有方案。
    /// </summary>
    public string UniqueId(string preferred)
    {
        var baseId = UserColorSchemeStore.MakeId(preferred);
        if (!Contains(baseId)) return baseId;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!Contains(candidate)) return candidate;
        }
        return baseId + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    // ── JSON ────────────────────────────────────────────────────────────

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Read = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static readonly JsonSerializerOptions Write = new()
        {
            WriteIndented = true,
            // 不转义中文：注册表是给用户看、也可能会被手改的文件，
            // 满屏 \uXXXX 等于没法改。UnsafeRelaxedJsonEscaping 只对 <> & ' + 等
            // 放宽，JSON 结构本身仍然正确转义。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private sealed class RegistryDto
    {
        public int Version { get; set; } = CurrentVersion;
        public List<SchemeDto>? Schemes { get; set; }
        public List<string>? AppliedIds { get; set; }
    }

    private sealed class SchemeDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? ColorFormat { get; set; }
        public string? ColorSpace { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
    }
}

/// <summary>
/// 自定义配色的一站式入口：注册表读写 + 注入 weasel.custom.yaml。
/// </summary>
public sealed class UserColorSchemeStore
{
    public const string IdPrefix = "user_";

    public WeaselEnvironment Environment { get; }
    public UserColorSchemeRegistry Registry { get; private set; }
    public string RegistryPath { get; }

    public UserColorSchemeStore(WeaselEnvironment environment)
    {
        Environment = environment;
        RegistryPath = Path.Combine(environment.UserDirectory, UserColorSchemeRegistry.FileName);
        Registry = UserColorSchemeRegistry.Load(RegistryPath);
    }

    public void Reload() => Registry = UserColorSchemeRegistry.Load(RegistryPath);

    // ── id ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 把任意显示名清洗成合法的方案 id：小写、只留 [a-z0-9]、其余转连字符、
    /// 合并重复连字符、去首尾连字符，并保证以 <see cref="IdPrefix"/> 开头。
    /// </summary>
    /// <remarks>
    /// 只留 ASCII 的理由见文件头第 3 条。这一步是纯函数以便测试，
    /// 也保证「同一个名字在任何机器上算出同一个 id」。
    /// </remarks>
    /// 合法 id 字符。下划线必须保留 —— Rime 的配置键普遍用下划线分词
    /// （preset_color_schemes 下的方案名尤其如此），把它转成连字符会让
    /// 「user_foo」先变成「user-foo」、再因为认不出前缀被加一次前缀，
    /// 最终得到「user_user-foo」这种重复前缀的怪 id。
    private static bool IsIdChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

    public static string MakeId(string? raw)
    {
        var builder = new StringBuilder();
        var lastWasDash = false;

        foreach (var c in raw ?? "")
        {
            var lower = char.ToLowerInvariant(c);
            if (IsIdChar(lower))
            {
                builder.Append(lower);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        // 去掉结尾残留的连字符
        var id = builder.ToString().Trim('-');
        if (id.Length == 0) id = "scheme";
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) id = IdPrefix + id;
        return id;
    }

    /// <summary>id 是否由本面板管理（用于区分内置方案与自定义方案）。</summary>
    public static bool IsManagedId(string? id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ── 注入 weasel.custom.yaml ─────────────────────────────────────────

    /// <summary>本次应用的结果，供状态栏汇报。</summary>
    public sealed record ApplyResult(int Written, int Removed, string FilePath);

    /// <summary>
    /// 把注册表里的全部方案写进补丁文件，并摘掉「上次写过、这次没了」的方案。
    /// 走 <see cref="CustomYamlFile.ApplyLineEdits"/> 而不是整文件重序列化：
    /// 用户可能在同一个 weasel.custom.yaml 里手写过别的条目与注释，不能冲掉。
    /// </summary>
    public ApplyResult Apply(CustomYamlFile custom)
    {
        ArgumentNullException.ThrowIfNull(custom);

        if (!custom.IsWritable)
            throw PanelException.RefusedToOverwrite(Path.GetFileName(custom.FilePath));

        var set = new PatchSet();
        var written = 0;

        foreach (var scheme in Registry.Schemes)
        {
            set.Set(ColorSchemeFields.Node + "/" + scheme.Id,
                PatchValue.Dictionary(ColorSchemeFields.PresetDefinition(scheme)));
            written++;
        }

        var liveIds = Registry.Schemes.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var id in Registry.AppliedIds)
        {
            if (liveIds.Contains(id)) continue;
            // 只删我们自己写过的：用户手写或内置的同名方案一概不动。
            if (!IsManagedId(id)) continue;
            set.Remove(ColorSchemeFields.Node + "/" + id);
            removed++;
        }

        custom.ApplyLineEdits(set);

        // 落盘成功才更新 appliedIds —— 写盘失败时保持原样，
        // 否则下次应用会以为「已经从 YAML 里删掉了」，那条残留永远清不掉。
        Registry.MarkApplied(Registry.Schemes.Select(s => s.Id));
        Registry.Save();

        return new ApplyResult(written, removed, custom.FilePath);
    }

    /// <summary>清空本面板注入过的全部方案（含 YAML 里的条目），保留注册表文件本身。</summary>
    public ApplyResult ClearAll(CustomYamlFile custom)
    {
        ArgumentNullException.ThrowIfNull(custom);

        if (!custom.IsWritable)
            throw PanelException.RefusedToOverwrite(Path.GetFileName(custom.FilePath));

        var set = new PatchSet();
        var removed = 0;
        foreach (var id in Registry.AppliedIds.Concat(Registry.Schemes.Select(s => s.Id))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!IsManagedId(id)) continue;
            set.Remove(ColorSchemeFields.Node + "/" + id);
            removed++;
        }

        custom.ApplyLineEdits(set);

        Registry.Clear();
        Registry.ClearApplied();
        Registry.Save();

        return new ApplyResult(0, removed, custom.FilePath);
    }

    // ── 导入 / 导出 ─────────────────────────────────────────────────────

    /// <summary>导入结果。</summary>
    public sealed record ImportResult(int Added, int Skipped);

    /// <summary>
    /// 从 YAML 文本导入方案。接受三种形态（与 macOS 版 parseImportedYAML 对齐）：
    ///   1. <c>preset_color_schemes: { foo: {...} }</c>
    ///   2. <c>patch: { preset_color_schemes: { foo: {...} } }</c>
    ///   3. 直接就是 <c>{ foo: {...} }</c> 的方案名 → 定义映射
    /// </summary>
    /// <remarks>
    /// 只收 22 个已知颜色键：小狼毫对其它键一律不消费，留着就是一堆
    /// 看得见却改不了、也不知道会不会生效的死数据。
    /// </remarks>
    public ImportResult ImportYaml(string text, ISet<string>? existingIds = null)
    {
        var parsed = YamlLoader.Load(text);
        if (!parsed.Success || parsed.Root is null) return new ImportResult(0, 0);

        var pairs = FindSchemeMap(parsed.Root);
        var added = 0;
        var skipped = 0;

        foreach (var (name, body) in pairs)
        {
            var id = Registry.UniqueId(name);
            if (existingIds is not null && existingIds.Contains(id)) { skipped++; continue; }
            if (Registry.Contains(id)) { skipped++; continue; }

            var scheme = new UserColorScheme
            {
                Id = id,
                Name = StringOf(body, ColorSchemeFields.NameKey) ?? name,
                Author = StringOf(body, ColorSchemeFields.AuthorKey) ?? "",
                Format = RimeColorFormatExtensions.FromName(StringOf(body, ColorSchemeFields.FormatKey)),
                ColorSpace = RimeColorSpaceExtensions.FromName(StringOf(body, ColorSchemeFields.SpaceKey))
            };

            foreach (var key in ColorSchemeFields.ColorKeys)
            {
                if (!body.TryGetValue(key, out var raw)) continue;
                // 归一成字面量：YAML 会把 0xFFFFFF 读成整数，
                // 存整数会在下次写出时变成十进制，与人眼校对习惯不符。
                if (!RimeColor.TryParseAbgr(raw, scheme.Format, out var abgr)) continue;
                scheme.Colors[key] = RimeColor.FromAbgr(abgr).Literal(scheme.Format);
            }

            Registry.Add(scheme);
            added++;
        }

        return new ImportResult(added, skipped);
    }

    /// <summary>导出为 YAML（preset_color_schemes 段）。</summary>
    public static string ExportYaml(IEnumerable<UserColorScheme> schemes)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var s in schemes) map[s.Id] = ColorSchemeFields.PresetDefinition(s);

        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ColorSchemeFields.Node] = map
        };
        return string.Join('\n', YamlMiniEmitter.EmitMapping(root)) + "\n";
    }

    private static IEnumerable<(string Name, Dictionary<string, object?> Body)> FindSchemeMap(
        Dictionary<string, object?> root)
    {
        if (root.TryGetValue(ColorSchemeFields.Node, out var node) &&
            node is Dictionary<string, object?> direct)
            return Named(direct);

        if (root.TryGetValue("patch", out var patchNode) &&
            patchNode is Dictionary<string, object?> patch)
        {
            var extracted = PresetColorSchemes.Extract(patch);
            if (extracted.Count > 0) return extracted.Select(kv => (kv.Key, kv.Value));
            return Named(patch);
        }

        // 既没有 preset_color_schemes 也没有 patch：把根节点本身当方案名→定义映射。
        // 仅当**每个**值都是映射时才这么解释，否则会把无关的 YAML 文件误读成配色。
        if (root.Count > 0 && root.Values.All(v => v is Dictionary<string, object?>))
            return Named(root);

        return Array.Empty<(string, Dictionary<string, object?>)>();
    }

    /// <summary>
    /// 只收「至少含一个已知颜色键」的节点。
    /// 没有这层过滤的话，随便一份 YAML（style: { font_point: 16 }）都会被当成
    /// 一套「没有任何颜色的配色方案」导入，凭空多出一个空方案。
    /// </summary>
    private static IEnumerable<(string Name, Dictionary<string, object?> Body)> Named(
        Dictionary<string, object?> map) =>
        map.Where(kv => kv.Value is Dictionary<string, object?>)
           .Select(kv => (kv.Key, (Dictionary<string, object?>)kv.Value!))
           .Where(t => ColorSchemeFields.ColorKeys.Any(k => t.Item2.ContainsKey(k)));

    private static string? StringOf(Dictionary<string, object?> body, string key) =>
        body.TryGetValue(key, out var v) && v is string s ? s : null;
}

//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  输入方案（*.schema.yaml）的值对象 + 扫描合并。
//  GPL-3.0。
//
//  ── 三个分立概念的辨析（按 Rime / 小狼毫语义）────────────────────────────
//   1) 「已安装的方案」= 在共享 data/ 目录或用户目录里实际有 *.schema.yaml 文件者。
//      对应上游 WeaselServer 启动时从这两个目录枚举。
//   2) 「默认启用的方案」= default.yaml 里的 schema_list（上文的 5 步覆盖链里
//      priority=1 的列表，详见 WeaselDefaults / WeaselLayoutKernel）。注意它**不是**
//      全部已安装方案 —— 出厂默认只列出几个常见的。
//   3) 「用户当前启用的方案」= default.custom.yaml 里 patch/schema_list 覆盖后的结果。
//      列表整体替换（Rime 的合并语义：列表非深度合并），所以一旦用户写了 patch，
//      上游列表就被完全覆盖。
//
//  面板要做的：扫描 1）展示「可用」；读取 2）作为出厂参照；读取/写 3）作为「我的方案」。
//  一旦面板在 3）里写了 patch，即便 2）增删了方案，用户列表也不会自动跟着变 ——
//  这是上游行为，不应自作主张在面板里做"合并"。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

/// <summary>单个 Rime 输入方案（不可变快照）。</summary>
public sealed record InputSchema(
    string SchemaId,
    string Name,
    string? Version,
    string? Author,
    string? Description,
    string FilePath,
    bool IsBuiltIn);

/// <summary>
/// 一次性扫描结果：已安装方案集合 + 基础/自定义启用列表。
/// 全部属性只读，不持有任何文件状态；后续写盘请用 <see cref="SchemaActivationWriter"/>。
/// </summary>
public sealed class SchemaCatalog
{
    /// <summary>扫描到的所有方案，按 id 索引（同名时用户目录覆盖共享目录）。</summary>
    public required IReadOnlyDictionary<string, InputSchema> All { get; init; }

    /// <summary>default.yaml 出厂的 schema_list（priority=1 的「默认启用」列表）。</summary>
    public required IReadOnlyList<string> BaseActiveIds { get; init; }

    /// <summary>default.custom.yaml 里 patch/schema_list 的覆盖；空数组表示「未写 patch」。</summary>
    public required IReadOnlyList<string> CustomActiveIds { get; init; }

    /// <summary>用户目录路径，仅供 UI 展示用。</summary>
    public required string UserDirectory { get; init; }

    /// <summary>是否有用户自定义（用户在 default.custom.yaml 里写了 patch/schema_list）。</summary>
    public bool HasCustomization => CustomActiveIds.Count > 0;

    /// <summary>
    /// 合并后的生效启用列表：
    ///   1) 用户已写 patch → 用 patch 列表（Rime 列表整体替换的合并语义）；
    ///   2) 否则 → 用 base 列表。
    /// </summary>
    public IReadOnlyList<string> EffectiveActiveIds =>
        HasCustomization ? CustomActiveIds : BaseActiveIds;

    /// <summary>用户未启用但已安装的方案 —— 「可启用」候选列表的来源。</summary>
    public IEnumerable<InputSchema> AvailableToAdd
    {
        get
        {
            var active = new HashSet<string>(EffectiveActiveIds, StringComparer.Ordinal);
            return All.Values
                .Where(s => !active.Contains(s.SchemaId))
                .OrderBy(s => s.Name, StringComparer.CurrentCulture);
        }
    }

    /// <summary>未安装但被用户在 patch.schema_list 中引用的方案 id（孤儿条目）。</summary>
    public IEnumerable<string> OrphanIds =>
        CustomActiveIds.Where(id => !All.ContainsKey(id));

    public static SchemaCatalog Empty { get; } = new()
    {
        All = new Dictionary<string, InputSchema>(StringComparer.Ordinal),
        BaseActiveIds = Array.Empty<string>(),
        CustomActiveIds = Array.Empty<string>(),
        UserDirectory = "",
    };

    /// <summary>
    /// 扫描用户目录 + 共享数据目录，返回 <see cref="SchemaCatalog"/>。
    /// </summary>
    /// <remarks>
    /// 全部异常一律吞掉降级：
    ///   · 单个 schema.yaml 解析失败 → 该方案当作 id-only 仍纳入（避免一个坏文件拖垮整页）；
    ///   · default.yaml 解析失败 → BaseActiveIds 空；
    ///   · default.custom.yaml 解析失败 → 由调用方按 CustomYamlFile 的 IsWritable 自决。
    /// </remarks>
    public static SchemaCatalog Build(string userDirectory, string? sharedDataDirectory)
    {
        var all = new Dictionary<string, InputSchema>(StringComparer.Ordinal);

        // 1) 共享目录（先扫，作为底；同名方案会被用户目录覆盖）。
        ScanDirectory(sharedDataDirectory, isBuiltIn: true, sink: all);

        // 2) 用户目录（覆盖）。用户的方案可能与共享目录同名 —— 用户的优先。
        ScanDirectory(userDirectory, isBuiltIn: false, sink: all);

        // 3) default.yaml 的 schema_list。
        var baseList = ReadBaseActiveIds(sharedDataDirectory);

        // 4) default.custom.yaml 的 patch.schema_list。
        var customList = ReadCustomActiveIds(userDirectory);

        return new SchemaCatalog
        {
            All = all,
            BaseActiveIds = baseList,
            CustomActiveIds = customList,
            UserDirectory = userDirectory,
        };
    }

    // ── 扫描 ────────────────────────────────────────────────────────────────

    private static void ScanDirectory(
        string? directory, bool isBuiltIn, Dictionary<string, InputSchema> sink)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.schema.yaml", SearchOption.TopDirectoryOnly);
        }
        catch { return; /* 无权限即视为空目录 */ }

        foreach (var file in files)
        {
            // 单文件坏掉不应拖累整次扫描 —— 留为 id-only 让用户能看到「这里有个文件但读不出来」。
            // 不能完全跳过，否则面板会显示「方案数变少」，让人误以为是面板 bug。
            var schema = ReadSchemaFile(file, isBuiltIn) ?? BuildIdOnlySchema(file, isBuiltIn);

            // 同名时用户目录覆盖共享目录（上游枚举顺序即如此）。
            sink[schema.SchemaId] = schema;
        }
    }

    /// <summary>
    /// 解析单个 schema.yaml。**返回 null 表示「该文件应被降级为 id-only 条目」**——
    /// 两种情况走 fallback：解析失败；或缺 schema/schema_id（无 id 不算合法方案）。
    /// </summary>
    private static InputSchema? ReadSchemaFile(string path, bool isBuiltIn)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return null; /* 文件被独占锁/权限错误 → 同视为降级 */ }

        var view = RimeConfigView.FromYaml(text);

        // schema/schema_id 缺失 → 不是合法方案文件（例如有人误把 default.yaml 放进来）
        if (!view.TryGetString("schema/schema_id", out var id) || id.Length == 0) return null;

        var name = view.TryGetString("schema/name", out var n) ? n : id;
        var version = view.TryGetString("schema/version", out var v) ? v : null;
        var author = view.TryGetString("schema/author", out var a) ? a : null;
        var description = view.TryGetString("schema/description", out var d) ? d : null;

        return new InputSchema(
            SchemaId: id,
            Name: name,
            Version: version,
            Author: author,
            Description: description,
            FilePath: path,
            IsBuiltIn: isBuiltIn);
    }

    /// <summary>
    /// 单个文件解析失败时的兜底：仍以文件名造一个 id-only 条目进入列表，
    /// 让用户至少能「知道这里有个文件但读不出来」。不能完全跳过，
    /// 否则面板会显示「方案数变少」，让人误以为是面板 bug。
    /// </summary>
    private static InputSchema BuildIdOnlySchema(string path, bool isBuiltIn)
    {
        var raw = Path.GetFileName(path);
        var id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(raw)) ?? raw;
        return new InputSchema(
            SchemaId: id,
            Name: id + "（读取失败）",
            Version: null, Author: null, Description: null,
            FilePath: path,
            IsBuiltIn: isBuiltIn);
    }

    // ── 列表读取 ────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ReadBaseActiveIds(string? sharedDataDirectory)
    {
        if (string.IsNullOrEmpty(sharedDataDirectory)) return Array.Empty<string>();
        var path = Path.Combine(sharedDataDirectory, "default.yaml");
        if (!File.Exists(path)) return Array.Empty<string>();

        try
        {
            var view = RimeConfigView.FromYaml(File.ReadAllText(path));
            return ExtractSchemaIds(view.Lookup("schema_list"));
        }
        catch { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> ReadCustomActiveIds(string userDirectory)
    {
        if (string.IsNullOrEmpty(userDirectory)) return Array.Empty<string>();
        var path = Path.Combine(userDirectory, "default.custom.yaml");
        if (!File.Exists(path)) return Array.Empty<string>();

        try
        {
            var view = RimeConfigView.FromYaml(File.ReadAllText(path));
            return ExtractSchemaIds(view.Lookup("patch/schema_list"));
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 从已展开的 schema_list 节点抽取 id。Rime 的 schema_list 每项是
    /// <c>{schema: rime_ice}</c>，也可能可能存在 <c>schema: {id: ...}</c> 的嵌套形态
    /// （部分 fork 用）—— 一并兼容。
    /// </summary>
    private static IReadOnlyList<string> ExtractSchemaIds(object? node)
    {
        if (node is not System.Collections.IEnumerable seq || node is string) return Array.Empty<string>();
        var ids = new List<string>();
        foreach (var raw in seq)
        {
            if (raw is Dictionary<string, object?> map)
            {
                // 形态 1：{schema: rime_ice}
                if (map.TryGetValue("schema", out var s) && s is string id1 && id1.Length > 0)
                {
                    ids.Add(id1);
                    continue;
                }
                // 形态 2：{schema: {id: rime_ice}}
                if (map.TryGetValue("schema", out var nested) &&
                    nested is Dictionary<string, object?> nm &&
                    nm.TryGetValue("id", out var idObj) &&
                    idObj is string id2 &&
                    id2.Length > 0)
                {
                    ids.Add(id2);
                }
            }
        }
        return ids;
    }
}
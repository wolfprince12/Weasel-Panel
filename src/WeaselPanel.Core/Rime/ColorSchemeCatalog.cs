//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  内置配色方案目录。GPL-3.0。
//
//  ── 为什么运行时解析而不是硬编码 ──────────────────────────────────────
//  上游 weasel.yaml 自带 38 套 preset_color_schemes、数百个色值。
//  硬编码进本仓库意味着：
//    · 每次上游改配色都要人工同步，漏一个就是面板与实机不一致；
//    · 用户装的是旧版/新版小狼毫时，面板显示的配色与本机实际不符。
//  因此这里改为**从共享数据目录的 weasel.yaml 现场解析**，自动跟随本机版本。
//
//  ── 数据流向 ──────────────────────────────────────────────────────────
//      WeaselPaths.SharedDirectory()/weasel.yaml
//          → ColorSchemeCatalog.Parse(text)
//          → Resolve(name) → ColorSchemeResolver → ResolvedColorScheme
//  最后一步才套用回退链（见 ColorSchemeResolver），本类只负责取原始值。

using System;
using System.Collections.Generic;
using System.Linq;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

public sealed class ColorSchemeCatalog
{
    private readonly Dictionary<string, Dictionary<string, object?>> _schemes;
    private readonly List<string> _order;

    private ColorSchemeCatalog(Dictionary<string, Dictionary<string, object?>> schemes, List<string> order)
    {
        _schemes = schemes;
        _order = order;
    }

    /// <summary>空目录（weasel.yaml 缺失或解析失败时的降级，保证面板不崩）。</summary>
    public static ColorSchemeCatalog Empty { get; } =
        new(new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal), new List<string>());

    /// <summary>
    /// 从 weasel.yaml 文本解析 preset_color_schemes。解析失败返回空目录而非抛异常
    /// —— 共享目录缺失是常见的合法状态（小狼毫未装 / 自定义安装路径）。
    /// </summary>
    public static ColorSchemeCatalog Parse(string weaselYamlText)
    {
        var result = YamlLoader.Load(weaselYamlText);
        if (!result.Success || result.Root is null) return Empty;

        if (!result.Root.TryGetValue("preset_color_schemes", out var node) ||
            node is not Dictionary<string, object?> presets)
            return Empty;

        var schemes = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var kv in presets)
        {
            if (kv.Key is null) continue;
            if (kv.Value is not Dictionary<string, object?> body) continue;   // name: 不是映射 → 跳过
            schemes[kv.Key] = body;
            order.Add(kv.Key);
        }

        return new ColorSchemeCatalog(schemes, order);
    }

    /// <summary>方案 id 列表，按 weasel.yaml 中的声明顺序（面板下拉框依赖此顺序）。</summary>
    public IReadOnlyList<string> Names => _order;

    public bool Contains(string? name) => name is not null && _schemes.ContainsKey(name);

    /// <summary>取方案原始节点（未套用回退链）。</summary>
    public IReadOnlyDictionary<string, object?>? Raw(string name) =>
        _schemes.TryGetValue(name, out var body) ? body : null;

    /// <summary>取方案的显示名（name 字段），缺失时回退为 id 本身。</summary>
    public string DisplayName(string name) =>
        _schemes.TryGetValue(name, out var body) &&
        body.TryGetValue("name", out var v) &&
        v is string s && s.Length > 0
            ? s
            : name;

    public string? Author(string name) =>
        _schemes.TryGetValue(name, out var body) &&
        body.TryGetValue("author", out var v) &&
        v is string s
            ? s
            : null;

    /// <summary>
    /// 在目录末尾追加自定义方案（来自 weasel.custom.yaml 的 patch/preset_color_schemes）。
    /// 同名方案由自定义方覆盖内置方 —— 与 Rime 的补丁语义一致。
    /// </summary>
    /// <remarks>
    /// 不加这一层的话，用户在「自定义配色」页新建的方案虽然在 YAML 里，
    /// 「外观」页的下拉框（只解析 weasel.yaml）却看不到它 —— 建了选不上。
    /// </remarks>
    public ColorSchemeCatalog Appending(IReadOnlyDictionary<string, Dictionary<string, object?>> extra)
    {
        if (extra.Count == 0) return this;

        var schemes = new Dictionary<string, Dictionary<string, object?>>(_schemes, StringComparer.Ordinal);
        var order = new List<string>(_order);

        foreach (var kv in extra)
        {
            if (!schemes.ContainsKey(kv.Key)) order.Add(kv.Key);
            schemes[kv.Key] = kv.Value;
        }

        return new ColorSchemeCatalog(schemes, order);
    }

    /// <summary>
    /// 解析为可直接预览的配色（套用完整回退链）。方案不存在返回 null。
    /// </summary>
    public ResolvedColorScheme? Resolve(string name)
    {
        if (!_schemes.TryGetValue(name, out var body)) return null;
        return ColorSchemeResolver.Resolve(key => body.TryGetValue(key, out var v) ? v : null);
    }
}

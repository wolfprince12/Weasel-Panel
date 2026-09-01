//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  Rime 配置视图：把「基础配置 + patch」按 Rime 语义深度合并后，按路径取值。
//  GPL-3.0。
//
//  ── 为什么必须自己实现合并 ────────────────────────────────────────────
//  上游 C++ 拿到的 RimeConfig 是 librime **已完成 patch 合并**的结果，
//  因此 _UpdateUIStyle 里每次 config_get_* 看到的都是最终值。
//  本面板不链接 librime，必须在 Core 里自己复刻这个合并，否则
//  「面板读到的样式」与「小狼毫实际渲染的样式」必然对不上。
//
//  ── Rime patch 的合并语义（与直觉不同的两点）─────────────────────────
//   1. 映射是**深度合并**，不是整体替换。
//      patch 里只写 style/font_point，不会抹掉 style/layout 下的一整棵子树。
//   2. 列表是**整体替换**，不是追加。
//      例如 app_options 下的按键绑定列表，patch 写了就整份覆盖。
//
//  ── 键存在性必须严格区分（本类最重要的契约）─────────────────────────
//  上游 _RimeGetBool 的核心分支是 `if (config_get_bool(...) || cond)`，
//  即「键是否存在」与「键的值是什么」是两个独立信息；
//  键不存在时走的是 falseValue 分支（甚至完全不改，取决于 cond）。
//  因此本类所有 TryGet* 的**返回值表示「键是否存在且类型可解析」**，
//  而 out 参数表示值。调用方绝不可忽略返回值，否则会退化成
//  「键不存在 == 键为 false」，从而静默改变布局派生的结果。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

public sealed class RimeConfigView
{
    private readonly Dictionary<string, object?> _root;

    private RimeConfigView(Dictionary<string, object?> root) => _root = root;

    public static RimeConfigView Empty { get; } =
        new(new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>从 YAML 文本构造。解析失败一律降级为空视图（宁可缺配置，不可误判）。</summary>
    public static RimeConfigView FromYaml(string? yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText)) return Empty;
        var result = YamlLoader.Load(yamlText!);
        return result.Success && result.Root is not null ? new RimeConfigView(result.Root) : Empty;
    }

    public static RimeConfigView FromTree(Dictionary<string, object?> tree) => new(tree);

    /// <summary>
    /// 按 Rime 语义深度合并：patch 覆盖 base。两者都不可变，返回新视图。
    /// </summary>
    public static RimeConfigView Merge(RimeConfigView baseView, RimeConfigView patchView) =>
        new(MergeTrees(baseView._root, patchView._root));

    /// <summary>
    /// 合并一棵 patch 树。patch 中的扁平键（如 "style/font_point"）会先展开成嵌套结构。
    /// </summary>
    public static RimeConfigView MergePatch(RimeConfigView baseView, IReadOnlyDictionary<string, object?>? patch)
    {
        if (patch is null || patch.Count == 0) return baseView;
        var expanded = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in patch)
            expanded[kv.Key] = kv.Value;
        return new RimeConfigView(MergeTrees(baseView._root, ExpandFlatKeys(expanded)));
    }

    /// <summary>把形如 "style/font_point" 的顶层扁平键展开为嵌套映射。非扁平键原样保留。</summary>
    private static Dictionary<string, object?> ExpandFlatKeys(Dictionary<string, object?> tree)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in tree)
        {
            if (!kv.Key.Contains('/'))
            {
                // 同名键合并（展开出来的子树可能与已有嵌套键撞车）
                result[kv.Key] =
                    result.TryGetValue(kv.Key, out var existing) &&
                    existing is Dictionary<string, object?> existingMap &&
                    AsMap(kv.Value) is { } incomingMap
                        ? MergeTrees(existingMap, incomingMap)
                        : kv.Value;
                continue;
            }

            var segments = kv.Key.Split('/');
            var cursor = result;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (cursor.TryGetValue(seg, out var child) && child is Dictionary<string, object?> childMap)
                {
                    cursor = childMap;
                }
                else
                {
                    var fresh = new Dictionary<string, object?>(StringComparer.Ordinal);
                    cursor[seg] = fresh;
                    cursor = fresh;
                }
            }
            cursor[segments[^1]] = kv.Value;
        }
        return result;
    }

    private static Dictionary<string, object?> MergeTrees(
        Dictionary<string, object?> baseTree,
        Dictionary<string, object?> patchTree)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in baseTree) result[kv.Key] = kv.Value;

        foreach (var kv in patchTree)
        {
            if (result.TryGetValue(kv.Key, out var existing) &&
                existing is Dictionary<string, object?> baseMap &&
                AsMap(kv.Value) is Dictionary<string, object?> patchMap)
            {
                // 两边都是映射 → 深度合并
                result[kv.Key] = MergeTrees(baseMap, patchMap);
            }
            else
            {
                // 其余一律覆盖（列表整体替换，标量直接覆盖）
                result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    private static Dictionary<string, object?>? AsMap(object? value) =>
        value as Dictionary<string, object?>;

    // ── 取值 ────────────────────────────────────────────────────────────

    /// <summary>键是否存在（无论类型能否解析）。</summary>
    public bool Contains(string path) => TryResolve(path, out _);

    public object? Lookup(string path) => TryResolve(path, out var v) ? v : null;

    /// <summary>取布尔。返回值 = 键存在且可解析为布尔。</summary>
    public bool TryGetBool(string path, out bool value)
    {
        value = false;
        if (!TryResolve(path, out var raw) || raw is null) return false;

        switch (raw)
        {
            case bool b: value = b; return true;
            case int i: value = i != 0; return true;
            case long l: value = l != 0; return true;
            case double d: value = Math.Abs(d) > double.Epsilon; return true;
            case string s: return TryParseBoolText(s, out value);
            default: return false;
        }
    }

    /// <summary>取整数。返回值 = 键存在且可解析为整数。</summary>
    public bool TryGetInt(string path, out int value)
    {
        value = 0;
        if (!TryResolve(path, out var raw) || raw is null) return false;

        switch (raw)
        {
            case int i: value = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
            case uint u when u <= int.MaxValue: value = (int)u; return true;
            case double d: value = (int)d; return true;
            case string s:
                var text = s.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out value);
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default: return false;
        }
    }

    /// <summary>取字符串。非字符串标量会按其文本形式返回（与 librime 的 config_get_string 一致）。</summary>
    public bool TryGetString(string path, out string value)
    {
        value = string.Empty;
        if (!TryResolve(path, out var raw) || raw is null) return false;

        switch (raw)
        {
            case string s: value = s; return true;
            case bool b: value = b ? "true" : "false"; return true;
            case int i: value = i.ToString(CultureInfo.InvariantCulture); return true;
            case long l: value = l.ToString(CultureInfo.InvariantCulture); return true;
            // 映射/列表不是标量，librime 的 config_get_string 会失败
            default: return false;
        }
    }

    private static bool TryParseBoolText(string text, out bool value)
    {
        var s = text.Trim();
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            s == "1")
        {
            value = true;
            return true;
        }
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            s == "0")
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    /// <summary>
    /// 按 '/' 分段解析路径。除逐段下钻外，还兼容 Rime 的扁平键写法
    /// （"style/font_point": 14），在每一层都试一次「剩余路径整体作为键」。
    /// </summary>
    private bool TryResolve(string path, out object? value)
    {
        value = null;
        if (string.IsNullOrEmpty(path)) return false;

        var segments = path.Split('/');
        object? current = _root;

        for (var i = 0; i < segments.Length; i++)
        {
            if (current is not Dictionary<string, object?> map) return false;

            // 扁平键优先：从当前层起，剩余路径整体作为一个键
            if (i < segments.Length - 1)
            {
                var flatKey = string.Join("/", segments.Skip(i));
                if (map.TryGetValue(flatKey, out var flat))
                {
                    value = flat;
                    return true;
                }
            }

            if (!map.TryGetValue(segments[i], out current)) return false;
        }

        value = current;
        return true;
    }
}

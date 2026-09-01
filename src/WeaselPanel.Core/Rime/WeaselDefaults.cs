//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  出厂默认值表。GPL-3.0。
//
//  ── 用途 ──────────────────────────────────────────────────────────────
//  支撑两条铁律：
//   1. 与出厂默认相同的值**一律不落盘**（避免把 weasel.custom.yaml 撑成一份全量配置，
//      也避免上游改默认值后用户被旧补丁钉死）；
//   2. 面板能标出「此项已被修改」，并提供恢复默认。
//
//  ── 默认值来自哪里 ────────────────────────────────────────────────────
//  小狼毫的出厂默认值有**两个来源**，缺一不可：
//
//   A. 共享数据目录的 weasel.yaml（安装时随程序释放）
//      → 本类运行时现场解析，**自动跟随用户本机版本**，不做硬编码。
//
//   B. C++ UIStyle 构造函数里的硬编码初值（weasel.yaml 里没写的键）
//      → 见下方 CxxFallbacks，每条都注明上游出处，未经核实不得添加。
//
//  ── 别名回退（高危，改动前必读）────────────────────────────────────────
//  上游 _RimeGetIntStr 的语义是「取主键，取不到再取别键」。这意味着
//  **面板若写入主键，会静默覆盖用户写在别键上的值**：
//
//      style/layout/border        ← style/layout/border_width
//      style/layout/corner_radius ← style/layout/round_corner
//      style/layout/hilited_corner_radius ← style/layout/round_corner
//      style/layout/hilite_padding_x      ← style/layout/hilite_padding
//      style/layout/hilite_padding_y      ← style/layout/hilite_padding
//
//  ⚠️ 尤其注意第一条：weasel.yaml 出厂写的正是 `border_width: 3`，
//     面板若按 UIStyle 的字段名去写 `border`，会让用户的 `border_width` 失效。
//     面板写回时必须优先写**别键**（即出厂已有的那个键），除非用户自己用了主键。

using System;
using System.Collections.Generic;
using System.Globalization;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

public sealed class WeaselDefaults
{
    private readonly Dictionary<string, object?> _root;

    private WeaselDefaults(Dictionary<string, object?> root) => _root = root;

    /// <summary>空表（weasel.yaml 缺失/解析失败时的降级）。</summary>
    public static WeaselDefaults Empty { get; } =
        new(new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>
    /// 上游「主键 ← 别键」回退表。依据是 RimeWithWeasel.cpp 中
    /// _RimeGetIntStr(config, 主键, out, 别键, ...) 的调用点（第 1287–1310 行）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["style/layout/border"] = "style/layout/border_width",
            ["style/layout/corner_radius"] = "style/layout/round_corner",
            ["style/layout/hilited_corner_radius"] = "style/layout/round_corner",
            ["style/layout/hilite_padding_x"] = "style/layout/hilite_padding",
            ["style/layout/hilite_padding_y"] = "style/layout/hilite_padding",
        };

    /// <summary>
    /// weasel.yaml 中没有、只能从 C++ 侧取得的默认值。
    /// 每条都标注了上游出处；**未经核实不得添加条目**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 曾在此写错过一条，原委记录如下，改动前务必读完（RimeWithWeasel.cpp:1050）：
    /// <code>
    ///   void _RimeGetBool(cfg, key, cond, T&amp; value,
    ///                     const T&amp; trueValue = true,
    ///                     const T&amp; falseValue = false) {
    ///     Bool tempb = False;
    ///     if (config_get_bool(cfg, key, &amp;tempb) || cond)
    ///       value = (!!tempb) ? trueValue : falseValue;
    ///   }
    /// </code>
    /// 第 5 参是「键存在且为真时映射到的值」，**不是默认值**。
    /// 键缺失时走的是 falseValue 分支：initialize=true 时写 falseValue，
    /// initialize=false 时**完全不改**。
    /// 因此 style/enhanced_position（调用点 trueValue=true, falseValue=false）
    /// 在键缺失时的出厂默认 = falseValue = **false**，
    /// 与 UIStyle 构造函数的 enhanced_position(false)（WeaselIPCData.h:308）一致。
    /// </remarks>
    public static IReadOnlyDictionary<string, object?> CxxFallbacks { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // RimeWithWeasel.cpp:1355
            //   _RimeGetBool(config, "style/enhanced_position", initialize,
            //                style.enhanced_position, true, false);
            // 两个独立来源都是 false：
            //   ① _RimeGetBool 键缺失 → falseValue = false
            //   ② UIStyle 构造函数 enhanced_position(false)
            ["style/enhanced_position"] = false,
        };

    /// <summary>从 weasel.yaml 文本解析。失败返回空表而非抛异常。</summary>
    public static WeaselDefaults Parse(string weaselYamlText)
    {
        var result = YamlLoader.Load(weaselYamlText);
        return result.Success && result.Root is not null
            ? new WeaselDefaults(result.Root)
            : Empty;
    }

    /// <summary>
    /// 按扁平路径取值（如 "style/font_point"）。键缺失时按 Aliases 回退，
    /// 仍缺失则查 CxxFallbacks，最后返回 null。
    /// </summary>
    public object? Lookup(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        if (TryGetExact(path, out var value) && value is not null) return value;
        if (Aliases.TryGetValue(path, out var alias) &&
            TryGetExact(alias, out var aliasValue) && aliasValue is not null) return aliasValue;
        if (CxxFallbacks.TryGetValue(path, out var cxx)) return cxx;

        return null;
    }

    private bool TryGetExact(string path, out object? value)
    {
        value = null;
        var segments = path.Split('/');
        object? current = _root;

        foreach (var segment in segments)
        {
            if (current is not Dictionary<string, object?> mapping) return false;
            if (!mapping.TryGetValue(segment, out current)) return false;
        }

        value = current;
        return true;
    }

    public int Int(string path, int fallback) => Lookup(path) switch
    {
        int i => i,
        long l => (int)l,
        uint u => (int)u,
        ulong ul => (int)ul,
        double d => (int)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => (int)p,
        _ => fallback
    };

    public bool Bool(string path, bool fallback) => Lookup(path) switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s when bool.TryParse(s, out var p) => p,
        // YAML 1.1：yes/no/on/off 也算布尔（YamlLoader 已归一，这里兜底手写值）
        string s => s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("on", StringComparison.OrdinalIgnoreCase),
        _ => fallback
    };

    public string? Str(string path, string? fallback) => Lookup(path) switch
    {
        string s => s,
        null => fallback,
        object o => o.ToString()
    };

    /// <summary>
    /// 判定某个值是否等于出厂默认。用于「与出厂默认相同的值不落盘」。
    /// 比较按 Rime 语义归一：布尔、整数、字符串分别比较；颜色字面量按数值比较
    /// （避免用户写 0xECEEEE、面板生成 0xeceeee 被判为不同）。
    /// </summary>
    public bool IsFactoryValue(string path, object? candidate)
    {
        var factory = Lookup(path);

        if (factory is null || candidate is null)
            return factory is null && candidate is null;

        if (factory is bool fb && candidate is bool cb) return fb == cb;

        if (factory is string fs && candidate is string cs)
        {
            if (string.Equals(fs, cs, StringComparison.Ordinal)) return true;
            // 颜色字面量：忽略大小写与 0x / # 前缀差异
            var a = RimeColor.NormalizeColorCode(fs);
            var b = RimeColor.NormalizeColorCode(cs);
            return a is not null && b is not null &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        return Equals(factory, candidate);
    }
}

//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  最小 YAML 发射器。用于在「逐行手术式写入」中渲染补丁值里的结构化内容
//  （key_bindings 列表、punctuator 映射、switches 整段、preset_color_schemes 配色定义等），
//  对应 macOS 侧 `Yams.dump(object:)` 在 renderMaps / renderDict 中的用途。
//
//  为什么不直接用 YamlDotNet 序列化：
//    YamlDotNet 的 Serializer 只接受强类型对象或需手写 IYamlTypeConverter，
//    且对「0x 颜色字面量」「键排序」「引号风格」的控制是隐式的。
//    本发射器把全部规则显式化，与 YamlLineEditor 的引号判定完全同源
//    （复用 YamlLineEditor.IsPlainSafe），避免同一项目里出现两套标量转义规则。
//
//  保证：输出可被 librime（yaml-cpp）正确解析；不保证与 Yams 逐字节一致，
//  因为跨平台的验收标准是「Rime 能正确读到值」，而非文本相等。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WeaselPanel.Core.Yaml;

public static class YamlMiniEmitter
{
    private const int IndentStep = 2;

    /// <summary>把单个映射渲染成 YAML 行（相对缩进 0）。对应 Swift 的 <c>renderDict</c>。</summary>
    public static IReadOnlyList<string> EmitMapping(IReadOnlyDictionary<string, object?> dict)
    {
        var output = new List<string>();
        EmitMappingBody(dict, 0, output);
        return output;
    }

    /// <summary>
    /// 把「映射的列表」渲染成 YAML 行：每个元素首行以 "- " 开头，续行缩进 2 格。
    /// 对应 Swift 的 <c>renderMaps</c>。
    /// </summary>
    public static IReadOnlyList<string> EmitMapList(IReadOnlyList<IReadOnlyDictionary<string, object?>> list)
    {
        var output = new List<string>();
        foreach (var dict in list)
        {
            var body = new List<string>();
            EmitMappingBody(dict, 0, body);
            if (body.Count == 0) continue;

            for (var i = 0; i < body.Count; i++)
                output.Add(i == 0 ? "- " + body[i] : new string(' ', IndentStep) + body[i]);
        }
        return output;
    }

    private static void EmitMappingBody(IReadOnlyDictionary<string, object?> dict,
                                        int indent, List<string> output)
    {
        var pad = new string(' ', indent);
        // 与 macOS 侧 Yams.dump(sortKeys: true) 对齐：键按序号升序，保证输出稳定可比对
        foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = dict[key];
            var keyText = YamlLineEditor.IsPlainSafe(key) ? key : QuoteSingle(key);

            if (IsInlineScalar(value))
            {
                output.Add(pad + keyText + ": " + EmitScalar(key, value));
            }
            else if (value is IReadOnlyDictionary<string, object?> nested)
            {
                if (nested.Count == 0) { output.Add(pad + keyText + ": {}"); continue; }
                output.Add(pad + keyText + ":");
                EmitMappingBody(nested, indent + IndentStep, output);
            }
            else if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                var materialized = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                if (materialized.Count == 0) { output.Add(pad + keyText + ": {}"); continue; }
                output.Add(pad + keyText + ":");
                EmitMappingBody(materialized, indent + IndentStep, output);
            }
            else if (value is System.Collections.IEnumerable seq && value is not string)
            {
                var items = seq.Cast<object?>().ToList();
                if (items.Count == 0) { output.Add(pad + keyText + ": []"); continue; }
                output.Add(pad + keyText + ":");
                foreach (var item in items)
                {
                    if (IsInlineScalar(item)) output.Add(pad + new string(' ', IndentStep) + "- " + EmitScalar(key, item));
                    else if (item is IReadOnlyDictionary<string, object?> itemMap)
                    {
                        var body = new List<string>();
                        EmitMappingBody(itemMap, 0, body);
                        for (var i = 0; i < body.Count; i++)
                            output.Add(pad + (i == 0 ? "- " : new string(' ', IndentStep)) + body[i]);
                    }
                    else output.Add(pad + new string(' ', IndentStep) + "- " + EmitScalar(key, item));
                }
            }
            else if (value is null)
            {
                output.Add(pad + keyText + ":");
            }
            else
            {
                output.Add(pad + keyText + ": " + EmitScalar(key, value));
            }
        }
    }

    /// <summary>标量：可写在同一行的值。</summary>
    private static bool IsInlineScalar(object? value) => value switch
    {
        null => true,
        string => true,
        bool => true,
        int or long or short or byte or double or float or decimal => true,
        _ => false
    };

    /// <summary>
    /// 发射标量文本。
    /// 特例：键名以 _color 结尾时，十六进制颜色值一律用双引号包裹 —— 与
    /// CustomYamlFile.QuoteHexColorLiterals 的保护逻辑同源，确保落盘后仍是 "0x..."，
    /// 不会被 YAML 解析器（YAML 1.1 会把 0x 识别为整数）改写成十进制。
    /// </summary>
    public static string EmitScalar(string key, object? value)
    {
        if (value is null) return "null";

        if (value is bool b) return b ? "true" : "false";

        if (value is double d)
            return (d == Math.Round(d) && Math.Abs(d) < 1e9)
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("R", CultureInfo.InvariantCulture);

        if (value is float f)
            return (f == Math.Round(f) && Math.Abs(f) < 1e9)
                ? ((long)f).ToString(CultureInfo.InvariantCulture)
                : f.ToString("R", CultureInfo.InvariantCulture);

        if (value is decimal m)
            return m.ToString(CultureInfo.InvariantCulture);

        if (value is int or long or short or byte)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";

        var text = value as string ?? value.ToString() ?? "";

        if (key.EndsWith("_color", StringComparison.Ordinal) &&
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return "\"" + text + "\"";

        return YamlLineEditor.IsPlainSafe(text) ? text : QuoteSingle(text);
    }

    private static string QuoteSingle(string s) => "'" + s.Replace("'", "''") + "'";
}

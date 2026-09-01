//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  YAML 只读加载器。基于 YamlDotNet，**只用于解析与校验，绝不用于写入** ——
//  写入一律走 YamlLineEditor（逐行手术式）+ YamlMiniEmitter（结构化块渲染），
//  因为任何通用序列化器都会丢掉注释、重排键序、改写用户的手写格式。
//
//  标量类型推断刻意对齐 YAML 1.1 语义（与 macOS 侧 Yams 的行为一致）：
//  裸写的 true/false/yes/no/on/off 为布尔，0x 前缀为十六进制整数，
//  否则按整数 → 浮点 → 字符串依次尝试。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WeaselPanel.Core.Yaml;

public sealed class YamlLoadResult
{
    public bool Success { get; init; }
    public Dictionary<string, object?>? Root { get; init; }
    public string? Error { get; init; }

    public static YamlLoadResult Ok(Dictionary<string, object?> root) =>
        new() { Success = true, Root = root };

    public static YamlLoadResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public static class YamlLoader
{
    /// <summary>
    /// 解析 YAML 文本为 Rime 配置树。顶层必须为映射；空文本 / 纯注释文本视为空映射（可解析）。
    /// </summary>
    public static YamlLoadResult Load(string text)
    {
        var normalized = YamlText.NormalizeIndentation(text);
        var protectedText = YamlText.QuoteHexColorLiterals(normalized);

        if (protectedText.Trim().Length == 0)
            return YamlLoadResult.Ok(new Dictionary<string, object?>(StringComparer.Ordinal));

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(protectedText));

            if (stream.Documents.Count == 0)
                return YamlLoadResult.Ok(new Dictionary<string, object?>(StringComparer.Ordinal));

            var rootNode = stream.Documents[0].RootNode;
            if (rootNode is not YamlMappingNode mapping)
                return YamlLoadResult.Fail("顶层不是映射");

            if (ConvertMapping(mapping) is not Dictionary<string, object?> root)
                return YamlLoadResult.Fail("顶层不是映射");

            return YamlLoadResult.Ok(root);
        }
        catch (Exception ex)
        {
            // 解析失败 → 调用方进入「只读」状态，拒绝覆盖用户文件（安全底线）
            return YamlLoadResult.Fail(ex.Message);
        }
    }

    private static object? ConvertNode(YamlNode node) => node switch
    {
        YamlMappingNode m => ConvertMapping(m),
        YamlSequenceNode s => s.Children.Select(ConvertNode).ToList(),
        // 关键：YamlDotNet 的 YamlScalarNode.Value 已剥掉引号，只留内容。
        // 若只看内容做类型推断，带引号的 "0x1A2B3C" 会被误判成十六进制整数 ——
        // 而 YAML 语义里引号恰恰是「强制按字符串解析」的标记（Yams 亦如此处理）。
        // 这正是 QuoteHexColorLiterals 给颜色加引号的意义所在，此处必须呼应。
        YamlScalarNode sc when sc.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted => sc.Value,
        YamlScalarNode sc => ParseScalar(sc.Value),
        _ => null
    };

    private static object? ConvertMapping(YamlMappingNode mapping)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in mapping.Children)
        {
            var key = entry.Key is YamlScalarNode k ? k.Value : entry.Key.ToString();
            if (key is null) continue;
            result[key] = ConvertNode(entry.Value);
        }
        return result;
    }

    /// <summary>按 YAML 1.1 语义推断标量类型。</summary>
    public static object? ParseScalar(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();

        if (s.Length == 0 || s == "~" ||
            string.Equals(s, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "off", StringComparison.OrdinalIgnoreCase))
            return false;

        // 十六进制（YAML 1.1），如 0x6EC800
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2)
        {
            var digits = s.Substring(2).Replace("_", "");
            if (digits.Length > 0 && digits.All(Uri.IsHexDigit) &&
                long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;

        return raw;   // 原样返回（未 Trim），保留用户写的空格
    }
}

//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  写后回读校验：配置文件落盘后，重新解析并断言本次 PatchSet 中的每个键确实落地。
//  用于干掉「我以为写进去了其实没写进」的经典坑——未落地则调用方回滚 .bak 并抛错。
//
//  由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  WriteVerifier.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。
//
//  设计要点：
//   - 标量键（bool/int/double/string）严格比对值；
//   - 结构化键（列表/映射）由发射器往返保证，仅校验「键存在」；
//   - value 为 null 的键断言「应被删除」。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.IO;

public sealed class WriteVerificationException : Exception
{
    public WriteVerificationException(string message) : base("配置文件写后校验失败：" + message) { }
}

public static class WriteVerifier
{
    /// <summary>校验已落盘的（或临时的）文件文本。</summary>
    public static void VerifyText(string text, PatchSet patchSet)
    {
        var result = YamlLoader.Load(text);
        if (!result.Success || result.Root is null)
            throw new WriteVerificationException("写入结果无法解析");

        // 删光全部托管键后，patch: 段可能只剩注释而解析为 null——等同于空 patch（无托管键），
        // 此时所有「期望删除」的键都满足、所有「期望存在」的键都缺失，应按空字典处理而非报错。
        var patch = result.Root.TryGetValue("patch", out var p) && p is Dictionary<string, object?> pd
            ? pd
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, maybeValue) in patchSet.Enumerate())
        {
            var actual = ReadFlat(patch, key);

            if (maybeValue is not null)
            {
                if (actual is null)
                    throw new WriteVerificationException("键 " + key + " 写入后缺失");

                if (maybeValue.IsScalar && !ScalarEqual(actual, maybeValue))
                    throw new WriteVerificationException("键 " + key + " 写入值与预期不符");
                // 结构化值（列表/映射）由发射器往返序列化保证，仅校验存在性
            }
            else
            {
                if (actual is not null)
                    throw new WriteVerificationException("键 " + key + " 应被删除但仍存在");
            }
        }
    }

    /// <summary>校验磁盘上的文件。</summary>
    public static void Verify(string filePath, PatchSet patchSet)
    {
        var text = File.ReadAllText(filePath);
        VerifyText(text, patchSet);
    }

    // MARK: - 内部    /// <summary>按扁平路径读取 patch 节点下的值（如 "style/color_scheme"）；
    /// 同时兜底嵌套写法（极少数情况下键可能以真嵌套形式存在）。</summary>
    private static object? ReadFlat(Dictionary<string, object?> patch, string key)
    {
        if (patch.TryGetValue(key, out var v)) return v;

        var parts = key.Split('/');
        if (parts.Length <= 1) return null;

        object? node = patch;
        for (var i = 0; i < parts.Length; i++)
        {
            if (node is not Dictionary<string, object?> map) return null;
            if (!map.TryGetValue(parts[i], out node)) return null;
        }
        return node;
    }

    /// <summary>标量值比较：归一化整数 / 浮点 / 字符串 / 布尔的类型差异。</summary>
    private static bool ScalarEqual(object? actual, PatchValue value)
    {
        switch (value)
        {
            case PatchValue.BoolValue b:
                if (actual is bool ab) return ab == b.Value;
                if (actual is string as1) return (as1 == "true") == b.Value;
                return false;

            case PatchValue.IntValue i:
                if (actual is long or int or short or byte)
                    return Convert.ToInt64(actual, CultureInfo.InvariantCulture) == i.Value;
                if (actual is double d) return (long)d == i.Value;
                if (actual is float f) return (long)f == i.Value;
                if (actual is string si && long.TryParse(si, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var li)) return li == i.Value;
                return false;

            case PatchValue.DoubleValue dv:
                if (actual is double ad) return ad.Equals(dv.Value);
                if (actual is float af) return ((double)af).Equals(dv.Value);
                if (actual is long or int or short or byte)
                    return Convert.ToDouble(actual, CultureInfo.InvariantCulture).Equals(dv.Value);
                if (actual is string sd && double.TryParse(sd, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ld)) return ld.Equals(dv.Value);
                return false;

            case PatchValue.StringValue s:
                return actual is string aStr && string.Equals(aStr, s.Value, StringComparison.Ordinal);

            default:
                return false;
        }
    }
}

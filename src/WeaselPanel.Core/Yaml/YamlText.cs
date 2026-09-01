//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  YAML 文本的预处理工具：缩进空白归一化、十六进制颜色字面量加引号保护。
//  两者均由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  CustomYAMLFile.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。
//
//  这两段逻辑是 macOS 侧踩过真坑后加的，Windows 侧必须原样保留：
//   · 归一化 —— 某些第三方配置集用 U+2005 等特殊空格做缩进，会让解析器直接报
//     「无法解析」→ 文件进入只读 → 整个面板按钮变灰。
//   · 加引号 —— YAML 1.1 会把 0x6EC800 在解析阶段识别为整数（7251968），重新写出时
//     被序列化成十进制，输入法无法识别 → 颜色失效（GitHub issue）。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WeaselPanel.Core.Yaml;

public static class YamlText
{
    /// <summary>YAML 规范只允许 U+0020(空格) 与 U+0009(Tab) 作为结构空白。
    /// 以下「特殊空格」若出现在行首缩进位置，会被解析器判为非法。</summary>
    private static readonly HashSet<char> StructuralWhitespace = BuildStructuralWhitespace();

    private static HashSet<char> BuildStructuralWhitespace()
    {
        var set = new HashSet<char>();
        for (var cp = 0x2000; cp <= 0x200A; cp++) set.Add((char)cp);
        set.Add((char)0x202F);
        set.Add((char)0x205F);
        set.Add((char)0x3000);
        return set;
    }

    /// <summary>
    /// 把每行「行首缩进段」里的特殊空格替换为普通空格，其余内容原样保留。
    /// 只处理行首连续空白（普通空格 / Tab / 特殊空格），不碰引号内或值中的特殊空格。
    /// </summary>
    public static string NormalizeIndentation(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        lines.Add(current.ToString());

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var indentEnd = 0;
            while (indentEnd < line.Length)
            {
                var ch = line[indentEnd];
                if (ch == ' ' || ch == '\t') { indentEnd++; continue; }
                if (StructuralWhitespace.Contains(ch)) { indentEnd++; continue; }
                break;
            }
            if (indentEnd > 0) lines[i] = new string(' ', indentEnd) + line.Substring(indentEnd);
        }
        return string.Join('\n', lines);
    }

    // 匹配行首缩进 + 以 _color 结尾的键 + 冒号 + 空白 + 0x 十六进制字面量（允许下划线分隔）
    //
    // 键名部分允许「/」：本面板的补丁键一律写成扁平路径
    // （如 preset_color_schemes/aqua/back_color），这是 Rime patch 的强制写法
    // （嵌套映射会整体覆盖父节点）。macOS 侧原正则为 [\w\-]+_color，无法匹配扁平路径键，
    // 导致「面板用扁平键写入的颜色」读回时被 YAML 1.1 解析成整数而失真。
    // Windows 侧在此修正，建议同步回 Squirrel Panel。
    private static readonly Regex HexColorLiteralPattern = new(
        @"^((\s*[\w\-/]+_color\s*:\s*)(0x[0-9A-Fa-f][0-9A-Fa-f_]*[0-9A-Fa-f]))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 在解析前，把「键名以 _color 结尾」且未加引号的 0x 十六进制颜色字面量补上双引号。
    /// 这样解析器会把它作为字符串保留，重写出盘时仍以 "0x..." 形式落盘，
    /// 输入法可正常识别，彻底避免被改写成十进制整数。
    ///
    /// 仅匹配 _color: 键（Rime 全部颜色键均以 _color 结尾），不会误伤其它整数；
    /// 对于已经是 "0x..."（带引号）的值，由于 0x 前存在引号，模式不匹配，不会被重复处理。
    /// 支持 Rime 允许的下划线分隔写法（如 0xee_fa_3a_0a）。
    /// </summary>
    public static string QuoteHexColorLiterals(string text)
    {
        return HexColorLiteralPattern.Replace(text, m =>
        {
            var value = m.Groups[3].Value;
            var prefix = m.Groups[2].Value;
            return prefix + "\"" + value + "\"";
        });
    }
}

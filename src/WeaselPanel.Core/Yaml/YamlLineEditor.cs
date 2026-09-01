//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  逐行手术式 YAML 编辑：只重写目标行，保留其它行的注释、格式与用户手改内容。
//
//  Portions adapted from TriFecta (https://github.com/thesadbee/TriFecta),
//  original author thesadbee. Licensed under GPL-3.0.
//  本文件由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  YamlLineEditor.swift 直译而来，随 Weasel Panel 以相同协议（GPL-3.0）分发，
//  保留原作者署名与协议声明。
//
//  本编辑器完全基于纯文本行解析，不依赖任何 YAML 序列化库，因此不会重新序列化整个文件、
//  不会重排键顺序、不会丢掉用户写在配置文件里的注释。
//
//  直译纪律：本类的行号级行为必须与 Swift 原版逐一对齐，
//  以便用 macOS 侧同一批测试用例做交叉验证。除字符串索引（Swift String.Index → C# int）
//  与错误传播（Swift throws → C# 异常）外，不得引入行为差异。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WeaselPanel.Core.Yaml;

/// <summary>YAML 标量值。与 Swift 版 <c>YamlScalar</c> 一一对应。</summary>
public sealed class YamlScalar : IEquatable<YamlScalar>
{
    public enum Kind { String, Number, Bool }

    public Kind ScalarKind { get; }
    public string Payload { get; }   // String / Number 的原文
    public bool BoolValue { get; }

    private YamlScalar(Kind kind, string payload, bool boolValue)
    {
        ScalarKind = kind;
        Payload = payload;
        BoolValue = boolValue;
    }

    public static YamlScalar Str(string s) => new(Kind.String, s, false);
    public static YamlScalar Number(string raw) => new(Kind.Number, raw, false);
    public static YamlScalar BoolOf(bool b) => new(Kind.Bool, b ? "true" : "false", b);

    /// <summary>
    /// 与官方基线一致：颜色等全部用裸 0x 字面量（librime 对 0x 前缀值保留 hex 表示；
    /// 输入法侧读取颜色的正则也要求 0x 前缀；带引号同样兼容）。
    /// </summary>
    public static YamlScalar HexColor(uint value) => Number("0x" + value.ToString("X8"));

    public bool Equals(YamlScalar? other) =>
        other is not null && ScalarKind == other.ScalarKind &&
        Payload == other.Payload && BoolValue == other.BoolValue;

    public override bool Equals(object? obj) => obj is YamlScalar s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(ScalarKind, Payload, BoolValue);
    public override string ToString() => ScalarKind == Kind.Bool ? (BoolValue ? "true" : "false") : Payload;
}

public sealed class YamlLineEditorException : Exception
{
    public YamlLineEditorException(string message) : base("YamlLineEditor: " + message) { }
}

public sealed partial class YamlLineEditor
{
    private readonly List<string> _lines;
    private readonly bool _hadTrailingNewline;

    /// <summary>行解析结果。</summary>
    public sealed class LineInfo
    {
        public int Indent { get; init; }
        /// <summary>去引号后的键（如 ascii_composer/switch_key/Shift_L）。</summary>
        public string Key { get; init; } = "";
        /// <summary>原样键文本（可能带引号）。</summary>
        public string KeyText { get; init; } = "";
    }

    public YamlLineEditor(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        _hadTrailingNewline = normalized.EndsWith('\n');
        var comps = normalized.Split('\n').ToList();
        if (comps.Count > 1 && comps[^1].Length == 0) comps.RemoveAt(comps.Count - 1);
        _lines = comps;
    }

    public IReadOnlyList<string> Lines => _lines;

    public string Text
    {
        get
        {
            var output = string.Join('\n', _lines);
            // YAML 文件一律以换行结尾（原始文件缺失时补上，幂等）
            if (_hadTrailingNewline || _lines.Count > 0) output += "\n";
            return output;
        }
    }

    // MARK: 公开操作

    /// <summary>设置 section 下的标量键。keyText 为文件中的键原文（可含引号）。</summary>
    public void SetScalar(string section, string keyText, YamlScalar value)
    {
        var sectionIndex = FindTopLevelSectionIndex(section);
        if (sectionIndex.HasValue) SetScalarInSection(sectionIndex.Value, keyText, value);
        else AppendSection(section, new[] { (keyText, value) });
    }

    /// <summary>顶层节的子键批量设置；节不存在则在文件末尾创建。</summary>
    public void SetSectionValues(string section, IEnumerable<(string KeyText, YamlScalar Value)> values)
    {
        var list = values.ToList();
        var sectionIndex = FindTopLevelSectionIndex(section);
        if (sectionIndex.HasValue)
        {
            foreach (var (key, value) in list) SetScalarInSection(sectionIndex.Value, key, value);
        }
        else AppendSection(section, list);
    }

    /// <summary>
    /// 替换路径（如 ["patch", "schema_list"]）下的整个列表块。items 为不带缩进的条目文本。
    /// 块键缺失但父节存在时，在父节键行之后插入。
    /// </summary>
    public void ReplaceBlockList(IReadOnlyList<string> path, IReadOnlyList<string> items)
    {
        var keyIndex = FindPathKeyIndex(path);
        if (!keyIndex.HasValue)
        {
            var parent = path.Take(path.Count - 1).ToList();
            var parentIndex = FindPathKeyIndex(parent);
            if (parentIndex.HasValue)
            {
                var info = ParseLine(_lines[parentIndex.Value]) ?? new LineInfo
                {
                    Indent = (parent.Count - 1) * 2,
                    Key = parent.Count > 0 ? parent[^1] : "",
                    KeyText = parent.Count > 0 ? parent[^1] : ""
                };
                var keyText = path[^1];
                var keyIndent = info.Indent + 2;
                var block = new List<string> { new string(' ', keyIndent) + keyText + ":" };
                block.AddRange(items.Select(i => new string(' ', keyIndent + 2) + i));
                _lines.InsertRange(parentIndex.Value + 1, block);
                return;
            }
            throw new YamlLineEditorException("未找到块 " + string.Join("/", path));
        }

        var keyInfo = ParseLine(_lines[keyIndex.Value]) ?? new LineInfo
        {
            Indent = (path.Count - 1) * 2,
            Key = path[^1],
            KeyText = path[^1]
        };
        var itemIndent = keyInfo.Indent + 2;

        var bodyStart = keyIndex.Value + 1;
        while (bodyStart < _lines.Count && _lines[bodyStart].Trim().Length == 0) bodyStart++;

        var bodyEnd = bodyStart;
        while (bodyEnd < _lines.Count)
        {
            var line = _lines[bodyEnd];
            if (line.Trim().Length == 0) { bodyEnd++; continue; }
            var info = ParseLine(line);
            if (info is not null && info.Indent <= keyInfo.Indent) break;
            bodyEnd++;
        }
        // 回退末尾连续空行（属于分隔）
        var lastItem = bodyEnd;
        while (lastItem > bodyStart && _lines[lastItem - 1].Trim().Length == 0) lastItem--;

        var indentStr = new string(' ', itemIndent);
        _lines.RemoveRange(bodyStart, lastItem - bodyStart);
        _lines.InsertRange(bodyStart, items.Select(i => indentStr + i));
    }

    // MARK: 行解析

    public LineInfo? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;

        var indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
        if (line.Substring(0, indent).Contains('\t'))
            throw new YamlLineEditorException("制表符缩进：" + line);

        var rest = line.Substring(indent);
        var colon = KeyColonIndex(rest);
        if (colon < 0) return null;

        var keyText = rest.Substring(0, colon).Trim();
        if (keyText.Length == 0) return null;
        return new LineInfo { Indent = indent, Key = Unquote(keyText), KeyText = keyText };
    }

    private static int KeyColonIndex(string rest)
    {
        if (rest.StartsWith('"') || rest.StartsWith('\''))
        {
            var q = rest[0];
            var i = 1;
            int closing = -1;
            while (i < rest.Length)
            {
                if (rest[i] == q)
                {
                    if (i + 1 < rest.Length && rest[i + 1] == q) { i += 2; continue; }  // '' 转义
                    closing = i;
                    break;
                }
                i++;
            }
            if (closing < 0) return -1;
            var j = closing + 1;
            while (j < rest.Length && rest[j] == ' ') j++;
            return (j < rest.Length && rest[j] == ':') ? j : -1;
        }

        for (var offset = 0; offset < rest.Length; offset++)
        {
            var ch = rest[offset];
            if (ch == ':')
            {
                if (offset + 1 == rest.Length) return offset;
                var next = rest[offset + 1];
                if (next == ' ' || next == '\t' || next == '#') return offset;
            }
            if (ch == ' ' || ch == '\t' || ch == '#') return -1;
        }
        return -1;
    }

    /// <summary>扫描行内注释：返回（值结束位置，注释起始位置）。引号区域内的 # 不算注释。</summary>
    private static (int ValueEnd, int CommentStart) SplitCommentRaw(string s)
    {
        var inSingle = false;
        var inDouble = false;
        var skipNextSingle = false;
        var skipNextDouble = false;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (inSingle)
            {
                if (skipNextSingle) skipNextSingle = false;
                else if (c == '\'')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\'') { skipNextSingle = true; i++; }
                    else inSingle = false;
                }
            }
            else if (inDouble)
            {
                if (skipNextDouble) skipNextDouble = false;
                else if (c == '\\') skipNextDouble = true;
                else if (c == '"') inDouble = false;
            }
            else
            {
                if (c == '\'') inSingle = true;
                else if (c == '"') inDouble = true;
                else if (c == '#' && i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t'))
                    return (i, i);
            }
            i++;
        }
        return (s.Length, -1);
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2) return s;
        var first = s[0];
        if (first != '\'' && first != '"') return s;
        var inner = s.Substring(1, s.Length - 2);
        return first == '\'' ? inner.Replace("''", "'") : inner;
    }

    private static char? QuoteCharOf(string s)
    {
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return '\'';
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return '"';
        return null;
    }

    // MARK: 节与块

    /// <summary>顶层键的缩进 = 全部键行缩进的最小值（支持整文件统一缩进 0 或 2 的变体）。</summary>
    private int TopLevelIndent()
    {
        var minIndent = int.MaxValue;
        foreach (var line in _lines)
        {
            var info = ParseLine(line);
            if (info is not null) minIndent = Math.Min(minIndent, info.Indent);
        }
        return minIndent == int.MaxValue ? 0 : minIndent;
    }

    private int? FindTopLevelSectionIndex(string section)
    {
        var topIndent = TopLevelIndent();
        for (var i = 0; i < _lines.Count; i++)
        {
            var info = ParseLine(_lines[i]);
            if (info is not null && info.Indent == topIndent && info.Key == section) return i;
        }
        return null;
    }

    private void SetScalarInSection(int sectionIndex, string keyText, YamlScalar value)
    {
        var targetKey = Unquote(keyText);
        var sectionIndent = ParseLine(_lines[sectionIndex])!.Indent;
        var baseIndent = sectionIndent + 2;
        var i = sectionIndex + 1;
        var lastBodyLine = sectionIndex;

        while (i < _lines.Count)
        {
            var line = _lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            var info = ParseLine(line);
            if (info is null)
            {
                lastBodyLine = i;   // 列表条目/子结构，属于节体
                i++;
                continue;
            }
            if (info.Indent <= sectionIndent) break;
            if (info.Indent == baseIndent && info.Key == targetKey)
            {
                _lines[i] = ReplaceValue(line, keyText, value);
                return;
            }
            lastBodyLine = i;
            i++;
        }

        _lines.Insert(lastBodyLine + 1,
            new string(' ', baseIndent) + keyText + ": " + Emit(value));
    }

    /// <summary>替换键行上的值：保留键名样式、行内引号风格、行尾注释及注释前的空白。</summary>
    public string ReplaceValue(string line, string keyText, YamlScalar value)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        var rest = line.Substring(indent);
        var colon = KeyColonIndex(rest);
        if (colon < 0) return line;

        var keyPart = line.Substring(0, indent) + rest.Substring(0, colon);
        var valueAndComment = rest.Substring(colon + 1);
        var (valueEnd, commentStart) = SplitCommentRaw(valueAndComment);
        var rawValuePart = valueAndComment.Substring(0, valueEnd);
        var quoted = QuoteCharOf(rawValuePart.Trim());

        var output = keyPart + ": " + Emit(value, quoted);
        if (commentStart >= 0)
        {
            // 保留值与注释之间的原始空白
            var lastNonSpace = -1;
            for (var k = rawValuePart.Length - 1; k >= 0; k--)
            {
                if (rawValuePart[k] != ' ' && rawValuePart[k] != '\t') { lastNonSpace = k; break; }
            }
            var gapStart = lastNonSpace + 1;
            var gap = gapStart < commentStart ? valueAndComment.Substring(gapStart, commentStart - gapStart) : " ";
            output += gap + valueAndComment.Substring(commentStart);
        }
        return output;
    }

    private void AppendSection(string section, IReadOnlyList<(string KeyText, YamlScalar Value)> subkeys)
    {
        var topIndent = TopLevelIndent();
        while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);

        var block = new List<string> { new string(' ', topIndent) + section + ":" };
        foreach (var (key, value) in subkeys)
            block.Add(new string(' ', topIndent + 2) + key + ": " + Emit(value));
        _lines.AddRange(block);
    }

    private int? FindPathKeyIndex(IReadOnlyList<string> path)
    {
        if (path.Count == 0) return null;
        var expectedIndent = -2;
        var searchFrom = 0;
        foreach (var segment in path)
        {
            int? found = null;
            var i = searchFrom;
            while (i < _lines.Count)
            {
                var info = ParseLine(_lines[i]);
                if (info is null) { i++; continue; }
                if (info.Indent == expectedIndent + 2 && info.Key == segment) { found = i; break; }
                i++;
            }
            if (!found.HasValue) return null;
            expectedIndent += 2;
            searchFrom = found.Value + 1;
            if (segment == path[^1]) return found;
        }
        return null;
    }

    // MARK: 值发射

    public string Emit(YamlScalar scalar, char? preferQuote = null)
    {
        if (preferQuote.HasValue)
        {
            var s = ScalarString(scalar);
            return preferQuote.Value == '\''
                ? "'" + s.Replace("'", "''") + "'"
                : "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        return scalar.ScalarKind switch
        {
            YamlScalar.Kind.Number => scalar.Payload,
            YamlScalar.Kind.Bool => scalar.BoolValue ? "true" : "false",
            _ => IsPlainSafe(scalar.Payload)
                ? scalar.Payload
                : "'" + scalar.Payload.Replace("'", "''") + "'"
        };
    }

    private static string ScalarString(YamlScalar scalar) => scalar.ScalarKind switch
    {
        YamlScalar.Kind.Number => scalar.Payload,
        YamlScalar.Kind.Bool => scalar.BoolValue ? "true" : "false",
        _ => scalar.Payload
    };

    /// <summary>字符串值是否可用 YAML plain style 裸写（避免被解析为其它类型或非法字符）。</summary>
    public static bool IsPlainSafe(string s)
    {
        if (s.Length == 0) return false;

        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "true","false","yes","no","on","off","null","~",
            "True","False","Yes","No","On","Off","Null","NULL"
        };
        if (reserved.Contains(s)) return false;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2 &&
            s.Skip(2).All(Uri.IsHexDigit)) return false;

        var first = s[0];
        if (!(char.IsLetter(first) || first == '_')) return false;

        const string extra = "_./,+-";
        return s.All(c => char.IsLetterOrDigit(c) || extra.IndexOf(c) >= 0);
    }
}

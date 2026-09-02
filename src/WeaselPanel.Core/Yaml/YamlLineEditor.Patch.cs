//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  YamlLineEditor 的第二部分，对应 Swift 原版的
//  `extension YamlLineEditor`（适配「扁平路径键统一位于 patch: 段」的写入模型）。
//  用 partial class 实现，以便直接访问内部行表，避免对外暴露可变行集合。
//
//  本文件由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  YamlLineEditor.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Yaml;

public sealed partial class YamlLineEditor
{
    /// <summary>
    /// 把 Weasel Panel 的 PatchValue 应用到 patch 段下的扁平键（如 "style/color_scheme"）。
    /// 对应 Swift 版 <c>YamlLineEditor.applyPatchValue(_:value:)</c>。
    /// </summary>
    public void ApplyPatchValue(string key, PatchValue value)
    {
        switch (value)
        {
            case PatchValue.BoolValue v:
                SetScalar("patch", key, YamlScalar.BoolOf(v.Value));
                break;

            case PatchValue.IntValue v:
                SetScalar("patch", key, YamlScalar.Number(v.Value.ToString(CultureInfo.InvariantCulture)));
                break;

            case PatchValue.DoubleValue v:
                // 整数值写成整数，避免 16.0 这种冗余写法
                var n = v.Value == Math.Round(v.Value) && Math.Abs(v.Value) < 1e9
                    ? ((long)v.Value).ToString(CultureInfo.InvariantCulture)
                    : v.Value.ToString("R", CultureInfo.InvariantCulture);
                SetScalar("patch", key, YamlScalar.Number(n));
                break;

            case PatchValue.StringValue v:
                SetScalar("patch", key, YamlScalar.Str(v.Value));
                break;

            case PatchValue.StringListValue v:
                ReplaceBlockVerbatim(new[] { "patch", key },
                    v.Value.Select(s => "- " + QuoteIfNeeded(s)).ToList());
                break;

            case PatchValue.SchemaListValue v:
                ReplaceBlockVerbatim(new[] { "patch", key },
                    v.Value.Select(s => "- schema: " + QuoteIfNeeded(s)).ToList());
                break;

            case PatchValue.KeyBindingsValue kb:
                ReplaceBlockVerbatim(new[] { "patch", key }, EmitMapList(kb.Value));
                break;

            case PatchValue.MapListValue ml:
                ReplaceBlockVerbatim(new[] { "patch", key }, EmitMapList(ml.Value));
                break;

            case PatchValue.PunctuationValue pu:
                ReplaceBlockVerbatim(new[] { "patch", key }, YamlMiniEmitter.EmitMapping(pu.Value));
                break;

            case PatchValue.DictionaryValue di:
                ReplaceBlockVerbatim(new[] { "patch", key }, YamlMiniEmitter.EmitMapping(di.Value));
                break;

            default:
                throw new YamlLineEditorException("未知的 PatchValue 类型：" + value.GetType().Name);
        }
    }

    /// <summary>对需加引号的字符串做最小引号包裹（复用 IsPlainSafe 判断）。</summary>
    public static string QuoteIfNeeded(string s) =>
        IsPlainSafe(s) ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static List<string> EmitMapList(IReadOnlyList<Dictionary<string, object?>> list) =>
        YamlMiniEmitter.EmitMapList(list.Cast<IReadOnlyDictionary<string, object?>>().ToList()).ToList();

    /// <summary>
    /// 替换路径下的整块（列表或映射）。items 为「相对块缩进」的 YAML 行（第 0 缩进），
    /// 方法内部按块实际缩进整体重缩进后替换；父键缺失时在父节键行之后插入。
    /// items 为空数组表示「整块删除（含键行）」，用于实现写 nil。
    /// </summary>
    public void ReplaceBlockVerbatim(IReadOnlyList<string> path, IReadOnlyList<string> items)
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
        var blockIndent = keyInfo.Indent + 2;

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
        var lastItem = bodyEnd;
        while (lastItem > bodyStart && _lines[lastItem - 1].Trim().Length == 0) lastItem--;

        if (items.Count == 0)
        {
            // 整块删除（含键行），实现写 nil；同样保留节级注释，避免误删用户内容。
            RemoveRangePreservingComments(keyIndex.Value, keyInfo, lastItem);
            return;
        }

        var indented = items.Select(i => new string(' ', blockIndent) + i).ToList();
        _lines.RemoveRange(bodyStart, lastItem - bodyStart);
        _lines.InsertRange(bodyStart, indented);
    }

    /// <summary>
    /// 删除路径下的键及其整块（含更深缩进的行），实现写 nil。键不存在则幂等无操作。
    ///
    /// 关键：删除范围 [keyIndex, bodyEnd) 内，缩进 &lt;= 被删键的<b>注释行属于节级注释</b>，
    /// 与被删键无隶属关系，必须保留——否则「行级删托管键」会把它后面紧跟的用户手写注释
    /// 一起吃掉，违背「用户手写的配置一个字都不能弄丢」的设计底线。
    /// 更深缩进的注释（被删键块内的子注释）随块一起删。
    /// </summary>
    public void RemoveKeyAtPath(IReadOnlyList<string> path)
    {
        // 用展开后的路径定位：文件里可能是嵌套写法（menu: / page_size:），
        // 只看字面路径会找不到、删不掉。
        var resolved = ResolvePath(path);
        if (resolved is null) return;
        var (keyIndex, resolvedPath, intermediate) = resolved.Value;

        var keyInfo = ParseLine(_lines[keyIndex]) ?? new LineInfo
        {
            Indent = (resolvedPath.Count - 1) * 2,
            Key = resolvedPath[^1],
            KeyText = resolvedPath[^1]
        };
        var bodyEnd = BlockBodyEnd(keyIndex, keyInfo.Indent);
        RemoveRangePreservingComments(keyIndex, keyInfo, bodyEnd);

        // 删完之后，若某级容器因此变成空壳（menu: 下面再没有任何键），连它一起删。
        // 留下 menu: 这种空映射不是「无害的空白」—— Rime 读到的是「把 menu 整个设为
        // null」，比留着原值更糟。
        PruneEmptyContainers(resolvedPath, intermediate);
    }

    /// <summary>定位路径：先按字面（扁平写法），再按斜杠展开（嵌套写法）。返回行号、生效路径与中间级标记。</summary>
    private (int Index, List<string> Path, List<bool> Intermediate)? ResolvePath(IReadOnlyList<string> path)
    {
        var direct = FindPathKeyIndexCore(path);
        // 扁平写法命中的话，"menu/page_size" 本身就是文件里的真键，没有中间级可清。
        if (direct.HasValue) return (direct.Value, path.ToList(), new List<bool>(new bool[path.Count]));

        var expanded = ExpandPath(path, out var intermediate);
        if (expanded.Count == path.Count) return null;
        var nested = FindPathKeyIndexCore(expanded);
        return nested.HasValue ? (nested.Value, expanded, intermediate) : null;
    }

    /// <summary>
    /// 自下而上清掉「只是通往真键的容器、且已经空了」的中间级。
    /// 只清由斜杠展开产生的中间级：用户自己手写的容器即便空了也保留。
    /// 块里还有注释时不删 —— 宁可留个空壳，也不删用户写的字。
    /// </summary>
    private void PruneEmptyContainers(IReadOnlyList<string> resolvedPath, IReadOnlyList<bool> intermediate)
    {
        if (resolvedPath.Count != intermediate.Count) return;

        for (var depth = resolvedPath.Count - 1; depth >= 1; depth--)
        {
            if (!intermediate[depth]) continue;

            var parent = resolvedPath.Take(depth + 1).ToList();
            var index = FindPathKeyIndexCore(parent);
            if (!index.HasValue) continue;

            var info = ParseLine(_lines[index.Value]);
            if (info is null) continue;

            var bodyEnd = BlockBodyEnd(index.Value, info.Indent);
            var hasBody = false;
            for (var i = index.Value + 1; i < bodyEnd; i++)
                if (_lines[i].Trim().Length != 0) { hasBody = true; break; }

            if (hasBody) break;          // 底下还有东西，再往上也不该动
            _lines.RemoveAt(index.Value);
        }
    }

    /// <summary>
    /// 从 keyIndex+1 起，找到第一个「缩进 &lt;= keyIndent 的真实键值行」为止的块体右界。
    /// 注释行（ParseLine 返回 null）与更深缩进行都归入块体，不提前断开。
    /// </summary>
    private int BlockBodyEnd(int keyIndex, int keyIndent)
    {
        var bodyEnd = keyIndex + 1;
        while (bodyEnd < _lines.Count)
        {
            var line = _lines[bodyEnd];
            if (line.Trim().Length == 0) { bodyEnd++; continue; }
            var info = ParseLine(line);
            if (info is not null && info.Indent <= keyIndent) break;
            bodyEnd++;
        }
        return bodyEnd;
    }

    /// <summary>
    /// 删除 [keyIndex, bodyEnd) 区间，但保留缩进 &lt;= keyInfo.Indent 的注释行（节级注释）。
    /// 被删键行（keyIndex）恒删；更深缩进的注释随块删。
    /// </summary>
    private void RemoveRangePreservingComments(int keyIndex, LineInfo keyInfo, int bodyEnd)
    {
        var removals = new List<int>();
        for (var i = keyIndex; i < bodyEnd; i++)
        {
            var line = _lines[i];
            if (line.Trim().Length == 0) { removals.Add(i); continue; }

            LineInfo? info;
            try { info = ParseLine(line); }
            catch { info = null; }

            if (info is not null)
            {
                // 键值行（含被删键与同缩进兄弟键）：被删键在 keyIndex 必删；
                // 兄弟键不会落入本区间，因为 BlockBodyEnd 已在首个 <= 缩进的兄弟键处断开。
                removals.Add(i);
            }
            else
            {
                // 注释 / 无法解析行：仅当缩进 <= 被删键才保留（节级注释），否则随块删。
                var indent = 0;
                while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
                if (indent > keyInfo.Indent) removals.Add(i);
            }
        }
        for (var k = removals.Count - 1; k >= 0; k--) _lines.RemoveAt(removals[k]);
    }
}

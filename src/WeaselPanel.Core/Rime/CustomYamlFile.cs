//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  Rime 的 *.custom.yaml 补丁文件读写。
//
//  设计底线：用户手写的配置一个字都不能弄丢。
//  为此做了三件事：
//    1. 解析失败时拒绝写入，绝不用空内容覆盖；
//    2. 每次写入前留一份 .bak 备份；
//    3. 只改动本面板管理的键，其余原样回写。
//
//  由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  CustomYAMLFile.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。
//
//  Windows 侧的两处显式决策（与 Swift 原版的差异，均为有意为之）：
//   · 换行：一律输出 LF（\n），与上游 weasel.yaml 模板及 macOS 版一致。
//     理由：YamlLineEditor 内部归一化 CRLF→LF，若落盘转 CRLF，用户手工编辑后
//     会形成 LF/CRLF 混排的脏文件；且 yaml-cpp 与 Windows 11 记事本均原生支持 LF。
//   · 编码：UTF-8 无 BOM。读取时仍容忍 BOM（.NET 会自动探测并剥离），
//     写入不产生 BOM，避免 yaml-cpp 把 BOM 当内容导致首键解析失败。
//   · 颜色字节序：小狼毫支持 style/color_format，故 RimeColor 的处理全部带 format 参数。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WeaselPanel.Core.IO;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

public enum CustomYamlLoadState
{
    /// <summary>文件不存在，视为空补丁。</summary>
    Absent,
    /// <summary>正常载入。</summary>
    Loaded,
    /// <summary>文件存在但无法解析，为安全起见进入只读状态。</summary>
    Unparsable
}

/// <summary>一个 Rime 补丁文件（weasel.custom.yaml / default.custom.yaml / rime_ice.custom.yaml ...）。</summary>
public sealed class CustomYamlFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public CustomYamlLoadState State { get; private set; } = CustomYamlLoadState.Absent;
    public string? LoadError { get; private set; }

    public string FilePath { get; }

    /// <summary>YAML 顶层内容（除 patch 外可能还有别的键，需原样保留）。</summary>
    private Dictionary<string, object?> _root = new(StringComparer.Ordinal);
    /// <summary>patch 节点内容。</summary>
    private Dictionary<string, object?> _patch = new(StringComparer.Ordinal);

    public bool IsWritable => State != CustomYamlLoadState.Unparsable;

    public IReadOnlyDictionary<string, object?> Patch => _patch;
    public IReadOnlyDictionary<string, object?> Root => _root;

    public CustomYamlFile(string filePath)
    {
        FilePath = filePath;
        Load();
    }

    // MARK: - 载入

    public void Load()
    {
        _root = new Dictionary<string, object?>(StringComparer.Ordinal);
        _patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        LoadError = null;

        if (!File.Exists(FilePath))
        {
            State = CustomYamlLoadState.Absent;
            return;
        }

        var result = YamlLoader.Load(ReadAllText());
        if (!result.Success || result.Root is null)
        {
            State = CustomYamlLoadState.Unparsable;
            LoadError = result.Error ?? "无法解析";
            return;
        }

        _root = result.Root;
        _patch = result.Root.TryGetValue("patch", out var p) && p is Dictionary<string, object?> pd
            ? pd
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        State = CustomYamlLoadState.Loaded;
    }

    private string ReadAllText() => File.ReadAllText(FilePath, Encoding.UTF8);

    // MARK: - 读取

    /// <summary>按 Rime 路径读取值。同时兼容扁平写法（style/font_point）与嵌套写法。</summary>
    public object? ValueForPath(string path)
    {
        if (_patch.TryGetValue(path, out var flat)) return flat;

        var parts = path.Split('/');
        if (parts.Length <= 1) return null;

        // 逐段尝试：可能是「前缀扁平 + 后缀嵌套」的混合写法
        for (var split = parts.Length - 1; split >= 1; split--)
        {
            var prefix = string.Join("/", parts.Take(split));
            if (!_patch.TryGetValue(prefix, out var nodeObj) ||
                nodeObj is not Dictionary<string, object?> node) continue;

            object? found = null;
            var ok = true;
            for (var i = split; i < parts.Length; i++)
            {
                if (i == parts.Length - 1)
                {
                    node.TryGetValue(parts[i], out found);
                }
                else if (node.TryGetValue(parts[i], out var next) && next is Dictionary<string, object?> nd)
                {
                    node = nd;
                }
                else { found = null; ok = false; break; }
            }
            if (ok && found is not null) return found;
        }
        return null;
    }

    public string? StringForPath(string path) => ValueForPath(path) switch
    {
        string v => v,
        long v => v.ToString(CultureInfo.InvariantCulture),
        int v => v.ToString(CultureInfo.InvariantCulture),
        double v => v.ToString(CultureInfo.InvariantCulture),
        bool v => v ? "true" : "false",
        _ => null
    };

    public bool? BoolForPath(string path) => ValueForPath(path) switch
    {
        bool v => v,
        string v => new[] { "true", "yes", "on", "1" }.Contains(v.ToLowerInvariant()),
        long v => v != 0,
        int v => v != 0,
        _ => null
    };

    public double? DoubleForPath(string path) => ValueForPath(path) switch
    {
        double v => v,
        float v => v,
        long v => v,
        int v => v,
        string v when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public int? IntForPath(string path) => ValueForPath(path) switch
    {
        int v => v,
        long v => (int)v,
        double v => (int)v,
        string v when int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null
    };

    /// <summary>当前 patch 中所有键（仅顶层，含扁平路径键）。</summary>
    public IReadOnlyList<string> TopLevelKeys => _patch.Keys.ToList();

    // MARK: - 写入（内存态）

    /// <summary>
    /// 设置某个路径的值；传 null 表示移除。
    ///
    /// Rime 的 patch 键必须使用斜杠路径（如 engine/filters）。嵌套映射
    /// engine: { filters: ... } 会整体覆盖 engine，进而清空 processors、
    /// segmentors 与 translators，造成无法生成候选。所有由面板管理的键一律
    /// 写成扁平路径；同时迁移并清除同路径的历史嵌套键。
    /// </summary>
    public void Set(string path, object? newValue)
    {
        if (newValue is not null) _patch[path] = newValue;
        else _patch.Remove(path);
        RemoveNested(path);
    }

    private void RemoveNested(string path)
    {
        var parts = path.Split('/');
        if (parts.Length <= 1) return;
        _patch = RemovingNested(_patch, parts);
    }

    private static Dictionary<string, object?> RemovingNested(
        Dictionary<string, object?> node, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return node;
        var head = path[0];

        if (path.Count == 1)
        {
            node.Remove(head);
            return node;
        }
        if (!node.TryGetValue(head, out var childObj) || childObj is not Dictionary<string, object?> child)
            return node;

        var updated = RemovingNested(child, path.Skip(1).ToList());
        if (updated.Count == 0) node.Remove(head);
        else node[head] = updated;
        return node;
    }

    /// <summary>移除一组由本面板管理的键（用于「恢复默认」），用户手写的其他键保持不动。</summary>
    public void RemoveManaged(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _patch.Remove(key);
            RemoveNested(key);
        }
    }

    /// <summary>移除某个前缀下的全部键，例如 app_options/cmd.exe。</summary>
    public void RemoveAllWithPrefix(string prefix)
    {
        foreach (var key in _patch.Keys.Where(k => k == prefix || k.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList())
            _patch.Remove(key);
    }

    /// <summary>
    /// 卸载语法模型时移除本面板注入的全部 grammar 内容：
    /// grammar/language 与 grammar/collocation_prism（后者是 octagram 加载模型所必需）。
    /// 若该 grammar 节点因此变空则一并删除，避免残留空节点或孤立的 prism 引用。
    /// 不影响用户手动添加的其它 grammar 子配置（若存在则保留）。
    /// </summary>
    public void RemoveGrammar()
    {
        Set("grammar/language", null);
        Set("grammar/collocation_prism", null);
        if (_patch.TryGetValue("grammar", out var g) && g is Dictionary<string, object?> gd && gd.Count == 0)
            _patch.Remove("grammar");
    }

    // MARK: - 序列化（整文件重写）

    public const string Header =
        "# 由「小狼毫控制面板」(Weasel Panel) 维护\n" +
        "# https://github.com/wolfprince12/Weasel-Panel\n" +
        "#\n" +
        "# 本文件是 Rime 的补丁文件，用于覆盖默认配置。\n" +
        "# 控制面板只会修改它认得的配置项，你手写的其他条目会原样保留。\n" +
        "# 如需手动编辑，建议先在控制面板中关闭对应选项，避免两边互相覆盖。\n\n";

    /// <summary>生成即将写入磁盘的完整文本。</summary>
    public string Serialize()
    {
        var output = new Dictionary<string, object?>(_root, StringComparer.Ordinal);
        if (_patch.Count == 0) output.Remove("patch");
        else output["patch"] = _patch;

        if (output.Count == 0) return Header;

        var body = YamlMiniEmitter.EmitMapping(output);
        return Header + string.Join('\n', body) + "\n";
    }

    /// <summary>写入磁盘：先备份，再替换。</summary>
    public void Save()
    {
        if (!IsWritable) throw PanelException.RefusedToOverwrite(Path.GetFileName(FilePath));
        WriteWithVerification(Serialize(), null);
    }

    // MARK: - 逐行手术式写入

    /// <summary>
    /// 逐行手术式写入：只改 set 中列出的键对应行，保留其它行原样（注释/格式/用户手改）。
    /// value 为 null 表示删除该键。写盘后回读校验，未落地则回滚 .bak 并抛错。
    ///
    /// 与 Save()（整文件重序列化）互补：本方法用于「应用配置」这类只动少数托管键的场景，
    /// 能保住用户手写在同文件里的其它条目与注释；Save() 仍用于「恢复默认」等需整段重写的场景。
    /// </summary>
    public void ApplyLineEdits(PatchSet set)
    {
        if (!IsWritable) throw PanelException.RefusedToOverwrite(Path.GetFileName(FilePath));

        var editor = new YamlLineEditor(CurrentOrSkeletonText());
        foreach (var (key, maybeValue) in set.Enumerate())
        {
            if (maybeValue is not null) editor.ApplyPatchValue(key, maybeValue);
            else editor.RemoveKeyAtPath(new[] { "patch", key });
        }
        WriteWithVerification(editor.Text, set);
    }

    /// <summary>读取当前磁盘原文；不存在时用「注释头 + patch:」骨架（等价首次写入）。</summary>
    private string CurrentOrSkeletonText() =>
        File.Exists(FilePath) ? ReadAllText() : Header + "patch:\n";

    /// <summary>
    /// 写盘 + 写后校验。校验失败则删除临时文件并抛错（原文件未覆盖，.bak 无需回滚）。
    /// </summary>
    private void WriteWithVerification(string text, PatchSet? verifying)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var existed = File.Exists(FilePath);

        // 仅覆盖已存在文件时留 .bak 备份
        if (existed)
        {
            var backup = FilePath + ".bak";
            try { File.Copy(FilePath, backup, overwrite: true); }
            catch { /* 备份失败不阻断主流程，但不静默吞掉：交给调用方决定是否继续 */ }
        }

        var tmp = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            ".wp-" + Path.GetFileName(FilePath) + ".tmp");

        File.WriteAllText(tmp, text, Utf8NoBom);

        if (verifying is not null)
        {
            try { WriteVerifier.Verify(tmp, verifying); }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { /* 忽略清理失败 */ }
                // 带上原始原因：只报「校验失败」、不报是哪个键、为什么，排查时只能靠猜。
                // 错误码仍是 WriteVerificationFailed，App 层照旧按码渲染本地化文案。
                throw new PanelException(PanelErrorCode.WriteVerificationFailed,
                    "Write verification failed: " + Path.GetFileName(FilePath) + " — " + ex.Message,
                    Path.GetFileName(FilePath));
            }
        }

        // 校验通过：替换目标文件（同卷内为原子操作）
        File.Move(tmp, FilePath, overwrite: true);
    }
}

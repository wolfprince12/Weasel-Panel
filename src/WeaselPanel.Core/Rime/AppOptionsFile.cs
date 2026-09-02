//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  按程序设定输入法默认状态。对应 weasel.custom.yaml 的 patch/app_options 段。
//
//  ── 上游行为（2026-09-02 逐行核实 rime/weasel 源码，不是猜的）────────────────
//   · 键是什么：WeaselIPC/WeaselClientImpl.cpp 的 _InitializeClientInfo() 取
//     GetModuleFileName(NULL) 的文件名部分（如 "cmd.exe"），经
//     session.client_app=<name> 发给服务端；RimeWithWeasel.cpp 的
//     _ReadClientInfo() 再 to_lower() 后存入 map。
//     ⇒ 配置里的键就是 **exe 文件名**，且比较时大小写不敏感。
//   · 值是什么：_LoadAppOptions() 遍历 app_options/<exe> 下**所有**键，
//     逐个当 bool 读出来再 rime_api->set_option()。所以写任何 option 名都不会报错，
//     但只有真正被 librime / Weasel 消费的那几个才有效果。
//   · 哪三个真正生效：
//       ascii_mode     —— librime 内建选项，官方 weasel.yaml 出厂就给
//                         cmd.exe / conhost.exe 配了 ascii_mode: true。
//       vim_mode       —— RimeWithWeasel.cpp:283，仅当按 Esc / Ctrl+C / Ctrl+[
//                         且当前不在 ascii 模式时，自动切回英文（「回命令模式」）。
//       inline_preedit —— RimeWithWeasel.cpp:645 有专门分支：不仅 set_option，
//                         还会同步改该 session 的 style.inline_preedit。
//   · 不能照抄 macOS 鼠须管的 no_inline / inline：这两个键在 rime/weasel
//     整个仓库里根本不存在，写了不生效 —— 界面上多两个开关只会让人以为生效了。
//
//  ── 为什么这里要单独做一层大小写折叠 ──────────────────────────────────
//  上游的容器是 std::map<std::string, AppOptions, CaseInsensitiveCompare>，
//  即 cmd.exe 与 CMD.EXE 是同一个键，后插入的覆盖先插入的。
//  而 RimeConfigView 的合并是大小写敏感的 —— 这对 Rime 的其它键是对的
//  （style/font_point 与 style/Font_Point 就是两个不同的键），
//  所以这一层折叠只能在 app_options 这里单独做，不能改到公共合并逻辑里去。
//  不折叠的后果很具体：base 写 cmd.exe、补丁写 CMD.EXE 时，
//  界面会显示成两行，而小狼毫实际只认一行。
//
//  ── 落盘规则：与出厂相同的值一律不写 ──────────────────────────────────
//  weasel.yaml 出厂就带 cmd.exe / conhost.exe 的 ascii_mode: true。
//  用户把开关拨回 true 时，正确做法是**删掉补丁键**让它回落到出厂值，
//  而不是写一条 ascii_mode: true —— 后者会让补丁文件越攒越乱，
//  而且以后升级 weasel.yaml 改了出厂值时，用户的冗余补丁还会把它盖住。
//  唯一例外是 inline_preedit：它是三态（null = 不干预 / true / false），
//  显式 false 必须落盘，否则会退回全局 style/inline_preedit 的值。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.Core.Rime;

/// <summary>单个程序的选项。</summary>
public sealed class AppOptionEntry
{
    /// <summary>
    /// 写进 YAML 的 exe 键。优先沿用补丁里已有的写法 —— 那一条才是真正压过出厂的键；
    /// 补丁里还没有时沿用 weasel.yaml 的原始大小写，最后才退回小写。
    /// </summary>
    public required string ExeKey { get; init; }

    /// <summary>进入该程序时默认英文。</summary>
    public bool AsciiMode { get; set; }

    /// <summary>Vim 模式：Esc / Ctrl+C / Ctrl+[ 时自动切回英文。</summary>
    public bool VimMode { get; set; }

    /// <summary>null = 不干预，跟随全局 style/inline_preedit；否则为该程序单独指定。</summary>
    public bool? InlinePreedit { get; set; }

    /// <summary>出厂 weasel.yaml 里就有这一条（如 cmd.exe / conhost.exe）。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>本面板是否在补丁里为它写过托管键（决定「重置」按钮是否可用）。</summary>
    public bool IsCustomized { get; set; }
}

public sealed class AppOptionsFile
{
    public const string Node = "app_options";

    /// <summary>本面板管理的三个选项。用户在同一节点下手写的其它键一律原样保留。</summary>
    public static readonly string[] ManagedOptions = { "ascii_mode", "vim_mode", "inline_preedit" };

    /// <summary>新增条目时的默认开关：默认英文是绝大多数人加条目的理由。</summary>
    public const bool DefaultAsciiModeWhenAdded = true;

    private readonly RimeConfigView _base;      // weasel.yaml（出厂态）
    private readonly CustomYamlFile _custom;    // weasel.custom.yaml

    public AppOptionsFile(RimeConfigView baseView, CustomYamlFile custom)
    {
        _base = baseView;
        _custom = custom;
    }

    public CustomYamlFile Custom => _custom;

    /// <summary>从真实环境载入。base 只取第一个配置源 —— Rime 的规则是「用户目录
    /// 同名文件整份覆盖共享目录」，不是逐键合并。</summary>
    public static AppOptionsFile Load(WeaselEnvironment environment)
    {
        var sources = environment.ConfigSources("weasel.yaml");
        var baseView = sources.Length > 0
            ? RimeConfigView.FromYaml(File.ReadAllText(sources[0]))
            : RimeConfigView.Empty;

        var custom = new CustomYamlFile(
            Path.Combine(environment.UserDirectory, "weasel.custom.yaml"));
        return new AppOptionsFile(baseView, custom);
    }

    // ── 读取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 合并后的生效条目。出厂条目按 weasel.yaml 里的原始顺序排在最前，
    /// 用户后加的按 exe 名字典序排在其后 —— 顺序稳定，切语言重刷时列表不会跳。
    /// </summary>
    public List<AppOptionEntry> Entries()
    {
        var baseFold = Fold(AsMap(_base.Lookup(Node)), out var baseOrder);
        var patchFold = Fold(AsMap(PatchView().Lookup(Node)), out _);

        // 出厂在前、补丁在后：后写的赢，与上游 map 的插入次序一致
        var keys = new List<string>(baseOrder);
        foreach (var k in patchFold.Keys)
            if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);

        var rows = new List<AppOptionEntry>();
        foreach (var lower in keys)
        {
            var opts = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (baseFold.TryGetValue(lower, out var b))
                foreach (var kv in b.Opts) opts[kv.Key] = kv.Value;
            if (patchFold.TryGetValue(lower, out var p))
                foreach (var kv in p.Opts) opts[kv.Key] = kv.Value;

            // 写回优先用补丁里的键（那才是真正生效的一条），其次是出厂键，最后小写兜底
            var writeKey = patchFold.TryGetValue(lower, out var pk) ? pk.Key
                : baseFold.TryGetValue(lower, out var bk) ? bk.Key
                : lower;

            var prefix = Node + "/" + writeKey + "/";
            rows.Add(new AppOptionEntry
            {
                ExeKey = writeKey,
                AsciiMode = BoolOf(opts, "ascii_mode") ?? false,
                VimMode = BoolOf(opts, "vim_mode") ?? false,
                InlinePreedit = BoolOf(opts, "inline_preedit"),
                IsBuiltIn = baseFold.ContainsKey(lower),
                IsCustomized = ManagedOptions.Any(o => _custom.Patch.ContainsKey(prefix + o)),
            });
        }

        // 出厂条目保持原序在前，其余按名字排序
        var builtInIndex = baseOrder
            .Select((k, i) => (Key: k, Index: i))
            .ToDictionary(x => x.Key, x => x.Index, StringComparer.OrdinalIgnoreCase);

        return rows
            .OrderBy(r =>
            {
                var lower = r.ExeKey.ToLowerInvariant();
                return builtInIndex.TryGetValue(lower, out var idx) ? idx : int.MaxValue;
            })
            .ThenBy(r => r.ExeKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>补丁自身的树（不含出厂内容），用于与出厂分开折叠。</summary>
    private RimeConfigView PatchView() =>
        RimeConfigView.MergePatch(RimeConfigView.Empty, _custom.Patch);

    /// <summary>
    /// 把 app_options 映射按 exe 名小写归一：同一程序的大小写变体合成一条，
    /// 重复出现时后写的覆盖先写的。返回「小写名 → (原始键, 选项映射)」，
    /// <paramref name="order"/> 为首次出现的顺序（出厂顺序）。
    /// </summary>
    private static Dictionary<string, (string Key, Dictionary<string, object?> Opts)> Fold(
        Dictionary<string, object?> map, out List<string> order)
    {
        var fold = new Dictionary<string, (string, Dictionary<string, object?>)>(
            StringComparer.OrdinalIgnoreCase);
        order = new List<string>();

        foreach (var (key, raw) in map)
        {
            var lower = key.ToLowerInvariant();
            var opts = AsMap(raw);
            if (fold.TryGetValue(lower, out var prev))
            {
                var merged = new Dictionary<string, object?>(prev.Item2, StringComparer.Ordinal);
                foreach (var kv in opts) merged[kv.Key] = kv.Value;
                fold[lower] = (key, merged);   // 后出现的键赢
            }
            else
            {
                fold[lower] = (key, new Dictionary<string, object?>(opts, StringComparer.Ordinal));
                order.Add(lower);
            }
        }
        return fold;
    }

    /// <summary>
    /// 从一个「选项名 → 值」映射里取布尔。返回 null 表示键不存在。
    /// 借道 RimeConfigView 解析，扁平键（"x/ascii_mode"）与真假写法（true/yes/1）
    /// 都复用已经过验证的那套逻辑，不在这里另写一份。
    /// </summary>
    private static bool? BoolOf(Dictionary<string, object?> opts, string option)
    {
        if (opts.Count == 0) return null;
        var view = RimeConfigView.FromTree(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = opts });
        return view.TryGetBool("x/" + option, out var b) ? b : null;
    }

    // ── 写入 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 按给定的条目集合写回补丁。列表里没有、但补丁里存在托管键的 exe，
    /// 会被清掉本面板写过的键（用户在同一 exe 下手写的其它键仍然保留）。
    /// </summary>
    public void Save(IEnumerable<AppOptionEntry> entries)
    {
        var list = entries.ToList();
        var set = new PatchSet();
        var baseFold = Fold(AsMap(_base.Lookup(Node)), out _);

        foreach (var e in list)
        {
            var lower = e.ExeKey.ToLowerInvariant();
            var baseOpts = baseFold.TryGetValue(lower, out var b) ? b.Item2 : null;

            foreach (var opt in ManagedOptions)
            {
                var key = Node + "/" + e.ExeKey + "/" + opt;
                bool? desired = opt switch
                {
                    "ascii_mode" => e.AsciiMode,
                    "vim_mode" => e.VimMode,
                    "inline_preedit" => e.InlinePreedit,
                    _ => null,
                };

                if (desired is null)
                {
                    set.Remove(key);   // 三态的「不干预」
                    continue;
                }

                // 三态不能套用「与出厂相同就删」：base 里没有该键意味着
                // 「跟随全局」，而用户要的显式 false 与它并不相同。
                if (opt == "inline_preedit")
                {
                    set.Set(key, PatchValue.Of(desired.Value));
                    continue;
                }

                var baseValue = baseOpts is null ? false : BoolOf(baseOpts, opt) ?? false;
                if (desired.Value == baseValue) set.Remove(key);
                else set.Set(key, PatchValue.Of(desired.Value));
            }
        }

        var kept = new HashSet<string>(
            list.Select(e => e.ExeKey.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        foreach (var exe in PatchedExeKeys())
        {
            if (kept.Contains(exe)) continue;
            foreach (var opt in ManagedOptions)
                set.Remove(Node + "/" + exe + "/" + opt);
        }

        _custom.ApplyLineEdits(set);
    }

    /// <summary>补丁里出现过托管键的 exe 名（原始大小写）。</summary>
    private IEnumerable<string> PatchedExeKeys()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in _custom.Patch.Keys)
        {
            var parts = key.Split('/');
            if (parts.Length < 3) continue;
            if (!string.Equals(parts[0], Node, StringComparison.Ordinal)) continue;
            if (!ManagedOptions.Contains(parts[^1], StringComparer.Ordinal)) continue;
            if (seen.Add(parts[1])) yield return parts[1];
        }
    }

    /// <summary>本面板管理的全部键（供「恢复默认」一次性清空）。</summary>
    public IEnumerable<string> ManagedKeys()
    {
        foreach (var exe in PatchedExeKeys())
            foreach (var opt in ManagedOptions)
                yield return Node + "/" + exe + "/" + opt;
    }

    /// <summary>清空本面板写过的全部 app_options 键。</summary>
    public void ClearManaged()
    {
        var set = new PatchSet();
        foreach (var key in ManagedKeys()) set.Remove(key);
        _custom.ApplyLineEdits(set);
    }

    // ── 常用预设 ────────────────────────────────────────────────────────

    /// <summary>
    /// 预设：只列「进去就想默认英文」的终端与编辑器。
    /// <c>LabelKey</c> 是语言包键而不是写死的中文 —— Core 是跨平台纯逻辑层，
    /// 里面不该出现任何自然语言文案；显示名由 App 层查 L10n 得到。
    /// 出厂已带的 cmd.exe / conhost.exe 也在其中，方便用户一键恢复。
    /// </summary>
    public static readonly IReadOnlyList<(string LabelKey, string ExeKey)> Presets = new[]
    {
        ("AppOptions.Preset.Cmd", "cmd.exe"),
        ("AppOptions.Preset.Conhost", "conhost.exe"),
        ("AppOptions.Preset.Powershell", "powershell.exe"),
        ("AppOptions.Preset.Pwsh", "pwsh.exe"),
        ("AppOptions.Preset.WindowsTerminal", "WindowsTerminal.exe"),
        ("AppOptions.Preset.Wsl", "wsl.exe"),
        ("AppOptions.Preset.Bash", "bash.exe"),
        ("AppOptions.Preset.Vscode", "Code.exe"),
        ("AppOptions.Preset.VisualStudio", "devenv.exe"),
        ("AppOptions.Preset.Idea", "idea64.exe"),
        ("AppOptions.Preset.Pycharm", "pycharm64.exe"),
        ("AppOptions.Preset.Vim", "vim.exe"),
    };

    private static Dictionary<string, object?> AsMap(object? value) =>
        value as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}

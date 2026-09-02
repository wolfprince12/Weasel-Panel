//
//  RimeIceConfig.cs
//  WeaselPanel.Core
//
//  雾凇拼音（rime-ice）专属配置的唯一数据源。
//  移植自鼠须管控制面板的 RimeIceConfigStore.swift。
//
//  设计铁律（与 macOS 版一致）：
//  1. 本类独占 rime_ice.custom.yaml。
//  2. switcher/save_options 是**全局**名单，五笔、仓颉等其他方案的开关也在里面，
//     只能增删雾凇自己那 6 个名字 —— 整体覆盖会把别的方案的记忆项静默删掉。
//  3. switches 是列表不是映射：「读出厂模板 → 改托管项 reset → 整段写回」。
//  4. reset（固定默认）与 save_options（开关记忆）互斥：设固定默认时把该项摘出名单。
//  5. engine/translators、engine/filters、schema/dependencies、speller/algebra 是列表，
//     采用「列表托管合并」：只增删本类认得的条目，用户自己加的原样保留；
//     合并结果与出厂模板一致时删键回落出厂。
//  6. ⚠️ 绝不动 engine/processors。补丁里一旦出现 engine/processors: {}（哪怕是清键
//     产生的空映射），Rime 会把内置默认处理器整体清空 → 候选框消失、中文报废。
//
//  Windows 侧差异（务必知悉）：
//  · macOS 版经共享的 SettingsStore 统一落盘，本版没有共享 store。因此「全拼↔双拼
//    切换」不在这里提供 —— 它要写 default.custom.yaml 的 schema_list，而那把钥匙
//    归「输入方案」页。本页只读 schema_list 判断当前拼音方案，绝不写它。
//    （同文件不同键不冲突：ApplyLineEdits 是逐键手术式的。）
//  · switcher/save_options 目前无人托管，由本页写入。
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Rime;

/// <summary>
/// 三态开关模式：
/// Remember = 进 switcher/save_options，方案切换后记住上次状态（rime-ice 出厂行为）
/// On       = 固定开启，写 reset: 1，并从 save_options 摘除
/// Off      = 固定关闭，写 reset: 0，并从 save_options 摘除
/// </summary>
public enum SwitchDefaultMode
{
    Remember,
    On,
    Off,
}

/// <summary>模糊音规则分组。</summary>
public enum FuzzyRuleGroup
{
    Initials,
    Finals,
    Syllables,
}

/// <summary>纠错候选的注入位置（相对自然候选）。</summary>
public enum CorrectionInjectionPosition
{
    /// <summary>始终置顶（首位）。</summary>
    Top = 0,

    /// <summary>首条之后（次位，默认）。</summary>
    AfterFirst = 1,
}

/// <summary>出厂模板中的单个 switch（从 rime_ice.schema.yaml 解析，只读）。</summary>
public sealed record RimeIceSwitchTemplate(
    string Name,
    IReadOnlyList<string> States,
    IReadOnlyList<string>? Abbrev,
    int FactoryReset);

/// <summary>界面上的一个 switch 行。</summary>
public sealed record RimeIceSwitchItem(
    string Name,
    IReadOnlyList<string> States,
    IReadOnlyList<string>? Abbrev,
    SwitchDefaultMode Mode);

/// <summary>
/// 一条模糊音规则：<see cref="Rule"/> 是写进 speller/algebra 的原文，
/// <see cref="Label"/> 是界面上的人类可读描述。
/// </summary>
public sealed record FuzzyRule(string Rule, string Label, FuzzyRuleGroup Group);

/// <summary>
/// 一条拼音纠错规则：单向「错误音节 → 正确音节」派生，写进 speller/algebra。
/// 仅处理「错音本身也是合法拼音音节序列」的 typo，由 Rime 内核级 derive 在拼音
/// 编译期展开，零延迟。跨音节 / 含非法音节片段的错打交给 lua_filter 处理。
/// </summary>
public sealed record CorrectionRule(string Rule, string Label);

/// <summary>从 rime_ice.schema.yaml 解析出的出厂模板全集。</summary>
public sealed class RimeIceTemplate
{
    public List<RimeIceSwitchTemplate> Switches { get; } = [];
    public List<string> Translators { get; } = [];
    public List<string> Filters { get; } = [];
    public List<string> Dependencies { get; } = [];
    public List<string> Algebra { get; } = [];
    public string Opencc { get; set; } = "s2t.json";
}

/// <summary>
/// 雾凇拼音（rime-ice）专属配置 + 紫毫纠错模型的挂载状态。
/// </summary>
public sealed class RimeIceConfig
{
    private readonly WeaselEnvironment _environment;
    private CustomYamlFile _icePatch;
    private PatchSet _baseline = new();

    /// <summary>出厂模板（Reload 时从 rime_ice.schema.yaml 读一次，缓存）。</summary>
    public RimeIceTemplate Template { get; private set; } = new();

    /// <summary>
    /// 出厂 default.yaml 的 switcher/save_options 名单（Reload 时读一次，缓存）。
    ///
    /// 判断「开关是否停在出厂默认」必须带上它：rime-ice 出厂把 6 个开关里的 5 个
    /// 写进了 save_options，它们的出厂态是「记忆」而非任何一侧的固定默认。只按
    /// reset 判定会把这 5 项一律当成「被用户改过」，干净安装也会往 rime_ice.custom.yaml
    /// 里整段写 switches（整段替换非追加），上游日后调整 switches 会被这份陈旧快照
    /// 永久覆盖。
    ///
    /// **只在 Reload() 里读一次盘**：CompileIcePatch() 挂在 UI 求值路径上（脏值判断
    /// 每帧都会跑），绝不能在其中做磁盘 I/O。
    /// </summary>
    private IReadOnlyList<string> _factorySaveOptions = [];

    // ── 状态：基础开关 ────────────────────────────────────────────────

    public List<RimeIceSwitchItem> Switches { get; set; } = [];

    // 候选词数（menu/page_size）不由本页管理：唯一入口是「按键与输入」页的全局
    // menu/page_size，rime-ice 永远继承全局。见 CompileIcePatch() 里的恒删键逻辑。

    // ── 状态：词库 ────────────────────────────────────────────────────

    /// <summary>英文输入 melt_eng（translator + dependency 成对，autocap / reduce_english 随之同生同死）。</summary>
    public bool EnableMeltEng { get; set; } = true;

    /// <summary>中英混合词 cn_en。</summary>
    public bool EnableCnEn { get; set; } = true;

    /// <summary>部件拆字（translator + 两个 filter + dependency 四者联动）。</summary>
    public bool EnableRadical { get; set; } = true;

    /// <summary>Emoji 词库 simplifier@emoji。</summary>
    public bool EnableEmojiDict { get; set; } = true;

    // ── 状态：语言与拼音 ──────────────────────────────────────────────

    /// <summary>繁体类型：s2t.json | s2hk.json | s2tw.json | s2twp.json。</summary>
    public string Opencc { get; set; } = "s2t.json";

    /// <summary>
    /// 当前排在 schema_list 首位的拼音类方案（rime_ice = 全拼）。
    /// **只读**：切换方案要写 default.custom.yaml 的 schema_list，那归「输入方案」页。
    /// </summary>
    public string ActivePinyinSchemaId { get; private set; } = "rime_ice";

    /// <summary>
    /// 双拼「编码原样显示」：写 &lt;dp&gt;.custom.yaml 的 translator/preedit_format: []。
    /// </summary>
    public bool ShowRawDoubleCode { get; set; }

    // ── 状态：高级 ────────────────────────────────────────────────────

    /// <summary>6 个可独立开关的 Lua 滤镜，键为 lua 名（如 *corrector）。</summary>
    public Dictionary<string, bool> LuaFilters { get; set; } = [];

    /// <summary>已选中的模糊音规则（值为 speller/algebra 中的规则原文）。</summary>
    public HashSet<string> FuzzySelection { get; set; } = [];

    // ── 状态：紫毫纠错模型 ────────────────────────────────────────────

    /// <summary>是否启用拼音实时纠错。开启时挂上 algebra derive 规则 + lua_filter@*amethyst_corrector。</summary>
    public bool CorrectionEnabled { get; set; }

    /// <summary>纠错候选注入位置：首位 / 次位。</summary>
    public CorrectionInjectionPosition CorrectionInjectionPosition { get; set; } = CorrectionInjectionPosition.AfterFirst;

    /// <summary>最多注入几条纠错候选：1 / 2 / 3（默认 1，只给最接近的）。</summary>
    public int CorrectionCandidateCount { get; set; } = 1;

    /// <summary>注入位置的脏值基线。它只落 correction_position.txt，不进 custom.yaml，
    /// 因此 CompileIcePatch() 感知不到它的变化，必须单独跟踪，否则改它时 IsDirty 恒为
    /// false → 应用按钮不亮 → 配置写不进磁盘。</summary>
    private CorrectionInjectionPosition _baselinePosition = CorrectionInjectionPosition.AfterFirst;

    /// <summary>候选数量的脏值基线，同上。</summary>
    private int _baselineCandidateCount = 1;

    private bool _baselineShowRawDoubleCode;

    private CustomYamlFile? _doublePinyinPatch;

    // ── 托管常量（照抄 rime_ice.schema.yaml，杜绝手写错）──────────────

    /// <summary>本类托管的 rime_ice.custom.yaml 键（「恢复默认」只清理这些）。
    ///
    /// menu/page_size 仍在名单里，但语义已变成「只删不写」：候选数改由「按键与输入」
    /// 页全局托管，这里保留它是为了清掉历史遗留的方案级覆盖（方案级会压过全局）。</summary>
    public static readonly string[] ManagedIceKeys =
    [
        "switches",
        "menu/page_size",
        "traditionalize/opencc_config",
        "engine/translators",
        "engine/filters",
        "schema/dependencies",
        "speller/algebra",
        "grammar",
    ];

    /// <summary>托管的 translators 条目。</summary>
    public static readonly string[] ManagedTranslators =
    [
        "table_translator@melt_eng",
        "table_translator@cn_en",
        "table_translator@radical_lookup",
    ];

    /// <summary>托管的 filters 条目。</summary>
    public static readonly string[] ManagedFilters =
    [
        "lua_filter@*corrector",
        "lua_filter@*autocap_filter",
        "lua_filter@*v_filter",
        "lua_filter@*pin_cand_filter",
        "lua_filter@*long_word_filter",
        "lua_filter@*reduce_english_filter",
        "simplifier@emoji",
        "lua_filter@*search@radical_pinyin",
        "reverse_lookup_filter@radical_reverse_lookup",
        "lua_filter@*amethyst_corrector",
    ];

    /// <summary>托管的 dependencies 条目。</summary>
    public static readonly string[] ManagedDependencies = ["melt_eng", "radical_pinyin"];

    /// <summary>可独立开关的 6 个 Lua 滤镜（键 = lua 名，实际条目为 lua_filter@ + 键）。</summary>
    public static readonly string[] LuaFilterKeys =
    [
        "*corrector",
        "*autocap_filter",
        "*v_filter",
        "*pin_cand_filter",
        "*long_word_filter",
        "*reduce_english_filter",
    ];

    /// <summary>英文开关关闭时必须一并摘除的 Lua 滤镜。</summary>
    public static readonly string[] EnglishBoundLuaFilters = ["*autocap_filter", "*reduce_english_filter"];

    /// <summary>繁体类型可选值（Rime 内置 OpenCC 配置）。</summary>
    public static readonly string[] OpenccOptions = ["s2t.json", "s2hk.json", "s2tw.json", "s2twp.json"];

    /// <summary>未安装雾凇拼音时用于**占位展示**的开关行（置灰不可改，永不落盘）。
    ///
    /// 出厂 rime-ice 把 6 个开关里的 5 个写进了 save_options（即「记忆」），只有
    /// ascii_mode 是固定关 —— 这里照抄该事实，用户装好之后界面不会突然跳变。
    /// States 有意留空：真实文案要从 rime_ice.schema.yaml 读，装之前不该编造。</summary>
    public static readonly RimeIceSwitchItem[] PreviewSwitches =
    [
        new("ascii_mode", [], null, SwitchDefaultMode.Off),
        new("ascii_punct", [], null, SwitchDefaultMode.Remember),
        new("traditionalization", [], null, SwitchDefaultMode.Remember),
        new("emoji", [], null, SwitchDefaultMode.Remember),
        new("full_shape", [], null, SwitchDefaultMode.Remember),
        new("search_single_char", [], null, SwitchDefaultMode.Remember),
    ];

    /// <summary>出厂模板中默认注释掉的模糊音规则表（原文取自 rime_ice.schema.yaml）。</summary>
    public static readonly FuzzyRule[] FuzzyRules =
    [
        // 声母
        new("derive/^([zcs])h/$1/", "zh, ch, sh → z, c, s", FuzzyRuleGroup.Initials),
        new("derive/^([zcs])([^h])/$1h$2/", "z, c, s → zh, ch, sh", FuzzyRuleGroup.Initials),
        new("derive/^l/n/", "l → n", FuzzyRuleGroup.Initials),
        new("derive/^n/l/", "n → l", FuzzyRuleGroup.Initials),
        new("derive/^f/h/", "f → h", FuzzyRuleGroup.Initials),
        new("derive/^h/f/", "h → f", FuzzyRuleGroup.Initials),
        new("derive/^l/r/", "l → r", FuzzyRuleGroup.Initials),
        new("derive/^r/l/", "r → l", FuzzyRuleGroup.Initials),
        new("derive/^g/k/", "g → k", FuzzyRuleGroup.Initials),
        new("derive/^k/g/", "k → g", FuzzyRuleGroup.Initials),
        // 韵母
        new("derive/ang$/an/", "ang → an", FuzzyRuleGroup.Finals),
        new("derive/an$/ang/", "an → ang", FuzzyRuleGroup.Finals),
        new("derive/eng$/en/", "eng → en", FuzzyRuleGroup.Finals),
        new("derive/en$/eng/", "en → eng", FuzzyRuleGroup.Finals),
        new("derive/in$/ing/", "in → ing", FuzzyRuleGroup.Finals),
        new("derive/ing$/in/", "ing → in", FuzzyRuleGroup.Finals),
        new("derive/ian$/iang/", "ian → iang", FuzzyRuleGroup.Finals),
        new("derive/iang$/ian/", "iang → ian", FuzzyRuleGroup.Finals),
        new("derive/uan$/uang/", "uan → uang", FuzzyRuleGroup.Finals),
        new("derive/uang$/uan/", "uang → uan", FuzzyRuleGroup.Finals),
        new("derive/ai$/an/", "ai → an", FuzzyRuleGroup.Finals),
        new("derive/an$/ai/", "an → ai", FuzzyRuleGroup.Finals),
        new("derive/ong$/un/", "ong → un", FuzzyRuleGroup.Finals),
        new("derive/un$/ong/", "un → ong", FuzzyRuleGroup.Finals),
        new("derive/ong$/on/", "ong → on", FuzzyRuleGroup.Finals),
        new("derive/iong$/un/", "iong → un", FuzzyRuleGroup.Finals),
        new("derive/un$/iong/", "un → iong", FuzzyRuleGroup.Finals),
        new("derive/ong$/eng/", "ong → eng", FuzzyRuleGroup.Finals),
        new("derive/eng$/ong/", "eng → ong", FuzzyRuleGroup.Finals),
        // 音节
        new("derive/^fei$/hui/", "fei → hui", FuzzyRuleGroup.Syllables),
        new("derive/^hui$/fei/", "hui → fei", FuzzyRuleGroup.Syllables),
        new("derive/^hu$/fu/", "hu → fu", FuzzyRuleGroup.Syllables),
        new("derive/^fu$/hu/", "fu → hu", FuzzyRuleGroup.Syllables),
        new("derive/^wang$/huang/", "wang → huang", FuzzyRuleGroup.Syllables),
        new("derive/^huang$/wang/", "huang → wang", FuzzyRuleGroup.Syllables),
    ];

    /// <summary>模糊音规则原文集合，用于从用户现有 algebra 中识别并剥离。</summary>
    public static readonly HashSet<string> FuzzyRuleSet =
        new(FuzzyRules.Select(r => r.Rule), StringComparer.Ordinal);

    /// <summary>拼音纠错规则表（单向 derive，写进 speller/algebra）。
    /// 仅含「错音本身也是合法拼音音节序列」的 typo；跨音节 / 非法片段交由 lua 处理。
    /// 开启即全量生效，不再分强度档。</summary>
    public static readonly CorrectionRule[] CorrectionRules =
    [
        // 音近·基础
        new("derive/^n/l/", "n → l"),
        new("derive/^l/n/", "l → n"),
        new("derive/^([zcs])h/$1/", "zh/ch/sh → z/c/s"),
        new("derive/^([zcs])([^h])/$1h$2/", "z/c/s → zh/ch/sh"),
        new("derive/an$/ang/", "an → ang"),
        new("derive/ang$/an/", "ang → an"),
        new("derive/en$/eng/", "en → eng"),
        new("derive/eng$/en/", "eng → en"),
        new("derive/in$/ing/", "in → ing"),
        new("derive/ing$/in/", "ing → in"),
        // 高频键位错打·基础（两侧均合法音节）
        new("derive/^mi$/ni/", "mi → ni"),
        new("derive/^ni$/mi/", "ni → mi"),
        new("derive/^gao$/hao/", "gao → hao"),
        new("derive/^hao$/gao/", "hao → gao"),
        new("derive/^ra$/ta/", "ra → ta"),
        new("derive/^ta$/ra/", "ta → ra"),
        // 音近·标准
        new("derive/^f/h/", "f → h"),
        new("derive/^h/f/", "h → f"),
        new("derive/^r/l/", "r → l"),
        new("derive/^l/r/", "l → r"),
        new("derive/^g/k/", "g → k"),
        new("derive/^k/g/", "k → g"),
        new("derive/ian$/iang/", "ian → iang"),
        new("derive/iang$/ian/", "iang → ian"),
        new("derive/uan$/uang/", "uan → uang"),
        new("derive/uang$/uan/", "uang → uan"),
        new("derive/iong$/un/", "iong → un"),
        new("derive/un$/iong/", "un → iong"),
        new("derive/ong$/un/", "ong → un"),
        new("derive/un$/ong/", "un → ong"),
        new("derive/ong$/on/", "ong → on"),
        new("derive/ong$/eng/", "ong → eng"),
        new("derive/eng$/ong/", "eng → ong"),
        new("derive/^fei$/hui/", "fei → hui"),
        new("derive/^hui$/fei/", "hui → fei"),
        new("derive/^hu$/fu/", "hu → fu"),
        new("derive/^fu$/hu/", "fu → hu"),
        new("derive/^wang$/huang/", "wang → huang"),
        new("derive/^huang$/wang/", "huang → wang"),
        // 键位错打·标准（合法音节互转）
        new("derive/^xi$/ci/", "xi → ci"),
        new("derive/^ci$/xi/", "ci → xi"),
        new("derive/^fa$/da/", "fa → da"),
        new("derive/^da$/fa/", "da → fa"),
        new("derive/^li$/ni/", "li → ni"),
        new("derive/^ni$/li/", "ni → li"),
        new("derive/^ou$/uo/", "ou → uo"),
        new("derive/^ie$/ei/", "ie → ei"),
        new("derive/^ei$/ie/", "ei → ie"),
        new("derive/^sa$/da/", "sa → da"),
        new("derive/^da$/sa/", "da → sa"),
    ];

    /// <summary>纠错规则原文集合，用于从 algebra 中识别并剥离（独立于模糊音规则集）。</summary>
    public static readonly HashSet<string> CorrectionRuleSet =
        new(CorrectionRules.Select(r => r.Rule), StringComparer.Ordinal);

    // ── 生命周期 ──────────────────────────────────────────────────────

    public RimeIceConfig(WeaselEnvironment environment)
    {
        _environment = environment;
        _icePatch = new CustomYamlFile(IceCustomPath);
        Reload();
    }

    /// <summary>rime_ice.custom.yaml 的路径。</summary>
    public string IceCustomPath => Path.Combine(_environment.UserDirectory, "rime_ice.custom.yaml");

    /// <summary>default.custom.yaml 的路径（只有 switcher/save_options 这个键归本类写）。</summary>
    public string DefaultCustomPath => Path.Combine(_environment.UserDirectory, "default.custom.yaml");

    /// <summary>雾凇拼音方案（rime_ice.schema.yaml）已安装才启用配置区，否则整段置灰。</summary>
    public bool IsInstalled => Template.Switches.Count > 0;

    /// <summary>当前是否为双拼方案（全拼 rime_ice 时「编码原样显示」不适用）。</summary>
    public bool IsDoublePinyinActive =>
        !string.IsNullOrEmpty(ActivePinyinSchemaId) && ActivePinyinSchemaId != "rime_ice";

    public bool CanWrite => _icePatch.IsWritable;

    /// <summary>紫毫纠错模型的稳定名称（写入 correction_position.txt 供 lua 读取）。</summary>
    public static string PositionName(CorrectionInjectionPosition p) => p switch
    {
        CorrectionInjectionPosition.Top => "top",
        _ => "afterFirst",
    };

    /// <summary>拼音类方案（全拼 rime_ice + 各家双拼）。</summary>
    public static bool IsPinyinFamily(string? id) =>
        !string.IsNullOrEmpty(id) && (id == "rime_ice" || id.StartsWith("double_pinyin", StringComparison.Ordinal));

    // ── 读盘 ──────────────────────────────────────────────────────────

    public void Reload()
    {
        Template = ParseTemplate(_environment);
        _factorySaveOptions = FactorySaveOptions();

        ActivePinyinSchemaId = DetectActivePinyinSchema();

        _icePatch = new CustomYamlFile(IceCustomPath);
        _icePatch.Load();

        LoadDoublePinyinPatch();

        if (!IsInstalled)
        {
            // 未安装时没有出厂模板可读，但界面仍要把 6 个开关行**展示出来**（置灰不可改），
            // 让用户先看清面板提供哪些能力。这批占位项**永远不会落盘**：
            // CompileIcePatch() 开头有 `if (!IsInstalled) return`，没有任何路径能写出去。
            Switches = [.. PreviewSwitches];
            Opencc = "s2t.json";
            EnableMeltEng = true;
            EnableCnEn = true;
            EnableRadical = true;
            EnableEmojiDict = true;
            LuaFilters = LuaFilterKeys.ToDictionary(k => k, _ => true, StringComparer.Ordinal);
            FuzzySelection = [];
            CorrectionEnabled = false;
            _baseline = new PatchSet();
            return;
        }

        // 当前 rime_ice.custom.yaml 里已重写的 switch reset 值
        var currentReset = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_icePatch.ValueForPath("switches") is List<object?> switchNodes)
        {
            foreach (var node in switchNodes)
            {
                if (node is not Dictionary<string, object?> map) continue;
                if (map.TryGetValue("name", out var n) && n is string name)
                    currentReset[name] = map.TryGetValue("reset", out var r) && r is int v ? v : 0;
            }
        }

        var saved = new HashSet<string>(CurrentSaveOptions(), StringComparer.Ordinal);

        Switches = Template.Switches.Select(t =>
        {
            SwitchDefaultMode mode;
            if (saved.Contains(t.Name)) mode = SwitchDefaultMode.Remember;
            else if (currentReset.TryGetValue(t.Name, out var r)) mode = r == 1 ? SwitchDefaultMode.On : SwitchDefaultMode.Off;
            else mode = t.FactoryReset == 1 ? SwitchDefaultMode.On : SwitchDefaultMode.Off;
            return new RimeIceSwitchItem(t.Name, t.States, t.Abbrev, mode);
        }).ToList();

        Opencc = _icePatch.StringForPath("traditionalize/opencc_config") ?? Template.Opencc;

        // 列表型托管项：以「用户现状」为准，缺省回落出厂模板
        var translators = CurrentList("engine/translators", Template.Translators);
        var filters = CurrentList("engine/filters", Template.Filters);
        var algebra = CurrentList("speller/algebra", Template.Algebra);

        EnableMeltEng = translators.Contains("table_translator@melt_eng");
        EnableCnEn = translators.Contains("table_translator@cn_en");
        EnableRadical = translators.Contains("table_translator@radical_lookup");
        EnableEmojiDict = filters.Contains("simplifier@emoji");

        var lua = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in LuaFilterKeys)
        {
            if (!EnableMeltEng && EnglishBoundLuaFilters.Contains(key))
                // 英文关闭时这两项被强制摘除，界面记忆为「开」，重新开启英文后随之恢复
                lua[key] = true;
            else
                lua[key] = filters.Contains("lua_filter@" + key);
        }

        LuaFilters = lua;
        FuzzySelection = new HashSet<string>(algebra.Where(FuzzyRuleSet.Contains), StringComparer.Ordinal);

        // 纠错挂载态以磁盘实际为准：engine/filters 是否含 lua_filter@*amethyst_corrector。
        // 机制 B（lua_filter）是纠错的主载体，机制 A（derive）仅为其辅助；以过滤器为准
        // 最可靠，否则面板开关会与真实运行态脱钩（手动硬挂载 lua_filter 时面板误显「关闭」）。
        CorrectionEnabled = filters.Contains("lua_filter@*amethyst_corrector");

        // 注入位置 / 候选数量：以实际部署到用户目录的文件为准，保证 UI 与运行时一致。
        CorrectionInjectionPosition = ReadPositionFile();
        CorrectionCandidateCount = ReadCountFile();

        _baseline = CompileIcePatch();
        _baselinePosition = CorrectionInjectionPosition;
        _baselineCandidateCount = CorrectionCandidateCount;
        _baselineShowRawDoubleCode = ShowRawDoubleCode;
    }

    private CorrectionInjectionPosition ReadPositionFile()
    {
        var path = Path.Combine(_environment.UserDirectory, "correction_position.txt");
        if (!File.Exists(path)) return CorrectionInjectionPosition.AfterFirst;
        var text = File.ReadAllText(path).Trim();
        return text == PositionName(CorrectionInjectionPosition.Top)
            ? CorrectionInjectionPosition.Top
            : CorrectionInjectionPosition.AfterFirst;
    }

    private int ReadCountFile()
    {
        var path = Path.Combine(_environment.UserDirectory, "correction_count.txt");
        if (!File.Exists(path)) return 1;
        var text = File.ReadAllText(path).Trim();
        return int.TryParse(text, out var n) && n is >= 1 and <= 3 ? n : 1;
    }

    /// <summary>
    /// 从 rime_ice.schema.yaml 解析出厂模板。
    ///
    /// **只认非 build/ 的源文件**，取不到就返回空模板（面板随之整段置灰）。
    /// 绝不回退去读 build/ 里的编译产物 —— 那份已经合并过我们自己打的补丁，拿它当
    /// 「出厂模板」会形成反馈环：用户勾选的模糊音会被认成出厂自带，下次编译时因
    /// 「与出厂一致」而删键，规则就此静默消失。
    /// </summary>
    public static RimeIceTemplate ParseTemplate(WeaselEnvironment environment)
    {
        var result = new RimeIceTemplate();
        var path = environment.ConfigSources("rime_ice.schema.yaml")
            .FirstOrDefault(p => !p.Replace('\\', '/').Split('/').Contains("build"));
        if (path is null || !File.Exists(path)) return result;

        var parsed = YamlLoader.Load(File.ReadAllText(path));
        if (!parsed.Success || parsed.Root is null) return result;

        var root = parsed.Root;

        if (root.TryGetValue("switches", out var sw) && sw is List<object?> switchNodes)
        {
            foreach (var node in switchNodes)
            {
                if (node is not Dictionary<string, object?> map) continue;
                if (!map.TryGetValue("name", out var n) || n is not string name) continue;
                var states = StringList(map.GetValueOrDefault("states"));
                var abbrev = map.ContainsKey("abbrev") ? StringList(map["abbrev"]) : null;
                var reset = map.TryGetValue("reset", out var r) && r is int v ? v : 0;
                result.Switches.Add(new RimeIceSwitchTemplate(name, states, abbrev, reset));
            }
        }

        if (root.TryGetValue("engine", out var engine) && engine is Dictionary<string, object?> engineMap)
        {
            result.Translators.AddRange(StringList(engineMap.GetValueOrDefault("translators")));
            result.Filters.AddRange(StringList(engineMap.GetValueOrDefault("filters")));
        }

        if (root.TryGetValue("schema", out var schema) && schema is Dictionary<string, object?> schemaMap)
            result.Dependencies.AddRange(StringList(schemaMap.GetValueOrDefault("dependencies")));

        if (root.TryGetValue("speller", out var speller) && speller is Dictionary<string, object?> spellerMap)
            result.Algebra.AddRange(StringList(spellerMap.GetValueOrDefault("algebra")));

        if (root.TryGetValue("traditionalize", out var trad) && trad is Dictionary<string, object?> tradMap
            && tradMap.TryGetValue("opencc_config", out var cfg) && cfg is string config)
            result.Opencc = config;

        return result;
    }

    private static List<string> StringList(object? node)
    {
        if (node is not List<object?> list) return [];
        return list.OfType<string>().ToList();
    }

    /// <summary>
    /// 读取当前补丁中的列表。
    ///
    /// 区分「键不存在」与「显式空列表」：键不存在才回落出厂模板；用户手写
    /// `engine/filters: []` 是明确表态（「这条链上只留我允许的东西」），必须尊重，
    /// 不能把整套出厂条目塞回去。
    /// </summary>
    private IReadOnlyList<string> CurrentList(string path, IReadOnlyList<string> fallback)
    {
        if (_icePatch.ValueForPath(path) is not List<object?> list) return fallback;
        return list.OfType<string>().ToList();
    }

    /// <summary>当前 default.custom.yaml 里的 switcher/save_options（键缺失时回落出厂名单）。</summary>
    public IReadOnlyList<string> CurrentSaveOptions()
    {
        var file = new CustomYamlFile(DefaultCustomPath);
        file.Load();
        if (file.ValueForPath("switcher/save_options") is List<object?> list)
            return list.OfType<string>().ToList();
        return _factorySaveOptions;
    }

    /// <summary>从出厂 default.yaml 读取 switcher/save_options。</summary>
    public IReadOnlyList<string> FactorySaveOptions()
    {
        foreach (var path in _environment.ConfigSources("default.yaml"))
        {
            if (!File.Exists(path)) continue;
            var parsed = YamlLoader.Load(File.ReadAllText(path));
            if (!parsed.Success || parsed.Root is null) continue;

            if (parsed.Root.TryGetValue("switcher", out var node)
                && node is Dictionary<string, object?> switcher
                && switcher.TryGetValue("save_options", out var options)
                && options is List<object?> list)
                return list.OfType<string>().ToList();
        }
        return [];
    }

    /// <summary>
    /// 当前启用的拼音类方案。只读 —— 切换方案要动 default.custom.yaml 的 schema_list，
    /// 那归「输入方案」页，本页绝不写它。
    /// </summary>
    private string DetectActivePinyinSchema()
    {
        var ids = SchemaCatalog.Build(_environment.UserDirectory, _environment.SharedDataDirectory)
            .EffectiveActiveIds;
        return ids.FirstOrDefault(IsPinyinFamily) ?? "rime_ice";
    }

    // ── 列表型托管合并 ────────────────────────────────────────────────

    /// <summary>
    /// 列表托管合并算法：
    /// ① 以 current（用户现状）为骨架：剔除被关闭的托管项、去重，其余原样保留；
    /// ② 缺失的已开启托管项，按 template 中的相对位置（前邻优先、后邻兜底）插回。
    ///
    /// 顺序锚点由 template 自身保证（rime-ice 注释要求：pin_cand &gt; emoji &gt;
    /// traditionalize、long_word &gt; emoji）。
    /// </summary>
    public static List<string> MergedList(
        IReadOnlyList<string> template,
        IReadOnlyList<string> current,
        IReadOnlyCollection<string> managed,
        Func<string, bool> isEnabled)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in current)
        {
            if (managed.Contains(item) && !isEnabled(item)) continue;
            if (!seen.Add(item)) continue;
            result.Add(item);
        }

        for (var i = 0; i < template.Count; i++)
        {
            var item = template[i];
            if (!managed.Contains(item) || !isEnabled(item) || !seen.Add(item)) continue;

            var insertAt = result.Count;
            var prevAnchor = template.Take(i).Reverse().FirstOrDefault(result.Contains);
            if (prevAnchor is not null)
            {
                insertAt = result.IndexOf(prevAnchor) + 1;
            }
            else
            {
                var nextAnchor = template.Skip(i + 1).FirstOrDefault(result.Contains);
                if (nextAnchor is not null) insertAt = result.IndexOf(nextAnchor);
            }

            result.Insert(insertAt, item);
        }

        return result;
    }

    private bool IsTranslatorEnabled(string item) => item switch
    {
        "table_translator@melt_eng" => EnableMeltEng,
        "table_translator@cn_en" => EnableCnEn,
        "table_translator@radical_lookup" => EnableRadical,
        _ => true,
    };

    private bool IsFilterEnabled(string item)
    {
        switch (item)
        {
            case "simplifier@emoji":
                return EnableEmojiDict;
            case "lua_filter@*search@radical_pinyin":
            case "reverse_lookup_filter@radical_reverse_lookup":
                return EnableRadical;
            case "lua_filter@*amethyst_corrector":
                return CorrectionEnabled;
        }

        if (!item.StartsWith("lua_filter@", StringComparison.Ordinal)) return true;

        var key = item["lua_filter@".Length..];
        if (!LuaFilterKeys.Contains(key)) return true;
        if (EnglishBoundLuaFilters.Contains(key) && !EnableMeltEng) return false;
        return LuaFilters.TryGetValue(key, out var on) ? on : true;
    }

    private bool IsDependencyEnabled(string item) => item switch
    {
        "melt_eng" => EnableMeltEng,
        "radical_pinyin" => EnableRadical,
        _ => true,
    };

    private List<string> MergedTranslators() => MergedList(
        Template.Translators,
        CurrentList("engine/translators", Template.Translators),
        ManagedTranslators,
        IsTranslatorEnabled);

    private List<string> MergedFilters()
    {
        var result = MergedList(
            Template.Filters,
            CurrentList("engine/filters", Template.Filters),
            ManagedFilters,
            IsFilterEnabled);

        // 紫毫纠错过滤器 lua_filter@*amethyst_corrector 是面板自有条目，不在出厂
        // rime_ice.schema.yaml 的 engine/filters 里；MergedList 只回插「current 或
        // template 中存在」的托管项，因此它永远插不进来。这里在合并结果上显式兜底：
        // 开启纠错时确保它存在，并插到 uniquifier 之前（让重排后的候选也被去重清理）。
        // 关闭时由 IsFilterEnabled 在 MergedList 的 current 循环里判定为禁用而剔除。
        const string amethyst = "lua_filter@*amethyst_corrector";
        if (CorrectionEnabled && !result.Contains(amethyst))
        {
            var uniqIdx = result.IndexOf("uniquifier");
            if (uniqIdx >= 0) result.Insert(uniqIdx, amethyst);
            else result.Add(amethyst);
        }

        return result;
    }

    private List<string> MergedDependencies() => MergedList(
        Template.Dependencies,
        CurrentList("schema/dependencies", Template.Dependencies),
        ManagedDependencies,
        IsDependencyEnabled);

    /// <summary>
    /// 模糊音 + 拼音纠错：把已选规则**前置**到用户现有 algebra 之前；
    /// 出厂常驻规则（erase / abbrev / v-u 转换 / 自动纠错）原样保留。
    ///
    /// ⚠️ 两组规则在**原文层面是重叠的**：纠错的「音近·基础」一档（n↔l、zh/ch/sh↔z/c/s、
    /// an↔ang …）与模糊音的声母 / 韵母高频项本来就是同一批 derive 字符串。这是设计如此
    /// —— 开启纠错等于顺带打开了基础模糊音。
    ///
    /// 后果是这两组规则无法靠「字符串在不在 algebra 里」区分来源：开着纠错时，
    /// 用户取消勾选一个重叠的模糊音项，保存后规则依然被纠错注入，界面上却显示未勾选。
    /// 对策见 <see cref="IsFuzzyRuleForcedByCorrection"/> —— 由界面把它显示为
    /// 「已勾选且不可改」，而不是让用户的操作静默失效。
    /// </summary>
    private List<string> MergedAlgebra()
    {
        var current = CurrentList("speller/algebra", Template.Algebra);
        var baseRules = current.Where(x => !FuzzyRuleSet.Contains(x) && !CorrectionRuleSet.Contains(x));
        var fuzzy = FuzzyRules.Select(r => r.Rule).Where(FuzzySelection.Contains);
        var correction = CorrectionEnabled ? CorrectionRules.Select(r => r.Rule) : [];

        // 去重：重叠规则在 fuzzySel 与 corrSel 里会各出现一次，写两遍没有意义
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in fuzzy.Concat(correction).Concat(baseRules))
            if (seen.Add(rule)) result.Add(rule);

        return result;
    }

    /// <summary>
    /// 该模糊音规则是否被「纠错」强制注入（即它同时是一条纠错规则，且纠错已开启）。
    ///
    /// 界面必须把这类项显示为**已勾选且不可改**：它们在 algebra 里的存在由纠错决定，
    /// 用户取消勾选后保存，规则照样被写回去 —— 界面显示未勾选、磁盘却有，典型的假成功。
    /// 与其让操作静默失效，不如直接说明「纠错已包含它」。
    /// </summary>
    public bool IsFuzzyRuleForcedByCorrection(string rule) =>
        CorrectionEnabled && CorrectionRuleSet.Contains(rule);

    // ── 写：界面 → 补丁 ───────────────────────────────────────────────

    /// <summary>
    /// 界面上的开关是否全部停在出厂默认。
    ///
    /// 出厂默认分两种，缺一不可：
    /// - 名字在出厂 default.yaml 的 switcher/save_options 里 → 出厂态是「记忆」；
    /// - 否则看方案里写的 reset（没写即 0 → 固定关）。
    ///
    /// 漏掉前者会让 5/6 个开关在干净安装下就被判成「已改动」，switches 整段被无谓写入。
    /// </summary>
    private bool SwitchesAreAllFactory =>
        Switches.All(item =>
        {
            var t = Template.Switches.FirstOrDefault(x => x.Name == item.Name);
            return t is not null && item.Mode == FactoryModeFor(t);
        });

    /// <summary>某个出厂开关的出厂模式（save_options 优先于 reset）。</summary>
    private SwitchDefaultMode FactoryModeFor(RimeIceSwitchTemplate t) =>
        _factorySaveOptions.Contains(t.Name)
            ? SwitchDefaultMode.Remember
            : t.FactoryReset == 1 ? SwitchDefaultMode.On : SwitchDefaultMode.Off;

    /// <summary>编译本类要写入 rime_ice.custom.yaml 的补丁集合。
    /// 与出厂一致的项写 null（落回出厂默认），保持补丁文件精简。</summary>
    public PatchSet CompileIcePatch()
    {
        var set = new PatchSet();
        if (!IsInstalled) return set;

        // switches 整段重写：保留 name / states / abbrev，按三态填 reset
        var list = new List<Dictionary<string, object?>>();
        foreach (var s in Switches)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = s.Name,
                ["states"] = s.States.ToList(),
            };
            if (s.Abbrev is not null) dict["abbrev"] = s.Abbrev.ToList();
            switch (s.Mode)
            {
                case SwitchDefaultMode.On:
                    dict["reset"] = 1;
                    break;
                case SwitchDefaultMode.Off:
                    dict["reset"] = 0;
                    break;
                case SwitchDefaultMode.Remember:
                    // 在 save_options 中被记住，reset 被忽略，不写
                    break;
            }

            list.Add(dict);
        }

        set.Set("switches", SwitchesAreAllFactory ? null : PatchValue.MapList(list));

        // 候选词数：**恒删键**。候选数的唯一入口是「按键与输入」页的全局 menu/page_size，
        // rime-ice 永远继承它，本页不再提供方案级覆盖。保留这个键、只写 null 是自愈路径：
        // 旧版本允许在本页拨候选数，那批用户的 rime_ice.custom.yaml 里躺着一个方案级
        // menu/page_size，方案级压过全局 —— 不显式删键的话，去「按键与输入」怎么改都没效果。
        set.Remove("menu/page_size");

        set.Set("traditionalize/opencc_config",
            Opencc == Template.Opencc ? null : PatchValue.Of(Opencc));

        // ⚠️ 严禁写入 engine/processors（含写 null / @after 0 = none）。Rime 的
        // engine/processors 由内置默认提供；一旦补丁里出现 engine/processors: {}（哪怕是
        // 清键产生的空映射），合并时会把内置默认处理器整体清空 → 候选框消失、中文报废。
        // 这里刻意不碰它，让它回退到 Rime 内置默认。

        set.Set("engine/translators",
            SafeListPatch(MergedTranslators(), Template.Translators));
        set.Set("engine/filters",
            SafeListPatch(MergedFilters(), Template.Filters));
        set.Set("schema/dependencies",
            SafeListPatch(MergedDependencies(), Template.Dependencies));

        // speller/algebra 是最致命的键：一旦整体覆盖写成残缺列表（仅面板自有规则），
        // 官方 40+ 条拼写规则全部丢失 → 拼音拼不出词、中文输入崩溃。
        //
        // ⚠️ 落盘判据必须用「当前磁盘实际」(current) 而非「出厂模板」(template)：
        // 纠错 derive 规则是面板**非出厂**的增量 —— 关闭纠错时 merged 退回工厂列表，
        // 若与 template 比较会判成「相等」而跳过写入，导致旧 derive 规则残留在 yaml 里、
        // 开关关不掉、UI 状态与磁盘脱钩。
        var currentAlgebra = CurrentList("speller/algebra", Template.Algebra);
        var algebra = MergedAlgebra();

        if (CorrectionEnabled)
            // 纠错开启时 derive 是「非工厂」增量：一旦因「与 current 相等」走删键，
            // rime_ice.custom.yaml 回退到出厂 algebra（无 derive）→ 纠错被假关闭、
            // UI 与磁盘脱钩。故开启时恒写完整 merged。
            set.Set("speller/algebra", algebra.Count == 0 ? null : PatchValue.StringList(algebra));
        else if (algebra.Count == 0 || algebra.SequenceEqual(currentAlgebra))
            set.Remove("speller/algebra");
        else
            set.Set("speller/algebra", PatchValue.StringList(algebra));

        // grammar：面板不再管理，恒删键（清理历史残留）
        set.Remove("grammar");

        return set;
    }

    /// <summary>
    /// 列表型键的安全写入护栏（根治「面板部署清空输入法」）。
    ///
    /// 面板对 engine/translators / engine/filters / schema/dependencies 三类列表采用
    /// 「整体覆盖」式补丁。若直接写 `key: []` 或仅含面板自有条目的残缺列表，会清空
    /// Rime 内置/出厂列表，导致候选框消失、中文报废。本护栏三重兜底：
    ///   1. merged 为空 → 删键（绝不 `: []` 清空）；
    ///   2. merged 与出厂一致 → 删键（与出厂相同不落盘，避免快照压制上游）；
    ///   3. 否则写完整 merged（= 实际安装全部官方规则 + 面板增量）。
    /// </summary>
    public static PatchValue? SafeListPatch(IReadOnlyList<string> merged, IReadOnlyList<string> template)
    {
        if (merged.Count == 0) return null;
        if (merged.SequenceEqual(template)) return null;
        return PatchValue.StringList(merged);
    }

    /// <summary>
    /// 算出要写进 default.custom.yaml 的 switcher/save_options 值。
    /// 返回 null 表示「与出厂一致 → 删键回落」，不需要写。
    ///
    /// 只增删雾凇自己那 6 个名字 —— 这份名单是全局的，五笔、仓颉等其他方案的开关
    /// 也在里面，整体覆盖会把别的方案的记忆项静默删掉。
    /// </summary>
    public PatchValue? SaveOptionsPatch(IReadOnlyList<string>? currentOnDisk = null)
    {
        if (!IsInstalled) return null;

        var current = currentOnDisk ?? CurrentSaveOptions();
        var remember = Switches.Where(s => s.Mode == SwitchDefaultMode.Remember)
            .Select(s => s.Name).ToList();
        var mine = new HashSet<string>(Template.Switches.Select(t => t.Name), StringComparer.Ordinal);
        var others = current.Where(x => !mine.Contains(x)).ToList();
        var merged = others.Concat(remember).ToList();

        if (merged.Count == 0 || merged.SequenceEqual(_factorySaveOptions)) return null;
        if (merged.SequenceEqual(current)) return null;   // 与磁盘一致，不必写
        return PatchValue.StringList(merged);
    }

    // ── 落盘 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 把编译结果写盘（自带 .bak + unparsable 拒写），并更新基线。
    /// 同时写 default.custom.yaml 的 save_options 与双拼方案的 preedit_format。
    /// </summary>
    /// <param name="correctionAssetRoot">紫毫纠错资源根目录（含 lua/ 与 data/），null 表示不部署。</param>
    public void WritePatch(string? correctionAssetRoot = null)
    {
        WriteDoublePinyinPatch();

        if (IsInstalled && _icePatch.IsWritable)
        {
            if (CorrectionEnabled)
                DeployCorrectionAssets(correctionAssetRoot);
            else
                RemoveCorrectionAssets();

            var set = CompileIcePatch();

            // 干净安装 + 全部托管项都回落出厂 = 一个键都不用写。此时凭空创建一份只有
            // 注释头、没有任何 patch 段的 rime_ice.custom.yaml 纯属垃圾文件。
            //
            // 文件**已存在**时必须照常写 —— 那是删除历史托管键的自愈路径（旧版本写进去
            // 的 switches / menu_page_size 快照要靠这一步清掉），跳过就永远自愈不了。
            var hasValueToWrite = set.Items.Values.Any(v => v is not null);
            if (hasValueToWrite || File.Exists(IceCustomPath))
            {
                _icePatch.ApplyLineEdits(set);
                _icePatch = new CustomYamlFile(IceCustomPath);
                _icePatch.Load();
                _baseline = CompileIcePatch();
            }
            else
            {
                _baseline = set;
            }
        }

        WriteSaveOptions();

        _baselinePosition = CorrectionInjectionPosition;
        _baselineCandidateCount = CorrectionCandidateCount;
        _baselineShowRawDoubleCode = ShowRawDoubleCode;
    }

    private void WriteSaveOptions()
    {
        var value = SaveOptionsPatch();
        var file = new CustomYamlFile(DefaultCustomPath);
        file.Load();
        if (!file.IsWritable) return;

        // 文件不存在且没有要写的值 → 不凭空创建
        if (value is null && !File.Exists(DefaultCustomPath)) return;

        var set = new PatchSet();
        if (value is null) set.Remove("switcher/save_options");
        else set.Set("switcher/save_options", value);
        file.ApplyLineEdits(set);
    }

    /// <summary>本面板自身的脏值判断。</summary>
    public bool IsDirty
    {
        get
        {
            if (ShowRawDoubleCode != _baselineShowRawDoubleCode) return true;

            // 纠错位置与数量只落 txt、不进 custom.yaml，CompileIcePatch() 感知不到它们，
            // 必须显式纳入脏值判断。
            //
            // 但**只在纠错开启时**才算：关闭状态下这两个值是惰性的 —— 写出来也会被
            // RemoveCorrectionAssets() 立刻删掉。算脏值会让「应用」按钮亮起、点了报成功、
            // 磁盘却什么都没变，是比不改更糟的假成功。
            if (CorrectionEnabled)
            {
                if (CorrectionInjectionPosition != _baselinePosition) return true;
                if (CorrectionCandidateCount != _baselineCandidateCount) return true;
            }

            if (!IsInstalled) return false;
            if (!CompileIcePatch().ValueEquals(_baseline)) return true;
            if (SaveOptionsPatch() is not null) return true;
            return HasStaleManagedKeysToRemove();
        }
    }

    /// <summary>
    /// 磁盘上残留着本类要删掉的托管键。
    ///
    /// ⚠️ 这道判断必须有，否则「删键」这一类自愈改动永远不算改动：
    /// 编译结果与基线都写着「删 menu/page_size」，两边相等 → IsDirty 为 false →
    /// 应用按钮不亮 → 用户点不到 → 那颗历史遗留的键就永远躺在 yaml 里。
    ///
    /// 收敛性：键被删掉之后 ValueForPath 返回 null，本方法随即回到 false，
    /// 不会形成「永远脏」的死循环。
    /// </summary>
    private bool HasStaleManagedKeysToRemove()
    {
        if (!File.Exists(IceCustomPath)) return false;

        foreach (var (key, value) in CompileIcePatch().Items)
        {
            if (value is not null) continue;
            if (_icePatch.ValueForPath(key) is not null) return true;
        }

        return false;
    }

    // ── 紫毫纠错资源部署 ──────────────────────────────────────────────

    /// <summary>
    /// 部署纠错资源：轻量 lua 纠错器 + 通用正向纠错词表 + 位置 / 数量 txt。
    /// 幂等，可反复调用。
    /// </summary>
    /// <param name="assetRoot">含 lua/amethyst_corrector.lua 与 data/correction_pinyin.txt 的根目录。</param>
    public void DeployCorrectionAssets(string? assetRoot)
    {
        var rimeDir = _environment.UserDirectory;
        Directory.CreateDirectory(rimeDir);

        if (assetRoot is not null && Directory.Exists(assetRoot))
        {
            var luaSrc = Path.Combine(assetRoot, "lua", "amethyst_corrector.lua");
            if (File.Exists(luaSrc))
            {
                var luaDir = Path.Combine(rimeDir, "lua");
                Directory.CreateDirectory(luaDir);
                File.Copy(luaSrc, Path.Combine(luaDir, "amethyst_corrector.lua"), overwrite: true);
            }

            var dictSrc = Path.Combine(assetRoot, "data", "correction_pinyin.txt");
            if (File.Exists(dictSrc))
                File.Copy(dictSrc, Path.Combine(rimeDir, "correction_pinyin.txt"), overwrite: true);
        }

        // 位置文件：供 lua 决定把「纠错」候选插到第几位。
        File.WriteAllText(Path.Combine(rimeDir, "correction_position.txt"),
            PositionName(CorrectionInjectionPosition) + Environment.NewLine);

        // 数量文件：1~3，供 lua 决定最多注入几条纠错候选。
        var count = Math.Clamp(CorrectionCandidateCount, 1, 3);
        File.WriteAllText(Path.Combine(rimeDir, "correction_count.txt"),
            count.ToString(System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
    }

    /// <summary>
    /// 关闭纠错时清理部署资源。**只删开关 txt 与词表，保留 lua 文件**：
    /// lua_filter@*amethyst_corrector 已被 MergedList 从 filters 摘除，编译不再引用该 lua，
    /// 留着它零副作用，且避免「删 lua 瞬间若过滤器仍在」的竞态导致编译失败、候选框消失。
    /// </summary>
    public void RemoveCorrectionAssets()
    {
        var rimeDir = _environment.UserDirectory;
        // 旧版本遗留的 strength 文件与 v2 反向表一并清理（本版无强度分级、改用正向表）。
        foreach (var name in new[]
                 {
                     "correction_strength.txt", "correction_map.txt", "correction_pinyin.txt",
                     "correction_position.txt", "correction_count.txt",
                 })
        {
            var path = Path.Combine(rimeDir, name);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 文件被占用（输入法正在读）：不致命，下次部署会再清一次
                }
            }
        }
    }

    // ── 双拼方案 ──────────────────────────────────────────────────────

    private void LoadDoublePinyinPatch()
    {
        var id = ActivePinyinSchemaId;
        if (string.IsNullOrEmpty(id) || id == "rime_ice")
        {
            _doublePinyinPatch = null;
            ShowRawDoubleCode = false;
            _baselineShowRawDoubleCode = false;
            return;
        }

        var file = new CustomYamlFile(Path.Combine(_environment.UserDirectory, id + ".custom.yaml"));
        file.Load();
        _doublePinyinPatch = file;

        // 空列表（[]）表示「不做任何 preedit 转换」，即原样显示双拼编码
        var raw = file.ValueForPath("translator/preedit_format") is List<object?> { Count: 0 };
        ShowRawDoubleCode = raw;
        _baselineShowRawDoubleCode = raw;
    }

    private void WriteDoublePinyinPatch()
    {
        var file = _doublePinyinPatch;
        if (file is null || !file.IsWritable) return;
        if (ShowRawDoubleCode == _baselineShowRawDoubleCode) return;

        file.Set("translator/preedit_format", ShowRawDoubleCode ? new List<object?>() : null);
        file.Save();
        _baselineShowRawDoubleCode = ShowRawDoubleCode;
    }

    // ── 原始 YAML 编辑 ────────────────────────────────────────────────

    /// <summary>rime_ice.custom.yaml 的磁盘原文（文件不存在时给出即将写入的骨架）。</summary>
    public string RawIceText()
    {
        if (File.Exists(IceCustomPath))
        {
            try
            {
                return File.ReadAllText(IceCustomPath);
            }
            catch (IOException)
            {
                // 落回骨架
            }
        }

        return _icePatch.Serialize();
    }

    /// <summary>校验原始 YAML；返回 null 表示合法，否则返回错误描述。</summary>
    public string? ValidateRawIce(string text)
    {
        if (text.Trim().Length == 0) return null;
        var parsed = YamlLoader.Load(text);
        return parsed.Success ? null : parsed.Error;
    }

    /// <summary>保存原始 YAML：校验 → .bak → 落盘 → 重载界面。</summary>
    public void SaveRawIce(string text)
    {
        if (ValidateRawIce(text) is not null)
            throw PanelException.RefusedToOverwrite(Path.GetFileName(IceCustomPath));

        Directory.CreateDirectory(Path.GetDirectoryName(IceCustomPath)!);

        if (File.Exists(IceCustomPath))
        {
            var backup = IceCustomPath + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Copy(IceCustomPath, backup);
        }

        var body = text.EndsWith('\n') ? text : text + "\n";
        File.WriteAllText(IceCustomPath, body);
        Reload();
    }

    // ── 恢复默认 ──────────────────────────────────────────────────────

    /// <summary>
    /// 把本面板管理的配置全部回到出厂默认：UI 状态回出厂、save_options 回落出厂。
    /// **不**直接落盘 —— 改动进入 dirty 状态，用户需点「应用」才会落盘。
    /// 与全项目「未应用即不落盘」铁律一致。
    /// </summary>
    public void ResetManaged()
    {
        if (!IsInstalled) return;

        // 1. UI 状态回出厂。开关模式同样要认出厂 save_options：出厂即「记忆」的 5 项
        //    若被一律设成固定开/关，磁盘结果虽然对（摘出名单 → 删键回落 default 出厂值），
        //    界面却会错显成「固定关」，用户会以为重置把记忆功能关掉了。
        Switches = Template.Switches
            .Select(t => new RimeIceSwitchItem(t.Name, t.States, t.Abbrev, FactoryModeFor(t)))
            .ToList();
        Opencc = Template.Opencc;
        EnableMeltEng = true;
        EnableCnEn = true;
        EnableRadical = true;
        EnableEmojiDict = true;
        LuaFilters = LuaFilterKeys.ToDictionary(k => k, _ => true, StringComparer.Ordinal);
        FuzzySelection = [];
        CorrectionEnabled = false;
        CorrectionInjectionPosition = CorrectionInjectionPosition.AfterFirst;
        CorrectionCandidateCount = 1;
        ShowRawDoubleCode = false;

        // 2. 兜底清掉 rime_ice.custom.yaml 里的托管键。UI 已回出厂 → CompileIcePatch()
        //    会对全部托管项写 null，本来就不会再写回去；这一步额外负责扫掉历史遗留
        //    （例如旧版本写过、现已不再编译的键），用户手写的其他条目不受影响。
        _icePatch.RemoveManaged(ManagedIceKeys);
    }

    /// <summary>
    /// 急救机制：将雾凇拼音**所有**配置恢复到出厂默认状态。
    /// 与 <see cref="ResetManaged"/> 的区别：会删除 rime_ice.custom.yaml 整个文件
    /// （包括用户手写的非托管键），删除前自动备份为 .bak。
    ///
    /// 落盘一气呵成（与「保险」同级语义，用于「点错了什么把 rime-ice 搞乱了」的救场）。
    /// </summary>
    public void ResetAll()
    {
        if (!IsInstalled) return;

        if (File.Exists(IceCustomPath))
        {
            var backup = IceCustomPath + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Copy(IceCustomPath, backup);
            File.Delete(IceCustomPath);
        }

        RemoveCorrectionAssets();
        Reload();

        // save_options 回落出厂：删键即可，让 Rime 读回 default.yaml 的出厂名单。
        var file = new CustomYamlFile(DefaultCustomPath);
        file.Load();
        if (file.IsWritable && File.Exists(DefaultCustomPath))
        {
            var set = new PatchSet();
            set.Remove("switcher/save_options");
            file.ApplyLineEdits(set);
        }
    }
}

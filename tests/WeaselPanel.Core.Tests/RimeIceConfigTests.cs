//
//  RimeIceConfigTests.cs
//  WeaselPanel.Core.Tests
//
//  雾凇拼音（rime-ice）配置 + 紫毫纠错挂载的行为测试。
//  全程跑在临时目录上，不依赖本机是否装了小狼毫、也不联网。
//
//  这些用例守的是几类「不出错但会悄悄坏掉」的事：
//   1. 干净安装时不往 rime_ice.custom.yaml 里拍 switches 快照 —— 那会永久压制上游调整；
//   2. 列表型键的合并绝不产出残缺列表或 `key: []` —— 那会清空出厂规则、拼音拼不出词；
//   3. 绝不动 engine/processors —— 写了会让候选框消失、中文报废；
//   4. 成对约束（英文 ↔ autocap/reduce_english、拆字 ↔ 四条目）必须联动，不能只摘一半；
//   5. 只落 txt 不进 YAML 的纠错位置 / 数量，改了必须算脏值，否则配置永远写不进磁盘。
//

using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class RimeIceConfigTests
{
    // ── 夹具 ──────────────────────────────────────────────────────────

    /// <summary>出厂 rime_ice.schema.yaml（精简版，结构与真实文件一致）。</summary>
    private const string FactorySchema = """
# rime_ice.schema.yaml
schema:
  schema_id: rime_ice
  name: 雾凇拼音
  dependencies:
    - melt_eng
    - radical_pinyin

switches:
  - name: ascii_mode
    reset: 0
    states: [ 中文, 西文 ]
  - name: ascii_punct
    states: [ 。，, ．， ]
  - name: traditionalization
    states: [ 简, 繁 ]
  - name: emoji
    states: [ 关闭, 开启 ]
  - name: full_shape
    states: [ 半角, 全角 ]
  - name: search_single_char
    states: [ 关闭, 开启 ]

engine:
  translators:
    - punct_translator
    - script_translator
    - table_translator@melt_eng
    - table_translator@cn_en
    - table_translator@radical_lookup
  filters:
    - simplifier
    - lua_filter@*corrector
    - lua_filter@*autocap_filter
    - lua_filter@*v_filter
    - lua_filter@*pin_cand_filter
    - lua_filter@*long_word_filter
    - lua_filter@*reduce_english_filter
    - simplifier@emoji
    - lua_filter@*search@radical_pinyin
    - reverse_lookup_filter@radical_reverse_lookup
    - uniquifier

speller:
  algebra:
    - erase/^xx$/
    - abbrev/^([a-z]).+$/$1/

traditionalize:
  option_name: traditionalization
  opencc_config: s2t.json
""";

    /// <summary>
    /// 出厂 default.yaml。6 个开关里 5 个在 save_options 中（出厂态是「记忆」），
    /// 只有 ascii_mode 是固定关 —— 这是 rime-ice 的真实事实，判定「出厂默认」必须带上它。
    /// </summary>
    private const string FactoryDefault = """
# default.yaml
switcher:
  save_options:
    - ascii_punct
    - traditionalization
    - emoji
    - full_shape
    - search_single_char
  schema_list:
    - schema: rime_ice
    - schema: double_pinyin_flypy
""";

    private static (TempDirectory Tmp, WeaselEnvironment Env) Fixture(
        string? iceCustom = null, string? defaultCustom = null)
    {
        var tmp = new TempDirectory();
        var shared = Path.Combine(tmp.Root, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "rime_ice.schema.yaml"), FactorySchema);
        File.WriteAllText(Path.Combine(shared, "default.yaml"), FactoryDefault);

        if (iceCustom is not null) tmp.Write("rime_ice.custom.yaml", iceCustom);
        if (defaultCustom is not null) tmp.Write("default.custom.yaml", defaultCustom);

        var env = new WeaselEnvironment
        {
            UserDirectory = tmp.Root,
            SharedDataDirectory = shared,
        };
        return (tmp, env);
    }

    private static IReadOnlyList<string> Strings(PatchValue? value)
    {
        if (value is not PatchValue.StringListValue list) return [];
        return list.Value;
    }

    private static Dictionary<string, object?>? SwitchNamed(PatchValue? switches, string name)
    {
        if (switches is not PatchValue.MapListValue mapList) return null;
        return mapList.Value.FirstOrDefault(m =>
            m.TryGetValue("name", out var n) && n is string s && s == name);
    }

    // ── 未安装 ────────────────────────────────────────────────────────

    [Fact]
    public void 未安装时展示占位开关且编译结果为空()
    {
        using var tmp = new TempDirectory();
        var env = new WeaselEnvironment { UserDirectory = tmp.Root };

        var cfg = new RimeIceConfig(env);

        Assert.False(cfg.IsInstalled);
        // 界面仍要看到 6 行，用户才知道这个面板提供哪些能力
        Assert.Equal(6, cfg.Switches.Count);
        Assert.Equal(RimeIceConfig.PreviewSwitches.Select(s => s.Name), cfg.Switches.Select(s => s.Name));

        // 未安装时没有任何路径能把占位项写进磁盘
        Assert.Empty(cfg.CompileIcePatch().Items);
    }

    [Fact]
    public void 未安装时写盘不凭空创建文件()
    {
        using var tmp = new TempDirectory();
        var env = new WeaselEnvironment { UserDirectory = tmp.Root };

        new RimeIceConfig(env).WritePatch();

        Assert.False(File.Exists(Path.Combine(tmp.Root, "rime_ice.custom.yaml")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "default.custom.yaml")));
    }

    // ── 出厂模板 ──────────────────────────────────────────────────────

    [Fact]
    public void 出厂模板解析出六个开关与四类列表()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);

        Assert.True(cfg.IsInstalled);
        Assert.Equal(
            ["ascii_mode", "ascii_punct", "traditionalization", "emoji", "full_shape", "search_single_char"],
            cfg.Template.Switches.Select(s => s.Name));
        // ascii_punct 出厂没写 reset，其余同理；ascii_mode 明确写了 reset: 0
        Assert.Equal(0, cfg.Template.Switches.First(s => s.Name == "ascii_mode").FactoryReset);
        Assert.Equal(3, cfg.Template.Translators.Count(t => t.Contains("table_translator@")));
        Assert.Contains("uniquifier", cfg.Template.Filters);
        Assert.Equal(["melt_eng", "radical_pinyin"], cfg.Template.Dependencies);
        Assert.Equal("s2t.json", cfg.Template.Opencc);
    }

    [Fact]
    public void 出厂模板不读_build_目录里的编译产物()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        // build/ 里的那份已合并过我们自己打的补丁：拿它当「出厂模板」会形成反馈环 ——
        // 用户勾选的模糊音会被认成出厂自带，下次编译时因「与出厂一致」而删键，规则静默消失。
        tmp.Write("build/rime_ice.schema.yaml", """
schema:
  schema_id: rime_ice
switches:
  - name: ascii_mode
    reset: 0
    states: [ 中文, 西文 ]
speller:
  algebra:
    - erase/^xx$/
    - derive/^([zcs])h/$1/
""");

        var template = RimeIceConfig.ParseTemplate(env);

        Assert.Equal(6, template.Switches.Count);      // 仍是出厂那份的 6 个，不是 build 里的 1 个
        Assert.DoesNotContain("derive/^([zcs])h/$1/", template.Algebra);
    }

    // ── 三态开关 ──────────────────────────────────────────────────────

    [Fact]
    public void 开关全部停出厂默认时不写_switches_段()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);

        // 5 项是「记忆」、ascii_mode 是固定关 —— 都与出厂一致
        Assert.All(cfg.Switches, s => Assert.Equal(
            s.Name == "ascii_mode" ? SwitchDefaultMode.Off : SwitchDefaultMode.Remember, s.Mode));

        Assert.Null(cfg.CompileIcePatch().Items["switches"]);
        Assert.False(cfg.IsDirty);
    }

    [Fact]
    public void 设为固定开时写_reset_1_并从记忆名单摘除()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.Switches = cfg.Switches
            .Select(s => s.Name == "ascii_punct" ? s with { Mode = SwitchDefaultMode.On } : s)
            .ToList();

        var switches = SwitchNamed(cfg.CompileIcePatch().Items["switches"], "ascii_punct");
        Assert.NotNull(switches);
        Assert.Equal(1, switches["reset"]);

        // 固定默认与「记忆」互斥：设了固定开就必须从 save_options 摘除
        var names = Strings(cfg.SaveOptionsPatch());
        Assert.DoesNotContain("ascii_punct", names);
        Assert.Contains("traditionalization", names);   // 别的方案记忆项与原记忆项都要留着
    }

    [Fact]
    public void 记忆名单只增删雾凇自己那六个名字()
    {
        var (tmp, env) = Fixture(
            defaultCustom: """
patch:
  switcher:
    save_options:
      - ascii_punct
      - traditionalization
      - emoji
      - full_shape
      - search_single_char
      - wubi_something
""");
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.Switches = cfg.Switches
            .Select(s => s.Name == "emoji" ? s with { Mode = SwitchDefaultMode.Off } : s)
            .ToList();

        // wubi_something 是别的方案的记忆项，整体覆盖会把它静默删掉
        var names = Strings(cfg.SaveOptionsPatch());
        Assert.Contains("wubi_something", names);
        Assert.DoesNotContain("emoji", names);
    }

    [Fact]
    public void 记忆名单与出厂一致时不写盘()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);

        Assert.Null(cfg.SaveOptionsPatch());
    }

    // ── 列表托管合并 ──────────────────────────────────────────────────

    [Fact]
    public void 安全护栏_空合并结果绝不写成空数组()
    {
        // `key: []` 会清空 Rime 的内置 / 出厂列表 → 候选框消失、中文报废
        Assert.Null(RimeIceConfig.SafeListPatch([], ["a", "b"]));
        // 与出厂一致也不落盘（避免快照压制上游日后调整）
        Assert.Null(RimeIceConfig.SafeListPatch(["a", "b"], ["a", "b"]));
        Assert.NotNull(RimeIceConfig.SafeListPatch(["a", "b", "c"], ["a", "b"]));
    }

    [Fact]
    public void 绝不动_engine_processors()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.CorrectionEnabled = true;

        // 补丁里一旦出现 engine/processors: {}（哪怕是清键产生的空映射），Rime 会把
        // 内置默认处理器整体清空 → 按键完全不被处理、候选框彻底消失。
        Assert.DoesNotContain("engine/processors", cfg.CompileIcePatch().Items.Keys);
    }

    [Fact]
    public void 列表合并保留用户自己加的条目()
    {
        var (tmp, env) = Fixture(iceCustom: """
patch:
  engine:
    filters:
      - simplifier
      - lua_filter@*corrector
      - lua_filter@*autocap_filter
      - lua_filter@*v_filter
      - lua_filter@*pin_cand_filter
      - lua_filter@*long_word_filter
      - lua_filter@*reduce_english_filter
      - simplifier@emoji
      - lua_filter@*search@radical_pinyin
      - reverse_lookup_filter@radical_reverse_lookup
      - my_own_filter
      - uniquifier
""");
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.EnableEmojiDict = false;      // 只关一个托管项

        var filters = Strings(cfg.CompileIcePatch().Items["engine/filters"]);

        Assert.Contains("my_own_filter", filters);        // 用户自己加的原样保留
        Assert.DoesNotContain("simplifier@emoji", filters);
        Assert.Contains("uniquifier", filters);           // 出厂条目一个不少
    }

    [Fact]
    public void 关闭英文时_autocap_与_reduce_english_一并摘除()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.EnableMeltEng = false;

        var filters = Strings(cfg.CompileIcePatch().Items["engine/filters"]);
        var translators = Strings(cfg.CompileIcePatch().Items["engine/translators"]);
        var dependencies = Strings(cfg.CompileIcePatch().Items["schema/dependencies"]);

        Assert.DoesNotContain("lua_filter@*autocap_filter", filters);
        Assert.DoesNotContain("lua_filter@*reduce_english_filter", filters);
        Assert.DoesNotContain("table_translator@melt_eng", translators);
        Assert.DoesNotContain("melt_eng", dependencies);      // translator 与 dependency 同生同死
        Assert.Contains("table_translator@cn_en", translators);
    }

    [Fact]
    public void 关闭部件拆字时四条目联动摘除()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.EnableRadical = false;

        var filters = Strings(cfg.CompileIcePatch().Items["engine/filters"]);
        var translators = Strings(cfg.CompileIcePatch().Items["engine/translators"]);
        var dependencies = Strings(cfg.CompileIcePatch().Items["schema/dependencies"]);

        Assert.DoesNotContain("table_translator@radical_lookup", translators);
        Assert.DoesNotContain("lua_filter@*search@radical_pinyin", filters);
        Assert.DoesNotContain("reverse_lookup_filter@radical_reverse_lookup", filters);
        Assert.DoesNotContain("radical_pinyin", dependencies);
    }

    // ── 模糊音与纠错 ──────────────────────────────────────────────────

    [Fact]
    public void 模糊音规则前置到出厂_algebra_之前()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.FuzzySelection = ["derive/^l/n/", "derive/ang$/an/"];

        var algebra = Strings(cfg.CompileIcePatch().Items["speller/algebra"]);

        // 已选规则在前，出厂常驻规则原样保留在后
        Assert.Equal("derive/^l/n/", algebra[0]);
        Assert.Equal("derive/ang$/an/", algebra[1]);
        Assert.Contains("erase/^xx$/", algebra);
        Assert.Contains("abbrev/^([a-z]).+$/$1/", algebra);
    }

    [Fact]
    public void 纠错关闭时残留的_derive_规则被剥离()
    {
        // 场景：用户上一次开着纠错，磁盘里躺着一批 derive 规则；现在关掉。
        // 若不剥离，规则留在 yaml 里 → 开关关不掉、UI 与磁盘脱钩。
        //
        // 注意挑的规则必须**只属于纠错**：n↔l、ang↔an 这类同时是模糊音项，
        // 用它们做夹具会被正确归类成「已勾选的模糊音」而不会被剥离。
        var (tmp, env) = Fixture(iceCustom: """
patch:
  speller:
    algebra:
      - derive/^mi$/ni/
      - derive/^gao$/hao/
      - erase/^xx$/
      - abbrev/^([a-z]).+$/$1/
""");
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        Assert.Empty(cfg.FuzzySelection);          // 这两条不是模糊音项
        cfg.CorrectionEnabled = false;

        var algebra = Strings(cfg.CompileIcePatch().Items["speller/algebra"]);

        Assert.DoesNotContain("derive/^mi$/ni/", algebra);
        Assert.DoesNotContain("derive/^gao$/hao/", algebra);
        Assert.Contains("erase/^xx$/", algebra);
        Assert.Contains("abbrev/^([a-z]).+$/$1/", algebra);
    }

    [Fact]
    public void 纠错开启时_algebra_恒写完整合并结果()
    {
        // ⚠️ 判据必须用「当前磁盘实际」而非「出厂模板」：derive 是面板非出厂的增量，
        // 若与 template 比较会判成「相等」而删键 → 磁盘回退到出厂（无 derive）→ 假关闭。
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.CorrectionEnabled = true;

        // 第一轮：写出带 derive 的 algebra
        var first = Strings(cfg.CompileIcePatch().Items["speller/algebra"]);
        Assert.NotNull(cfg.CompileIcePatch().Items["speller/algebra"]);
        Assert.Contains("derive/^n/l/", first);
        Assert.True(first.Count > RimeIceConfig.CorrectionRules.Length);   // 出厂规则也在里面

        // 第二轮：模拟「已落盘后再编译」。此时 merged 与磁盘一致，
        // 若走「相等就删键」的短路，开关就会被假关闭。
        cfg.WritePatch();
        Assert.True(cfg.CorrectionEnabled);
        Assert.NotNull(cfg.CompileIcePatch().Items["speller/algebra"]);
        Assert.Contains("derive/^n/l/", Strings(cfg.CompileIcePatch().Items["speller/algebra"]));
    }

    [Fact]
    public void 纠错与模糊音的规则原文重叠_开启纠错时重叠项强制生效()
    {
        // 两组的「基础」档本来就是同一批 derive 字符串（n↔l、zh/ch/sh↔z/c/s、an↔ang …），
        // 这是设计如此 —— 开纠错等于顺带打开基础模糊音。
        //
        // 由此带来一个必须堵住的坑：开着纠错时，用户取消勾选一个重叠项并保存，规则照样
        // 被纠错注入，界面却显示未勾选 —— 操作静默失效。因此界面必须把它显示成
        // 「已勾选且不可改」，IsFuzzyRuleForcedByCorrection 就是那个判据。
        Assert.NotEmpty(RimeIceConfig.FuzzyRuleSet.Intersect(RimeIceConfig.CorrectionRuleSet));

        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        Assert.False(cfg.IsFuzzyRuleForcedByCorrection("derive/^l/n/"));   // 纠错未开

        cfg.CorrectionEnabled = true;
        Assert.True(cfg.IsFuzzyRuleForcedByCorrection("derive/^l/n/"));
        // 只属于模糊音、不属于纠错的项不该被强制
        Assert.False(cfg.IsFuzzyRuleForcedByCorrection("derive/ai$/an/"));

        var algebra = Strings(cfg.CompileIcePatch().Items["speller/algebra"]);
        Assert.Contains("derive/^l/n/", algebra);
        // 去重：重叠规则在模糊音段与纠错段各出现一次，不得写两遍
        Assert.Equal(algebra.Count, algebra.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 两组规则自身均无重复原文()
    {
        Assert.Equal(RimeIceConfig.CorrectionRules.Length, RimeIceConfig.CorrectionRuleSet.Count);
        Assert.Equal(RimeIceConfig.FuzzyRules.Length, RimeIceConfig.FuzzyRuleSet.Count);
    }

    [Fact]
    public void 开启纠错时挂上纠错过滤器且不重复()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.CorrectionEnabled = true;

        var filters = Strings(cfg.CompileIcePatch().Items["engine/filters"]);
        Assert.Contains("lua_filter@*amethyst_corrector", filters);

        // 插在 uniquifier 之前，让重排后的候选也被去重清理
        var ordered = filters.ToList();
        Assert.True(ordered.IndexOf("lua_filter@*amethyst_corrector") < ordered.IndexOf("uniquifier"));

        // 反复编译不得重复插入
        cfg.WritePatch();
        var again = Strings(cfg.CompileIcePatch().Items["engine/filters"]);
        Assert.Equal(1, again.Count(x => x == "lua_filter@*amethyst_corrector"));
    }

    // ── 只落 txt 的纠错状态 ───────────────────────────────────────────

    [Fact]
    public void 注入位置与候选数量只落_txt_也算脏值()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        Assert.False(cfg.IsDirty);

        // 这两个值只写 correction_position.txt / correction_count.txt，不进 custom.yaml。
        // CompileIcePatch() 感知不到它们的变化，脏值判断必须单独跟踪 ——
        // 否则改了它们以后「应用」按钮不亮，配置永远写不进磁盘。
        //
        // 反过来，纠错**关闭**时它们不得算脏值：那两个值是惰性的，写出来也会被
        // RemoveCorrectionAssets() 立刻删掉 —— 按钮亮起、点了报成功、磁盘什么都没变，
        // 比不改更糟。
        cfg.CorrectionInjectionPosition = CorrectionInjectionPosition.Top;
        cfg.CorrectionCandidateCount = 2;
        Assert.False(cfg.IsDirty);

        cfg.CorrectionEnabled = true;
        Assert.True(cfg.IsDirty);

        cfg.WritePatch();
        Assert.False(cfg.IsDirty);
        Assert.Equal("top", File.ReadAllText(Path.Combine(tmp.Root, "correction_position.txt")).Trim());
        Assert.Equal("2", File.ReadAllText(Path.Combine(tmp.Root, "correction_count.txt")).Trim());

        cfg.CorrectionCandidateCount = 3;
        Assert.True(cfg.IsDirty);
        cfg.WritePatch();
        Assert.Equal("3", File.ReadAllText(Path.Combine(tmp.Root, "correction_count.txt")).Trim());
    }

    [Fact]
    public void 候选数量越界时收敛到_1_到_3()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.CorrectionEnabled = true;
        cfg.CorrectionCandidateCount = 99;

        cfg.WritePatch();

        Assert.Equal("3", File.ReadAllText(Path.Combine(tmp.Root, "correction_count.txt")).Trim());
    }

    // ── 纠错资源部署 ──────────────────────────────────────────────────

    [Fact]
    public void 开启纠错时部署_lua_与词表_关闭时保留_lua()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var assets = Path.Combine(tmp.Root, "_assets");
        Directory.CreateDirectory(Path.Combine(assets, "lua"));
        Directory.CreateDirectory(Path.Combine(assets, "data"));
        File.WriteAllText(Path.Combine(assets, "lua", "amethyst_corrector.lua"), "-- stub\n");
        File.WriteAllText(Path.Combine(assets, "data", "correction_pinyin.txt"), "wo\t1\t我\n");

        var cfg = new RimeIceConfig(env);
        cfg.CorrectionEnabled = true;
        cfg.CorrectionInjectionPosition = CorrectionInjectionPosition.Top;
        cfg.WritePatch(assets);

        Assert.True(File.Exists(Path.Combine(tmp.Root, "lua", "amethyst_corrector.lua")));
        Assert.True(File.Exists(Path.Combine(tmp.Root, "correction_pinyin.txt")));
        Assert.Equal("top", File.ReadAllText(Path.Combine(tmp.Root, "correction_position.txt")).Trim());

        // 关闭时只删 txt 与词表，保留 lua：过滤器已被摘除，留着它零副作用，
        // 且避免「删 lua 瞬间若过滤器仍在」的竞态导致编译失败、候选框消失。
        cfg.CorrectionEnabled = false;
        cfg.WritePatch(assets);

        Assert.True(File.Exists(Path.Combine(tmp.Root, "lua", "amethyst_corrector.lua")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "correction_pinyin.txt")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "correction_position.txt")));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "correction_count.txt")));
    }

    // ── 恢复默认 ──────────────────────────────────────────────────────

    [Fact]
    public void 恢复默认后回到出厂态()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        // 把界面搅乱
        cfg.Switches = cfg.Switches.Select(s => s with { Mode = SwitchDefaultMode.On }).ToList();
        cfg.EnableMeltEng = false;
        cfg.EnableRadical = false;
        cfg.EnableEmojiDict = false;
        cfg.FuzzySelection = ["derive/^l/n/"];
        cfg.CorrectionEnabled = true;
        cfg.Opencc = "s2twp.json";
        Assert.True(cfg.IsDirty);

        cfg.ResetManaged();

        // 重置只改内存状态，不落盘（与全项目「未应用即不落盘」铁律一致）
        Assert.False(cfg.IsDirty);
        Assert.All(cfg.Switches, s => Assert.Equal(
            s.Name == "ascii_mode" ? SwitchDefaultMode.Off : SwitchDefaultMode.Remember, s.Mode));
        Assert.True(cfg.EnableMeltEng);
        Assert.True(cfg.EnableRadical);
        Assert.True(cfg.EnableEmojiDict);
        Assert.Empty(cfg.FuzzySelection);
        Assert.False(cfg.CorrectionEnabled);
        Assert.Equal("s2t.json", cfg.Opencc);
    }

    // ── 原始 YAML 编辑 ────────────────────────────────────────────────

    [Fact]
    public void 原始_YAML_非法时拒写()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);

        Assert.Null(cfg.ValidateRawIce("patch:\n  switches: []\n"));
        Assert.NotNull(cfg.ValidateRawIce("patch: [ unclosed\n"));

        var before = cfg.RawIceText();
        Assert.Throws<PanelException>(() => cfg.SaveRawIce("patch: [ unclosed\n"));
        Assert.Equal(before, cfg.RawIceText());   // 拒写，磁盘原样
    }

    [Fact]
    public void 保存原始_YAML_会备份旧文件()
    {
        var (tmp, env) = Fixture(iceCustom: "patch:\n  switches: []\n");
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);
        cfg.SaveRawIce("patch:\n  traditionalize:\n    opencc_config: s2hk.json\n");

        Assert.True(tmp.Exists("rime_ice.custom.yaml.bak"));
        Assert.Contains("s2hk.json", cfg.RawIceText());
        Assert.Equal("s2hk.json", cfg.Opencc);    // 保存后立即重载，UI 与磁盘一致
    }

    // ── 候选词数恒删键 ────────────────────────────────────────────────

    [Fact]
    public void 恒删方案级候选词数_避免压过全局设置()
    {
        // 历史遗留：旧版本允许在本页拨候选数，那批用户的 rime_ice.custom.yaml 里
        // 躺着一个方案级 menu/page_size —— 方案级压过全局，不去删它，
        // 用户去「按键与输入」页怎么改都没效果。
        var (tmp, env) = Fixture(iceCustom: """
patch:
  menu:
    page_size: 9
""");
        using var _ = tmp;

        var cfg = new RimeIceConfig(env);

        Assert.Null(cfg.CompileIcePatch().Items["menu/page_size"]);
        Assert.True(cfg.IsDirty);          // 有键要删，必须算脏值，否则删不掉

        cfg.WritePatch();
        Assert.DoesNotContain("page_size", cfg.RawIceText());
    }

    // ── patch 段兜底（v0.3.0） ────────────────────────────────────────

    /// <summary>检测 yaml 文本顶层（0 缩进）是否存在 <c>patch:</c> 行。</summary>
    private static bool HasTopLevelPatchLine(string path)
    {
        foreach (var raw in File.ReadAllText(path).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var leading = 0;
            while (leading < line.Length && (line[leading] == ' ' || line[leading] == '\t')) leading++;
            var trimmed = line.TrimStart();
            if (leading != 0) continue;                      // 顶层判据：必须是 0 缩进
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("patch:", StringComparison.Ordinal))
            {
                var after = trimmed.Length > 6 ? trimmed[6] : '\0';
                if (after == '\0' || char.IsWhiteSpace(after)) return true;
            }
            return false;
        }
        return false;
    }

    [Fact]
    public void 顶层无patch段时写入自动注入空壳()
    {
        // 场景：从其他途径继承 / 手写出来的 rime_ice.custom.yaml 顶层只有零散键，
        // 没有 patch: 段包裹。Rime 实际只读 patch 段下，所以用户原数据本就无效；
        // 但本面板所有 ApplyPatchValue 都假设路径前缀是 "patch"，没壳就抛
        // 「未找到块 patch/...」异常。EnsurePatchEnvelop 兜底注入。
        var (tmp, env) = Fixture(iceCustom: """
# 用户手写：没有 patch: 顶层
engine:
  filters:
    - simplifier
    - uniquifier
""");
        using var _ = tmp;

        new RimeIceConfig(env).WritePatch();

        var text = File.ReadAllText(Path.Combine(tmp.Root, "rime_ice.custom.yaml"));
        Assert.True(HasTopLevelPatchLine(Path.Combine(tmp.Root, "rime_ice.custom.yaml")),
            $"写入后必须存在顶层 'patch:' 行\n--- 原文 ---\n{text}");
    }

    [Fact]
    public void 已有嵌套写法patch顶层时不再重复注入()
    {
        var (tmp, env) = Fixture(iceCustom: """
patch:
  engine:
    filters:
      - simplifier
      - uniquifier
""");
        using var _ = tmp;
        var customPath = Path.Combine(tmp.Root, "rime_ice.custom.yaml");

        new RimeIceConfig(env).WritePatch();

        // 不能因为兜底而把第二个 patch: 段插进去——面板的数据结构全靠单 patch 段承担。
        var text = File.ReadAllText(customPath);
        var patchCount = text.Split('\n')
            .Count(l => l.Length > 0 && l[0] != ' ' && l[0] != '\t'
                && l.TrimStart().StartsWith("patch:", StringComparison.Ordinal));
        Assert.Equal(1, patchCount);
    }

    [Fact]
    public void 已有扁平写法patch顶层时不再重复注入()
    {
        var (tmp, env) = Fixture(iceCustom: """
patch:
  switches: []
""");
        using var _ = tmp;
        var customPath = Path.Combine(tmp.Root, "rime_ice.custom.yaml");

        new RimeIceConfig(env).WritePatch();

        var text = File.ReadAllText(customPath);
        var patchCount = text.Split('\n')
            .Count(l => l.Length > 0 && l[0] != ' ' && l[0] != '\t'
                && l.TrimStart().StartsWith("patch:", StringComparison.Ordinal));
        Assert.Equal(1, patchCount);
    }

    [Fact]
    public void 文件不存在时兜底不凭空创建_yaml()
    {
        var (tmp, env) = Fixture();
        using var _ = tmp;
        Assert.False(File.Exists(Path.Combine(tmp.Root, "rime_ice.custom.yaml")));

        new RimeIceConfig(env).WritePatch();

        // 干净安装 + 没有改动 → 不写文件（与"凭空创建属垃圾文件"的铁律一致）
        Assert.False(File.Exists(Path.Combine(tmp.Root, "rime_ice.custom.yaml")));
    }
}

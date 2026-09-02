//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  GPL-3.0。自定义配色方案（模型 / 注册表 / 注入 YAML）测试。
//
//  这些用例守三件事：
//   1. 编辑器的 22 个通道与解析器逐条对应 —— 少一个键，用户就有一个颜色改不动；
//   2. 注入是「手术式」的 —— 用户手写在 weasel.custom.yaml 里的其它条目一个字都不能少；
//   3. 删掉的方案必须从 YAML 里真的消失 —— 否则小狼毫会一直读到一套用户以为删了的配色。

using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class UserColorSchemeTests
{
    // ── 通道清单 ────────────────────────────────────────────────────────

    [Fact]
    public void 可编辑通道与解析器一一对应()
    {
        // 顺序即 UI 行序，也必须与 ColorSchemeResolver 的解析顺序一致
        Assert.Equal(22, ColorSchemeFields.ColorKeys.Length);
        Assert.Equal(ColorSchemeFields.ColorKeys.Length,
            ColorSchemeFields.ColorKeys.Distinct(StringComparer.Ordinal).Count());

        // 逐个键都要能从解析结果里取到值，反之解析器认识的键也必须在清单里
        var resolved = new ResolvedColorScheme();
        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            Assert.True(resolved.AbgrForKey(key).HasValue, "解析器不认识通道 " + key);
        }
        Assert.Null(resolved.AbgrForKey("preedit_text_color"));   // macOS 专属键，小狼毫没有
        Assert.Null(resolved.AbgrForKey("no_such_key"));
    }

    [Fact]
    public void 分组覆盖全部通道且不重复()
    {
        // 分组漏一个键 → 编辑器里那一行静默消失，用户有一个颜色永远改不动。
        // 这道断言就是防止这种「看不见的丢失」。
        var grouped = ColorSchemeFields.Groups.SelectMany(g => g.Keys).ToList();

        Assert.Equal(ColorSchemeFields.ColorKeys.Length, grouped.Count);
        Assert.Equal(grouped.Count, grouped.Distinct(StringComparer.Ordinal).Count());

        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            Assert.Contains(key, grouped);
        }
        // 分组里也不许出现解析器不认识的键
        foreach (var key in grouped)
        {
            Assert.True(ColorSchemeFields.IsColorKey(key), "分组里有未知键 " + key);
        }
    }

    // ── id 生成 ─────────────────────────────────────────────────────────

    [Fact]
    public void MakeId只留ASCII小写并加前缀()
    {
        Assert.Equal("user_aqua", UserColorSchemeStore.MakeId("aqua"));
        Assert.Equal("user_aqua", UserColorSchemeStore.MakeId("Aqua"));
        // 分隔符统一用连字符（与 macOS 版一致），不是下划线
        Assert.Equal("user_my-theme", UserColorSchemeStore.MakeId("My Theme"));
        Assert.Equal("user_my-theme", UserColorSchemeStore.MakeId("  My   Theme!! "));
        Assert.Equal("user_theme2", UserColorSchemeStore.MakeId("theme2"));

        // 中文只进显示名，不进 id（文件头第 3 条：非 ASCII 配置路径无法真机验证）
        Assert.Equal("user_scheme", UserColorSchemeStore.MakeId("墨色"));
        Assert.Equal("user_scheme", UserColorSchemeStore.MakeId(""));
        Assert.Equal("user_scheme", UserColorSchemeStore.MakeId(null));

        // 下划线要保留，否则 user_foo → user-foo → 认不出前缀 → user_user-foo
        Assert.Equal("user_foo", UserColorSchemeStore.MakeId("user_foo"));
        Assert.Equal("user_a_b", UserColorSchemeStore.MakeId("A_B"));
    }

    [Fact]
    public void UniqueId在重名时加序号而不是覆盖()
    {
        using var tmp = new TempDirectory();
        var registry = UserColorSchemeRegistry.Load(Path.Combine(tmp.Root, "r.json"));

        var first = registry.UniqueId("我的主题");
        registry.Add(new UserColorScheme { Id = first, Name = "我的主题" });

        var second = registry.UniqueId("我的主题");
        Assert.NotEqual(first, second);
        Assert.Equal(first + "-2", second);
    }

    // ── 定义生成 ────────────────────────────────────────────────────────

    [Fact]
    public void 非abgr方案必须按自己的字节序解析()
    {
        // 同一个字面量在 argb 与 abgr 下是两种颜色：argb 的 "0xFF0000" 是红，
        // abgr 的 "0xFF0000" 是蓝。编辑器预览必须按方案自己的 color_format 解析，
        // 否则「改字节序 → 预览颜色全反」这种 bug 只在非默认方案上出现，极难定位。
        // 用非对称色值区分：0x112233 在 argb 下是 R=11 G=22 B=33，
        // 在 abgr 下是 B=11 G=22 R=33 —— 解析成 ABGR 打包值后二者互为 R/B 倒置。
        const string literal = "0x112233";

        var argb = new UserColorScheme { Id = "user_a", Name = "A", Format = RimeColorFormat.Argb };
        argb.Colors["text_color"] = literal;
        Assert.Equal(0xFF332211u, argb.Resolve().TextColor);

        var abgr = new UserColorScheme { Id = "user_b", Name = "B", Format = RimeColorFormat.Abgr };
        abgr.Colors["text_color"] = literal;
        Assert.Equal(0xFF112233u, abgr.Resolve().TextColor);
    }

    [Fact]
    public void 与出厂相同的元数据不落盘()
    {
        var scheme = new UserColorScheme { Id = "user_x", Name = "X" };
        var def = ColorSchemeFields.PresetDefinition(scheme);

        Assert.Equal("X", def[ColorSchemeFields.NameKey]);
        Assert.False(def.ContainsKey(ColorSchemeFields.FormatKey));    // abgr 是默认
        Assert.False(def.ContainsKey(ColorSchemeFields.SpaceKey));     // srgb 是默认
        Assert.False(def.ContainsKey(ColorSchemeFields.AuthorKey));    // 空作者不写

        scheme.Format = RimeColorFormat.Argb;
        scheme.ColorSpace = RimeColorSpace.DisplayP3;
        scheme.Author = "大狼";
        var def2 = ColorSchemeFields.PresetDefinition(scheme);
        Assert.Equal("argb", def2[ColorSchemeFields.FormatKey]);
        Assert.Equal("display_p3", def2[ColorSchemeFields.SpaceKey]);
        Assert.Equal("大狼", def2[ColorSchemeFields.AuthorKey]);
    }

    [Fact]
    public void 没设过的通道不写进定义()
    {
        var scheme = new UserColorScheme { Id = "user_x", Name = "X" };
        scheme.Colors["text_color"] = "0x112233";

        var def = ColorSchemeFields.PresetDefinition(scheme);
        Assert.Equal("0x112233", def["text_color"]);
        Assert.False(def.ContainsKey("back_color"));   // 交给回退链
    }

    [Fact]
    public void 展开解析结果后渲染结果与原方案一致()
    {
        // 只写两个键的最小配色
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text_color"] = "0xff112233",
            ["back_color"] = "0xff445566"
        };
        var before = ColorSchemeResolver.Resolve(k => source.TryGetValue(k, out var v) ? v : null);

        var scheme = UserColorScheme.FromResolved("user_x", "X", before);
        Assert.Equal(ColorSchemeFields.ColorKeys.Length, scheme.Colors.Count);

        // 展开成 22 个显式键后，再解析必须得到完全相同的值
        var after = scheme.Resolve();
        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            Assert.Equal(before.AbgrForKey(key), after.AbgrForKey(key));
        }
    }

    // ── 注册表往返 ──────────────────────────────────────────────────────

    [Fact]
    public void 注册表保存后读回完全一致()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "user_color_schemes.json");

        var registry = UserColorSchemeRegistry.Load(path);
        var scheme = new UserColorScheme
        {
            Id = "user_night",
            Name = "夜色",
            Author = "大狼",
            Format = RimeColorFormat.Argb,
            ColorSpace = RimeColorSpace.DisplayP3
        };
        scheme.Colors["back_color"] = "0x1a1a2e";
        scheme.Colors["text_color"] = "0xeee";
        registry.Add(scheme);
        registry.MarkApplied(new[] { "user_night" });
        registry.Save();

        var reloaded = UserColorSchemeRegistry.Load(path);
        Assert.False(reloaded.IsCorrupt);
        Assert.Single(reloaded.Schemes);

        var got = reloaded.Get("user_night")!;
        Assert.Equal("夜色", got.Name);
        Assert.Equal("大狼", got.Author);
        Assert.Equal(RimeColorFormat.Argb, got.Format);
        Assert.Equal(RimeColorSpace.DisplayP3, got.ColorSpace);
        Assert.Equal("0x1a1a2e", got.Colors["back_color"]);
        Assert.Equal("0xeee", got.Colors["text_color"]);
        Assert.Equal(new[] { "user_night" }, reloaded.AppliedIds);
    }

    [Fact]
    public void 注册表保留中文而不转义()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "user_color_schemes.json");

        var registry = UserColorSchemeRegistry.Load(path);
        registry.Add(new UserColorScheme { Id = "user_x", Name = "墨色" });
        registry.Save();

        var text = File.ReadAllText(path);
        Assert.Contains("墨色", text);
        Assert.DoesNotContain("\\u58A8", text);
    }

    [Fact]
    public void 注册表损坏时拒绝覆写()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "user_color_schemes.json");
        File.WriteAllText(path, "{ 这不是 JSON ]");

        var registry = UserColorSchemeRegistry.Load(path);
        Assert.True(registry.IsCorrupt);
        Assert.NotNull(registry.LoadError);

        // 损坏的文件绝不能被空清单盖掉
        Assert.Throws<PanelException>(() => registry.Save());
        Assert.Contains("这不是 JSON", File.ReadAllText(path));
    }

    // ── 注入 weasel.custom.yaml ─────────────────────────────────────────

    private static WeaselEnvironment Env(string root) => WeaselEnvironment.WithUserDirectory(root);

    /// <summary>
    /// 重新读盘。必须显式重读：<c>ApplyLineEdits</c> 是逐行手术式写入，
    /// 只改磁盘文本，不回写内存里的 patch 字典（与 AppOptions 页保存后回读同理）。
    /// 直接拿写入前的 CustomYamlFile.Patch 断言，等于在测一份过期数据。
    /// </summary>
    private static CustomYamlFile Reload(string path) => new(path);

    [Fact]
    public void 应用把方案写进补丁且不动用户手写条目()
    {
        using var tmp = new TempDirectory();
        var customPath = tmp.Write("weasel.custom.yaml", """
            # 我手写的注释
            patch:
              style/color_scheme: user_night
              # 这条是我自己加的，面板不能动
              style/font_point: 16

            """);

        var store = new UserColorSchemeStore(Env(tmp.Root));
        var scheme = new UserColorScheme { Id = "user_night", Name = "夜色" };
        scheme.Colors["back_color"] = "0x1a1a2e";
        scheme.Colors["text_color"] = "0xe0e0e0";
        store.Registry.Add(scheme);

        var custom = new CustomYamlFile(customPath);
        var result = store.Apply(custom);

        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.Removed);

        var text = File.ReadAllText(customPath);
        Assert.Contains("# 我手写的注释", text);
        Assert.Contains("# 这条是我自己加的，面板不能动", text);
        Assert.Contains("style/font_point: 16", text);
        Assert.Contains("style/color_scheme: user_night", text);

        // 配色定义确实落地了
        var reloaded = new CustomYamlFile(customPath);
        var extracted = PresetColorSchemes.Extract(reloaded.Patch);
        Assert.True(extracted.ContainsKey("user_night"));
        Assert.Equal("夜色", extracted["user_night"]["name"]);
        Assert.Equal("0x1a1a2e", extracted["user_night"]["back_color"]);
    }

    [Fact]
    public void 删掉的方案会从补丁里真的消失()
    {
        using var tmp = new TempDirectory();
        var customPath = tmp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 16\n");

        var store = new UserColorSchemeStore(Env(tmp.Root));
        foreach (var id in new[] { "user_a", "user_b" })
        {
            var s = new UserColorScheme { Id = id, Name = id };
            s.Colors["back_color"] = "0x101010";
            store.Registry.Add(s);
        }

        var custom = new CustomYamlFile(customPath);
        store.Apply(custom);
        Assert.Equal(2, PresetColorSchemes.Extract(Reload(customPath).Patch).Count);

        // 删掉 user_a 再应用
        Assert.True(store.Registry.Remove("user_a"));
        var result = store.Apply(Reload(customPath));

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Removed);

        var left = PresetColorSchemes.Extract(Reload(customPath).Patch);
        Assert.False(left.ContainsKey("user_a"));
        Assert.True(left.ContainsKey("user_b"));

        // 用户手写的条目仍然健在
        Assert.Contains("style/font_point: 16", File.ReadAllText(customPath));
    }

    [Fact]
    public void 未经面板管理的配色方案一律不动()
    {
        using var tmp = new TempDirectory();
        var customPath = tmp.Write("weasel.custom.yaml", """
            patch:
              preset_color_schemes/my_handmade:
                name: 我手搓的
                back_color: 0x123456

            """);

        var store = new UserColorSchemeStore(Env(tmp.Root));
        // 注册表里什么都没有，但 appliedIds 里混进一个非托管 id
        store.Registry.MarkApplied(new[] { "my_handmade" });

        var custom = new CustomYamlFile(customPath);
        store.Apply(custom);

        var left = PresetColorSchemes.Extract(Reload(customPath).Patch);
        Assert.True(left.ContainsKey("my_handmade"), "不是面板写的方案不能被摘掉");
        Assert.Equal("我手搓的", left["my_handmade"]["name"]);
    }

    [Fact]
    public void 全部清除只摘面板自己写过的()
    {
        using var tmp = new TempDirectory();
        var customPath = tmp.Write("weasel.custom.yaml", """
            patch:
              preset_color_schemes/user_a:
                name: A
              preset_color_schemes/handmade:
                name: 手搓

            """);

        var store = new UserColorSchemeStore(Env(tmp.Root));
        store.Registry.Add(new UserColorScheme { Id = "user_a", Name = "A" });
        store.Registry.MarkApplied(new[] { "user_a" });

        var custom = new CustomYamlFile(customPath);
        store.ClearAll(custom);

        var left = PresetColorSchemes.Extract(Reload(customPath).Patch);
        Assert.False(left.ContainsKey("user_a"));
        Assert.True(left.ContainsKey("handmade"));
        Assert.Empty(store.Registry.Schemes);
    }

    // ── 抽取 ────────────────────────────────────────────────────────────

    [Fact]
    public void 抽取同时认扁平与嵌套写法()
    {
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["preset_color_schemes/user_flat"] = new Dictionary<string, object?>
            {
                ["name"] = "扁平"
            },
            ["preset_color_schemes"] = new Dictionary<string, object?>
            {
                ["user_nested"] = new Dictionary<string, object?> { ["name"] = "嵌套" }
            },
            ["style/font_point"] = 16
        };

        var got = PresetColorSchemes.Extract(patch);
        Assert.Equal(2, got.Count);
        Assert.Equal("扁平", got["user_flat"]["name"]);
        Assert.Equal("嵌套", got["user_nested"]["name"]);
    }

    // ── 导入 / 导出 ─────────────────────────────────────────────────────

    [Fact]
    public void 导入三种形态的yaml()
    {
        using var tmp = new TempDirectory();
        var store = new UserColorSchemeStore(Env(tmp.Root));

        var direct = store.ImportYaml("""
            preset_color_schemes:
              墨色:
                name: 墨色
                back_color: 0x1a1a2e
                text_color: 0xe0e0e0
            """);
        Assert.Equal(1, direct.Added);

        var viaPatch = store.ImportYaml("""
            patch:
              preset_color_schemes:
                黛色:
                  back_color: 0x2e2e1a
            """);
        Assert.Equal(1, viaPatch.Added);

        var bare = store.ImportYaml("""
            晴色:
              back_color: 0xf0f0f0
            """);
        Assert.Equal(1, bare.Added);

        Assert.Equal(3, store.Registry.Schemes.Count);
        // 中文名只进显示名，id 仍是 ASCII
        Assert.Equal("墨色", store.Registry.Get("user_scheme")!.Name);
    }

    [Fact]
    public void 导入把整数颜色归一成字面量()
    {
        using var tmp = new TempDirectory();
        var store = new UserColorSchemeStore(Env(tmp.Root));

        // YAML 1.1 会把 0x1a1a2e 读成整数；存整数下次写出会变成十进制，没法人眼校对
        store.ImportYaml("""
            preset_color_schemes:
              night:
                back_color: 0x1a1a2e
                text_color: 0x80112233
            """);

        var scheme = store.Registry.Get("user_night")!;
        // 6 位整数补 alpha 后仍是 6 位字面量，且字节序不变（abgr 的 BB GG RR）
        Assert.Equal("0x1A1A2E", scheme.Colors["back_color"]);
        // 8 位带 alpha 的必须保住 alpha 段：丢了就变成完全不透明，
        // 「半透明文字」这种效果会直接失效
        Assert.Equal("0x80112233", scheme.Colors["text_color"]);
    }

    [Fact]
    public void 导出再导入可往返()
    {
        using var tmp = new TempDirectory();
        var store = new UserColorSchemeStore(Env(tmp.Root));

        var scheme = new UserColorScheme { Id = "user_night", Name = "夜色", Author = "大狼" };
        scheme.Colors["back_color"] = "0x2E1A1A";
        scheme.Colors["text_color"] = "0xE0E0E0";
        store.Registry.Add(scheme);

        var yaml = UserColorSchemeStore.ExportYaml(store.Registry.Schemes);

        var other = new UserColorSchemeStore(Env(Path.Combine(tmp.Root, "other")));
        var result = other.ImportYaml(yaml);

        Assert.Equal(1, result.Added);
        var got = other.Registry.Schemes[0];
        Assert.Equal("夜色", got.Name);
        Assert.Equal("大狼", got.Author);
        Assert.Equal("0x2E1A1A", got.Colors["back_color"]);
        Assert.Equal("0xE0E0E0", got.Colors["text_color"]);
    }

    [Fact]
    public void 导入忽略无法解析的内容()
    {
        using var tmp = new TempDirectory();
        var store = new UserColorSchemeStore(Env(tmp.Root));

        var result = store.ImportYaml("这不是: [一个, 合法的, 配色文件");
        Assert.Equal(0, result.Added);

        // 无关 YAML 不会被误当成配色（值不全是映射）
        var result2 = store.ImportYaml("style:\n  font_point: 16\n");
        Assert.Equal(0, result2.Added);
    }
}

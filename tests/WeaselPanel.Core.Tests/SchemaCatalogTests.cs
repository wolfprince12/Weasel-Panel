//  SchemaCatalog 测试。
//  用 TempDirectory 准备 fixture，不触碰任何真实用户目录 / 共享目录。

using System;
using System.IO;
using System.Linq;
using WeaselPanel.Core.Rime;
using Xunit;

namespace WeaselPanel.Core.Tests;

public sealed class SchemaCatalogTests
{
    // ── 扫描 ───────────────────────────────────────────────────────────────

    [Fact]
    public void 扫描_共享与用户目录_合并所有方案()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/luna_pinyin.schema.yaml", SchemaYaml("luna_pinyin", "朙月拼音"));
        tmp.Write("shared/rime_ice.schema.yaml", SchemaYaml("rime_ice", "雾凇拼音"));
        tmp.Write("user/my_custom.schema.yaml", SchemaYaml("my_custom", "我的自定义"));

        var cat = SchemaCatalog.Build(userDirectory: tmp.Root + "/user",
                                       sharedDataDirectory: tmp.Root + "/shared");

        Assert.Equal(3, cat.All.Count);
        Assert.Contains("luna_pinyin", cat.All.Keys);
        Assert.Contains("rime_ice", cat.All.Keys);
        Assert.Contains("my_custom", cat.All.Keys);
        Assert.True(cat.All["rime_ice"].IsBuiltIn);
        Assert.False(cat.All["my_custom"].IsBuiltIn);
    }

    [Fact]
    public void 同名方案_用户目录覆盖共享目录()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/x.schema.yaml", SchemaYaml("x", "共享版"));
        tmp.Write("user/x.schema.yaml", SchemaYaml("x", "用户版"));

        var cat = SchemaCatalog.Build(tmp.Root + "/user", tmp.Root + "/shared");

        Assert.Single(cat.All);
        Assert.Equal("用户版", cat.All["x"].Name);
        Assert.False(cat.All["x"].IsBuiltIn);
    }

    [Fact]
    public void 单个文件解析失败_降级为id_only条目_不拖累扫描()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/good.schema.yaml", SchemaYaml("good", "好的"));
        tmp.Write("shared/broken.schema.yaml", ":\n:\n:\n  [invalid yaml");

        var cat = SchemaCatalog.Build(tmp.Root + "/user", tmp.Root + "/shared");

        Assert.Equal(2, cat.All.Count);
        Assert.Equal("好的", cat.All["good"].Name);
        Assert.Contains("broken", cat.All.Keys);
        Assert.Equal("broken（读取失败）", cat.All["broken"].Name);
    }

    [Fact]
    public void 缺目录时不抛异常()
    {
        var cat = SchemaCatalog.Build("/no/such/dir", null);
        Assert.Empty(cat.All);
        Assert.Empty(cat.BaseActiveIds);
    }

    // ── schema_list 抽取 ───────────────────────────────────────────────────

    [Fact]
    public void 默认启用列表_从default_yaml读()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/default.yaml", """
            schema_list:
              - schema: luna_pinyin
              - schema: rime_ice
            """);
        tmp.Write("shared/luna_pinyin.schema.yaml", SchemaYaml("luna_pinyin", "朙月"));
        tmp.Write("shared/rime_ice.schema.yaml", SchemaYaml("rime_ice", "雾凇"));

        var cat = SchemaCatalog.Build("", tmp.Root + "/shared");

        Assert.Equal(new[] { "luna_pinyin", "rime_ice" }, cat.BaseActiveIds);
    }

    [Fact]
    public void 用户覆盖列表_从default_custom_yaml的patch读()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/default.yaml", """
            schema_list:
              - schema: luna_pinyin
            """);
        tmp.Write("user/default.custom.yaml", """
            patch:
              schema_list:
                - schema: rime_ice
                - schema: luna_pinyin
            """);

        var cat = SchemaCatalog.Build(tmp.Root + "/user", tmp.Root + "/shared");

        Assert.Equal(new[] { "luna_pinyin" }, cat.BaseActiveIds);
        Assert.Equal(new[] { "rime_ice", "luna_pinyin" }, cat.CustomActiveIds);
        Assert.True(cat.HasCustomization);
    }

    [Fact]
    public void EffectiveActiveIds_custom为空时用base()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/default.yaml", """
            schema_list:
              - schema: luna_pinyin
              - schema: rime_ice
            """);
        var cat = SchemaCatalog.Build("", tmp.Root + "/shared");

        Assert.False(cat.HasCustomization);
        Assert.Equal(cat.BaseActiveIds, cat.EffectiveActiveIds);
    }

    [Fact]
    public void EffectiveActiveIds_custom存在时用custom_忽略base()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/default.yaml", """
            schema_list:
              - schema: luna_pinyin
              - schema: rime_ice
            """);
        tmp.Write("user/default.custom.yaml", """
            patch:
              schema_list:
                - schema: rime_ice
            """);
        var cat = SchemaCatalog.Build(tmp.Root + "/user", tmp.Root + "/shared");

        Assert.Equal(new[] { "rime_ice" }, cat.EffectiveActiveIds);
    }

    [Fact]
    public void patch中引用了未安装方案_列入孤儿条目()
    {
        using var tmp = new TempDirectory();
        tmp.Write("user/default.custom.yaml", """
            patch:
              schema_list:
                - schema: rime_ice
                - schema: missing_one
            """);
        tmp.Write("shared/rime_ice.schema.yaml", SchemaYaml("rime_ice", "雾凇"));

        var cat = SchemaCatalog.Build(tmp.Root + "/user", tmp.Root + "/shared");

        Assert.Equal(new[] { "missing_one" }, cat.OrphanIds);
    }

    [Fact]
    public void 嵌套schema_id形态也兼容()
    {
        using var tmp = new TempDirectory();
        tmp.Write("user/default.custom.yaml", """
            patch:
              schema_list:
                - schema:
                    id: rime_ice
            """);

        var cat = SchemaCatalog.Build(tmp.Root + "/user", null);

        Assert.Equal(new[] { "rime_ice" }, cat.CustomActiveIds);
    }

    // ── 可启用候选 ─────────────────────────────────────────────────────────

    [Fact]
    public void AvailableToAdd_排除已启用方案_按显示名升序()
    {
        using var tmp = new TempDirectory();
        tmp.Write("shared/default.yaml", """
            schema_list:
              - schema: luna_pinyin
            """);
        tmp.Write("shared/luna_pinyin.schema.yaml", SchemaYaml("luna_pinyin", "朙月"));
        tmp.Write("shared/rime_ice.schema.yaml", SchemaYaml("rime_ice", "雾凇"));
        tmp.Write("shared/zhuyin.schema.yaml", SchemaYaml("zhuyin", "注音"));

        var cat = SchemaCatalog.Build("", tmp.Root + "/shared");

        var names = cat.AvailableToAdd.Select(s => s.Name).ToList();
        Assert.Equal(2, names.Count);
        // 注音/雾凇 按 CurrentCulture 拼音排序，w < z，故雾凇在前
        Assert.Equal(new[] { "雾凇", "注音" }, names);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string SchemaYaml(string id, string name) =>
        $"schema:\n  schema_id: {id}\n  name: {name}\n";
}
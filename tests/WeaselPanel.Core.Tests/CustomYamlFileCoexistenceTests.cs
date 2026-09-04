//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。
//
//  weasel.custom.yaml 同时被三个面板写：
//    · 外观页      → style/*（color_scheme / font_point / layout/type …）
//    · 行为页      → show_notifications / style/paging_on_scroll … 与 default.custom.yaml
//    · 自定义配色页 → preset_color_schemes/<id>
//
//  2026-09-04 排查发现：外观页 ApplyAsync 曾复用 LoadAll 时缓存的 _custom 旧快照做
//  Save()（整文件重写）。当三个面板在同一轮「应用并重新部署」里都脏时，最后一个写的
//  用旧快照覆盖掉了前两个刚写入的键 —— 表现为「改了 A 功能、B 功能配置丢失」。
//
//  本文件锁定契约：每个面板在 apply 时都必须「重新读取磁盘最新态」再写，
//  这样对同一个文件的多次写入互不覆盖。AppearanceViewModel 已改为 apply 时 new
//  CustomYamlFile(path)（与行为页 / 配色页一致）。

using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Tests;

public class CustomYamlFileCoexistenceTests : IDisposable
{
    private readonly string _dir;

    public CustomYamlFileCoexistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wp-coexist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结果 */ }
    }

    private string File_() => Path.Combine(_dir, "weasel.custom.yaml");

    // ── 契约：每次 apply 重新读盘 → 三个面板写入互不覆盖 ──────────────────

    [Fact]
    public void 多面板先后写入同一文件_互不覆盖()
    {
        // 起点：外观页此前已写过 style/color_scheme 与 style/font_point
        File.WriteAllText(File_(),
            "patch:\n  style/color_scheme: aqua\n  style/font_point: 14\n");

        // 1) 自定义配色页：注入 preset_color_schemes/test（用 fresh read）
        {
            var c = new CustomYamlFile(File_());
            var set = new PatchSet();
            set.Set("preset_color_schemes/test",
                PatchValue.Dictionary(new Dictionary<string, object?> { ["name"] = "测试", ["author"] = "panel" }));
            c.ApplyLineEdits(set);
        }

        // 2) 外观页：改 style/font_point（用 fresh read —— 修复后的写法）
        {
            var c = new CustomYamlFile(File_());
            c.Set("style/font_point", 16);
            c.Save();
        }

        // 3) 回读校验：两组的键都还在
        var reread = new CustomYamlFile(File_());
        Assert.Equal("aqua", reread.StringForPath("style/color_scheme"));
        Assert.Equal("16", reread.StringForPath("style/font_point"));
        var preset = reread.ValueForPath("preset_color_schemes/test") as Dictionary<string, object?>;
        Assert.NotNull(preset);
        Assert.Equal("测试", preset!["name"]);
    }

    [Fact]
    public void 行为页与外观页交替写入_互不覆盖()
    {
        File.WriteAllText(File_(), "patch:\n  style/color_scheme: ink\n  show_notifications: true\n");

        // 行为页写 show_notifications_time（fresh read + ApplyLineEdits）
        {
            var c = new CustomYamlFile(File_());
            var set = new PatchSet();
            set.Set("show_notifications_time", PatchValue.Of(3));
            c.ApplyLineEdits(set);
        }

        // 外观页写 style/font_point（fresh read + Save）
        {
            var c = new CustomYamlFile(File_());
            c.Set("style/font_point", 20);
            c.Save();
        }

        var reread = new CustomYamlFile(File_());
        Assert.Equal("ink", reread.StringForPath("style/color_scheme"));      // 没被碰，仍在
        Assert.Equal("true", reread.StringForPath("show_notifications"));    // 行为页的键仍在
        Assert.Equal("3", reread.StringForPath("show_notifications_time"));  // 行为页新写的仍在
        Assert.Equal("20", reread.StringForPath("style/font_point"));        // 外观页新写的仍在
    }

    // ── 对照：若某面板用「旧快照」整文件重写，必覆盖其它面板刚写的键 ────────
    // 这条刻画了 2026-09-04 的 bug 形态，锁死「绝不能用 stale snapshot 写 weasel.custom.yaml」。

    [Fact]
    public void 用旧快照整文件重写_会覆盖其它面板写入的键()
    {
        File.WriteAllText(File_(), "patch:\n  style/color_scheme: aqua\n");

        // ⚠️ 复刻旧 bug：外观页在「启动 LoadAll」时就把 _custom 加载进内存
        // （此刻磁盘上只有 style/color_scheme），之后配色页往同一文件写了
        // preset_color_schemes/test。外观页 apply 时若复用这个旧快照做整文件 Save()，
        // 旧快照的 _patch 里没有 preset_color_schemes，于是把它覆盖丢失。
        var stale = new CustomYamlFile(File_());   // 加载的是「写入之前」的状态

        // 配色页注入 preset_color_schemes/test（fresh read，正确写法）→ 磁盘现在两套键都有
        {
            var c = new CustomYamlFile(File_());
            var set = new PatchSet();
            set.Set("preset_color_schemes/test",
                PatchValue.Dictionary(new Dictionary<string, object?> { ["name"] = "测试" }));
            c.ApplyLineEdits(set);
        }

        // 外观页用旧快照改 style/font_point 并整文件重写
        stale.Set("style/font_point", 16);
        stale.Save();                               // 旧快照 _patch 里不含 preset_color_schemes

        var reread = new CustomYamlFile(File_());
        Assert.Equal("16", reread.StringForPath("style/font_point"));
        // 旧 bug：preset_color_schemes 被覆盖丢失
        Assert.Null(reread.ValueForPath("preset_color_schemes/test"));
        // 这正是为什么 AppearanceViewModel 必须在 apply 时重新读盘，而不能复用 stale 快照。
    }
}

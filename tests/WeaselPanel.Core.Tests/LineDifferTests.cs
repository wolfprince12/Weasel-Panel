//
//  LineDifferTests.cs
//  WeaselPanel.Core.Tests
//
//  LCS 行级 diff 的行为测试。全部为纯函数测试，不触碰文件系统。
//

namespace WeaselPanel.Core.Tests;

public class LineDifferTests
{
    [Fact]
    public void 两版相同则全部为相等行()
    {
        string[] a = ["alpha", "beta", "gamma"];
        var result = LineDiffer.Diff(a, a);

        Assert.Equal(3, result.Count);
        Assert.All(result, line => Assert.Equal(DiffKind.Equal, line.Kind));
    }

    [Fact]
    public void 两边都为空时结果为空()
    {
        Assert.Empty(LineDiffer.Diff([], []));
    }

    [Fact]
    public void 纯新增只产生新增行()
    {
        string[] a = ["keep"];
        string[] b = ["keep", "new1", "new2"];

        var result = LineDiffer.Diff(a, b);

        Assert.Equal(DiffKind.Equal, result[0].Kind);
        Assert.Equal(DiffKind.Added, result[1].Kind);
        Assert.Equal(DiffKind.Added, result[2].Kind);
        Assert.Equal(["new1", "new2"], result.Skip(1).Select(l => l.Text));
    }

    [Fact]
    public void 纯删除只产生删除行()
    {
        string[] a = ["gone1", "gone2", "keep"];
        string[] b = ["keep"];

        var result = LineDiffer.Diff(a, b);

        Assert.Equal(3, result.Count);
        Assert.Equal(DiffKind.Removed, result[0].Kind);
        Assert.Equal(DiffKind.Removed, result[1].Kind);
        Assert.Equal(DiffKind.Equal, result[2].Kind);
    }

    [Fact]
    public void 修改一行表现为先删除后新增()
    {
        string[] a = ["alpha", "old", "omega"];
        string[] b = ["alpha", "new", "omega"];

        var result = LineDiffer.Diff(a, b);

        Assert.Equal(
            [DiffKind.Equal, DiffKind.Removed, DiffKind.Added, DiffKind.Equal],
            result.Select(l => l.Kind));
        Assert.Equal("old", result[1].Text);
        Assert.Equal("new", result[2].Text);
    }

    [Fact]
    public void LCS保证不把公共行误判为改动()
    {
        // 经典的「中间插入一行」场景：若算法退化成按行号比对，
        // 会把插入点之后的所有行都判为改动。
        string[] a = ["1", "2", "3", "4"];
        string[] b = ["1", "X", "2", "3", "4"];

        var result = LineDiffer.Diff(a, b);

        Assert.Single(result.Where(l => l.Kind == DiffKind.Added));
        Assert.Empty(result.Where(l => l.Kind == DiffKind.Removed));
        Assert.Equal(4, result.Count(l => l.Kind == DiffKind.Equal));
    }

    [Fact]
    public void 双栏diff的行号从1开始且删除行右栏为空()
    {
        string[] a = ["alpha", "removed", "beta"];
        string[] b = ["alpha", "beta"];

        var result = LineDiffer.DiffSideBySide(a, b);

        Assert.Equal(3, result.Count);
        Assert.Equal((1, 1), (result[0].LeftNo, result[0].RightNo));
        Assert.Equal(DiffKind.Equal, result[0].Kind);

        Assert.Equal(2, result[1].LeftNo);
        Assert.Null(result[1].RightNo);
        Assert.Equal(DiffKind.Removed, result[1].Kind);
        Assert.Equal("removed", result[1].LeftText);
        Assert.Equal(string.Empty, result[1].RightText);

        Assert.Equal((3, 2), (result[2].LeftNo, result[2].RightNo));
    }

    [Fact]
    public void 双栏diff中新增行左栏为空()
    {
        string[] a = ["alpha"];
        string[] b = ["alpha", "inserted"];

        var result = LineDiffer.DiffSideBySide(a, b);

        Assert.Null(result[1].LeftNo);
        Assert.Equal(2, result[1].RightNo);
        Assert.Equal("inserted", result[1].RightText);
        Assert.Equal(string.Empty, result[1].LeftText);
    }

    [Theory]
    [InlineData("a\nb\nc")]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\rb\rc")]
    public void 行切分兼容LF与CRLF与CR(string text)
    {
        Assert.Equal(["a", "b", "c"], LineSplitter.Split(text));
    }

    [Fact]
    public void 行切分保留空行()
    {
        Assert.Equal(["a", "", "c"], LineSplitter.Split("a\n\nc"));
    }

    [Fact]
    public void 空文本切分出单个空行()
    {
        // 与 Swift 的 split(omittingEmptySubsequences: false) 一致
        Assert.Equal([""], LineSplitter.Split(""));
    }

    [Fact]
    public void 连接一律使用LF()
    {
        Assert.Equal("a\nb\nc", LineSplitter.Join(["a", "b", "c"]));
    }
}

//
//  ReleaseUpdateCheckerTests.cs
//  WeaselPanel.Core.Tests
//
//  ReleaseUpdateChecker 的可单测子集：版本段比较 + 状态机纯函数部分。
// 完整的 GitHub 拉取链路已在 GitHubMirrorFetchTests 里覆盖（用桩处理器），本测试
//  只关心版本号段比较的正确性——这是「是否报『有更新』」的唯一决定因素。
//

using System.Net;
using WeaselPanel.Core.Net;

namespace WeaselPanel.Core.Tests;

public class ReleaseUpdateCheckerTests
{
    // ── CompareVersion：segment-by-segment 比较，缺位补 0 ──

    [Theory]
    [InlineData("0.2.9", "0.2.9", false)]   // 完全相等 → 不是更高
    [InlineData("0.2.9", "0.2.10", true)]   // 高段赢（数字按段比对，不是字典序）
    [InlineData("0.2.9", "0.3.0", true)]
    [InlineData("0.2.9", "1.0.0", true)]
    [InlineData("0.2.9", "0.2.9.0", false)] // 缺位补 0 → 实际相等
    [InlineData("0.2.9", "0.2.9.1", true)]  // build > 0
    [InlineData("1.0",   "1.0.1", true)]    // 缺位补 0 的关键场景（输入 macOS 上常见简写）
    [InlineData("1.0.1", "1.0",   false)]   // 反向不应误报
    [InlineData("0.9.99","0.10.0", true)]   // 数字段比字符串字典序比较（关键修正：0.10 > 0.9，绝不能按字符串比）
    public void CompareVersion_ReturnsTrueOnlyWhenRemoteIsStrictlyNewer(
        string current, string remote, bool expected)
    {
        var actual = typeof(ReleaseUpdateChecker)
            .GetMethod("CompareVersion", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [current, remote]);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("",      "0.2.10", false)] // 空 local → 永远判无更新（保守降级）
    [InlineData("0.2.9", "",      false)] // 空 remote → 也判无更新
    [InlineData("0.2.x", "0.2.10", false)] // 含非数字段 → 解析失败，返回 false
    public void CompareVersion_MalformedInputs_AreTreatedAsFalse(string current, string remote, bool expected)
    {
        var actual = typeof(ReleaseUpdateChecker)
            .GetMethod("CompareVersion", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [current, remote]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CompareVersion_DoesNot_StripPrefix()
    {
        // v 前缀剥离是 CheckAsync 公共路径的职责（拿 raw tag 后用 StripPrefixV 处理），
        // CompareVersion 本身接收「已经剥过前缀」的两个串，再做段比较。
        // 若 raw 带 v 仍能传进来，"v0.2.10" 不是合法段 → TryParse 失败 → CompareVersion 返回 false。
        var actual = typeof(ReleaseUpdateChecker)
            .GetMethod("CompareVersion", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, ["0.2.9", "v0.2.10"]);
        Assert.Equal(false, actual); // 失败的解析 ≠ false 真值，但不代表 remote > current
    }

    // ── 集成：从 ctor 拉取 → checker 状态应被填充 ──

    [Fact]
    public async Task CheckAsync_OnSuccess_ReportsRemoteVersionAndHtmlUrl()
    {
        // 一个永远返回固定 JSON 的桩 fetch，直接绕开 git mirror，校验状态机本身。
        var fetch = new GitHubMirrorFetch(new StubHandler(_ => Stub.Json(
            """{"tag_name":"v0.2.10","html_url":"https://github.com/x/y/releases/tag/v0.2.10"}""")));

        var checker = new ReleaseUpdateChecker(fetch, () => "0.2.9", "x/y");

        await checker.CheckAsync();

        Assert.Equal(UpdateCheckState.Available, checker.State);
        Assert.Equal("0.2.10", checker.LatestVersion); // StripPrefixV 生效
        Assert.Equal("https://github.com/x/y/releases/tag/v0.2.10", checker.HtmlUrl);
    }

    [Fact]
    public async Task CheckAsync_HandlesBareTagWithoutVPrefix()
    {
        // 部分 release 仓库（如 alphav 分支打 tag 不带 v）的 tag_name 就是裸版本号，
        // StripPrefixV 在该输入上是 no-op，应自然走段比较。
        var fetch = new GitHubMirrorFetch(new StubHandler(_ => Stub.Json(
            """{"tag_name":"0.2.11","html_url":"https://github.com/x/y/releases/tag/0.2.11"}""")));

        var checker = new ReleaseUpdateChecker(fetch, () => "0.2.10", "x/y");

        await checker.CheckAsync();

        Assert.Equal(UpdateCheckState.Available, checker.State);
        Assert.Equal("0.2.11", checker.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenFetchFails_TransitionsToFailed()
    {
        // StubHandler 一律返回 500 → 全部候选 URL 都失败
        var fetch = new GitHubMirrorFetch(new StubHandler(_ => Stub.Status(HttpStatusCode.InternalServerError)));

        var checker = new ReleaseUpdateChecker(fetch, () => "0.2.9", "x/y");

        await checker.CheckAsync();

        Assert.Equal(UpdateCheckState.Failed, checker.State);
        Assert.Null(checker.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_WhenLocalVersionUnknown_DoesNotFalselyReportUpdate()
    {
        var fetch = new GitHubMirrorFetch(new StubHandler(_ => Stub.Json(
            """{"tag_name":"v99.0.0","html_url":"https://github.com/x/y/releases/tag/v99.0.0"}""")));

        // 当前版本提供器返回空 → 应该安全降级到 UpToDate，而不是误报成 Available
        var checker = new ReleaseUpdateChecker(fetch, () => string.Empty, "x/y");

        await checker.CheckAsync();

        Assert.Equal(UpdateCheckState.UpToDate, checker.State);
        Assert.Equal("99.0.0", checker.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_IsCancellable()
    {
        // 永远挂起的异步桩处理器（cancellation-aware）——
        // 不能复用 GitHubMirrorFetchTests 里的 StubHandler：它内部走 tcs + GetResult()，
        // 取消链不会传进去，外层 CheckAsync 会永久 await，挂死整个测试集。
        var fetch = new GitHubMirrorFetch(new HangingHandler());

        var checker = new ReleaseUpdateChecker(fetch, () => "0.2.9", "x/y");

        using var cts = new CancellationTokenSource();
        var checkTask = checker.CheckAsync(cts.Token);
        cts.Cancel();

        await checkTask; // checker 捕获 OperationCanceledException → 回到 Idle
        Assert.Equal(UpdateCheckState.Idle, checker.State);
    }

    /// <summary>永远挂起、对 cancellation 立即敏感的桩。专用于 cancellation 测试。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

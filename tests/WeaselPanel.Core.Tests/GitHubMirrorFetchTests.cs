//
//  GitHubMirrorFetchTests.cs
//  WeaselPanel.Core.Tests
//
//  镜像 fallback 的行为测试。**全程不联网**：通过注入 HttpMessageHandler 桩
//  模拟直连失败、镜像成功、全部失败、zip 签名错误等各种情形。
//

using System.Net;
using System.Text;

namespace WeaselPanel.Core.Tests;

/// <summary>记录所有请求并按委托返回响应的桩处理器。</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<string> Requested { get; } = [];

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri?.ToString() ?? string.Empty);
        // 只调用一次 responder：它有可能是带状态的委托，重复调用会污染计数
        var response = _responder(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

internal static class Stub
{
    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(data),
    };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

public class GitHubMirrorFetchTests
{
    // MARK: - 纯函数：URL 生成

    [Theory]
    [InlineData("https://github.com/rime/weasel", true)]
    [InlineData("https://api.github.com/repos/rime/weasel", true)]
    [InlineData("https://raw.githubusercontent.com/rime/weasel/master/README.md", true)]
    [InlineData("https://gitee.com/rime/weasel", false)]
    [InlineData("not a url", false)]
    public void 识别GitHub域名(string url, bool expected) =>
        Assert.Equal(expected, GitHubMirrorFetch.IsGitHubUrl(url));

    [Fact]
    public void 镜像URL拼接不产生双斜杠()
    {
        Assert.Equal(
            "https://ghproxy.com/https://github.com/rime/weasel",
            GitHubMirrorFetch.MirroredUrl("https://github.com/rime/weasel", "https://ghproxy.com/"));

        Assert.Equal(
            "https://ghp.ci/https://github.com/rime/weasel",
            GitHubMirrorFetch.MirroredUrl("https://github.com/rime/weasel", "https://ghp.ci"));
    }

    [Fact]
    public void 候选URL以原始URL开头()
    {
        var candidates = GitHubMirrorFetch.CandidateUrls("https://github.com/rime/weasel");

        Assert.Equal("https://github.com/rime/weasel", candidates[0]);
        Assert.Equal(1 + GitHubMirrorFetch.MirrorPrefixes.Length, candidates.Count);
    }

    [Fact]
    public void 镜像优先候选把原始URL放到最后()
    {
        var candidates = GitHubMirrorFetch.MirrorFirstCandidates("https://api.github.com/repos/rime/weasel");

        Assert.Equal("https://api.github.com/repos/rime/weasel", candidates[^1]);
        Assert.StartsWith(GitHubMirrorFetch.MirrorPrefixes[0], candidates[0]);
    }

    [Fact]
    public void 非GitHub地址不做镜像包裹()
    {
        Assert.Equal(["https://example.com/a"], GitHubMirrorFetch.CandidateUrls("https://example.com/a"));
        Assert.Equal(["https://example.com/a"], GitHubMirrorFetch.MirrorFirstCandidates("https://example.com/a"));
    }

    // MARK: - 纯函数：解析

    [Theory]
    [InlineData("https://github.com/rime/weasel/releases/tag/0.17.4", "0.17.4")]
    [InlineData("https://ghproxy.com/https://github.com/rime/weasel/releases/tag/0.17.4", "0.17.4")]
    [InlineData("https://github.com/rime/weasel/releases/tag/v1.2.3-beta", "v1.2.3-beta")]
    [InlineData("https://github.com/rime/weasel/releases/latest", null)]
    [InlineData("https://example.com/x", null)]
    public void 从release地址解析tag(string url, string? expected) =>
        Assert.Equal(expected, GitHubMirrorFetch.ParseTagFromReleaseUrl(url));

    [Fact]
    public void 从commit页面解析完整40位SHA()
    {
        var sha = new string('a', 40);
        var html = $"<a href=\"/rime/weasel/commit/{sha}\">link</a>";

        Assert.Equal(sha, GitHubMirrorFetch.ParseShaFromCommitPage(html, "rime", "weasel"));
    }

    [Fact]
    public void 短于40位的SHA不予采信()
    {
        // 与 macOS 版一致：只认完整 40 位，避免解析到页面上其他短哈希
        var html = "<a href=\"/rime/weasel/commit/abc1234\">link</a>";
        Assert.Null(GitHubMirrorFetch.ParseShaFromCommitPage(html, "rime", "weasel"));
    }

    [Fact]
    public void 其他仓库的commit链接不会被误匹配()
    {
        var sha = new string('b', 40);
        var html = $"<a href=\"/other/repo/commit/{sha}\">link</a>";
        Assert.Null(GitHubMirrorFetch.ParseShaFromCommitPage(html, "rime", "weasel"));
    }

    // MARK: - 网络：GET 与镜像回退

    [Fact]
    public async Task 直连成功则不再尝试镜像()
    {
        var handler = new StubHandler(_ => Stub.Text("ok"));
        var fetch = new GitHubMirrorFetch(handler);

        var result = await fetch.FetchAsync("https://raw.githubusercontent.com/rime/weasel/master/README.md");

        Assert.Equal("ok", Encoding.UTF8.GetString(result.Data));
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task 直连失败时回退到镜像()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Host == "raw.githubusercontent.com"
                ? Stub.Status(HttpStatusCode.Forbidden)      // 直连被墙
                : Stub.Text("via mirror"));
        var fetch = new GitHubMirrorFetch(handler);

        var result = await fetch.FetchAsync("https://raw.githubusercontent.com/rime/weasel/master/README.md");

        Assert.Equal("via mirror", Encoding.UTF8.GetString(result.Data));
        Assert.Equal(2, handler.Requested.Count);
        Assert.StartsWith(GitHubMirrorFetch.MirrorPrefixes[0], result.UsedUrl);
    }

    [Fact]
    public async Task 全部候选失败时抛出异常()
    {
        var handler = new StubHandler(_ => Stub.Status(HttpStatusCode.ServiceUnavailable));
        var fetch = new GitHubMirrorFetch(handler);

        await Assert.ThrowsAsync<GitHubMirrorFetchException>(
            () => fetch.FetchAsync("https://github.com/rime/weasel/zipball/master"));

        // 原始 URL + 全部镜像都应被尝试过
        Assert.Equal(1 + GitHubMirrorFetch.MirrorPrefixes.Length, handler.Requested.Count);
    }

    [Fact]
    public async Task 非2xx状态码携带具体URL信息()
    {
        var handler = new StubHandler(_ => Stub.Status(HttpStatusCode.NotFound));
        var fetch = new GitHubMirrorFetch(handler);

        var ex = await Assert.ThrowsAsync<GitHubMirrorFetchException>(
            () => fetch.FetchAsync("https://github.com/rime/weasel/zipball/master"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("github.com", ex.Url);
    }

    // MARK: - 网络：下载与 zip 校验

    [Fact]
    public async Task 下载zip时校验文件签名()
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02];
        var handler = new StubHandler(_ => Stub.Bytes(zip));
        var fetch = new GitHubMirrorFetch(handler);

        using var temp = new TempDirectory();
        var dest = Path.Combine(temp.Root, "pkg.zip");
        await fetch.DownloadAsync("https://github.com/rime/weasel/archive/master.zip", dest);

        Assert.Equal(zip, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public async Task zip签名不符时拒绝写入()
    {
        // 镜像常见故障：返回 HTML 错误页但状态码 200。
        // 若不校验签名，用户会拿到一个打不开的「zip」。
        var handler = new StubHandler(_ => Stub.Text("<!DOCTYPE html><html>error</html>"));
        var fetch = new GitHubMirrorFetch(handler);

        using var temp = new TempDirectory();
        var dest = Path.Combine(temp.Root, "pkg.zip");

        await Assert.ThrowsAsync<GitHubMirrorFetchException>(
            () => fetch.DownloadAsync("https://github.com/rime/weasel/archive/master.zip", dest));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".tmp"));   // 临时文件必须被清理
    }

    [Fact]
    public async Task 非zip文件不做签名校验()
    {
        var handler = new StubHandler(_ => Stub.Text("plain content"));
        var fetch = new GitHubMirrorFetch(handler);

        using var temp = new TempDirectory();
        var dest = Path.Combine(temp.Root, "data.txt");
        await fetch.DownloadAsync("https://github.com/rime/weasel/raw/master/data.txt", dest);

        Assert.Equal("plain content", File.ReadAllText(dest));
    }

    // MARK: - 网络：Release / Commit

    [Fact]
    public async Task 获取最新Release走API回退路径()
    {
        // HEAD 快速路径全部失败（返回 404）→ 应回退到 Releases API
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Head
                ? Stub.Status(HttpStatusCode.NotFound)
                : Stub.Json("{\"tag_name\":\"0.17.5\",\"html_url\":\"https://github.com/rime/weasel/releases/tag/0.17.5\"}"));
        var fetch = new GitHubMirrorFetch(handler);

        var release = await fetch.FetchLatestReleaseAsync("rime/weasel");

        Assert.Equal("0.17.5", release.Tag);
        Assert.Equal("https://github.com/rime/weasel/releases/tag/0.17.5", release.HtmlUrl);
        Assert.Contains("api.github.com", release.UsedUrl);
    }

    [Fact]
    public async Task 获取最新Release时镜像优先()
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Head
                ? Stub.Status(HttpStatusCode.NotFound)
                : Stub.Json("{\"tag_name\":\"0.17.5\"}"));
        var fetch = new GitHubMirrorFetch(handler);

        var release = await fetch.FetchLatestReleaseAsync("rime/weasel");

        Assert.StartsWith(GitHubMirrorFetch.MirrorPrefixes[0], release.UsedUrl);
    }

    [Fact]
    public async Task Release全部路径失败时抛出异常()
    {
        var handler = new StubHandler(_ => Stub.Status(HttpStatusCode.NotFound));
        var fetch = new GitHubMirrorFetch(handler);

        await Assert.ThrowsAsync<GitHubMirrorFetchException>(
            () => fetch.FetchLatestReleaseAsync("rime/weasel"));
    }

    [Fact]
    public async Task 获取最新commit解析JSON数组()
    {
        var sha = new string('c', 40);
        var handler = new StubHandler(request =>
            request.RequestUri!.ToString().Contains("/commits?sha=")
                ? Stub.Json($"[{{\"sha\":\"{sha}\",\"commit\":{{\"author\":{{\"date\":\"2026-08-18T00:00:00Z\"}}}}}}]")
                : Stub.Status(HttpStatusCode.NotFound));
        var fetch = new GitHubMirrorFetch(handler);

        var commit = await fetch.FetchLatestCommitAsync("rime", "weasel", "master");

        Assert.Equal(sha, commit.Sha);
        Assert.NotNull(commit.Date);
        Assert.Equal(2026, commit.Date!.Value.Year);
    }

    [Fact]
    public async Task API失败时从commits页面解析SHA()
    {
        var sha = new string('d', 40);
        var handler = new StubHandler(request =>
            request.RequestUri!.ToString().Contains("/commits?sha=")
                ? Stub.Status(HttpStatusCode.Forbidden)
                : Stub.Text($"<html><a href=\"/rime/weasel/commit/{sha}\">x</a></html>"));
        var fetch = new GitHubMirrorFetch(handler);

        var commit = await fetch.FetchLatestCommitAsync("rime", "weasel", "master");

        Assert.Equal(sha, commit.Sha);
        Assert.Null(commit.Date);   // 页面路径解析不出日期
    }

    // MARK: - Content-Length

    [Fact]
    public async Task 取远端文件大小用于更新判定()
    {
        var handler = new StubHandler(request =>
        {
            var response = Stub.Status(HttpStatusCode.OK);
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentLength = 123456;
            return response;
        });
        var fetch = new GitHubMirrorFetch(handler);

        var length = await fetch.ContentLengthAsync(["https://github.com/rime/weasel/releases/download/x/y.gram"]);

        Assert.Equal(123456, length);
    }

    [Fact]
    public async Task 取不到文件大小时返回null而不是抛异常()
    {
        var handler = new StubHandler(_ => Stub.Status(HttpStatusCode.NotFound));
        var fetch = new GitHubMirrorFetch(handler);

        Assert.Null(await fetch.ContentLengthAsync(["https://github.com/rime/weasel/releases/download/x/y.gram"]));
    }
}

//
//  GitHubMirrorFetch.cs
//  WeaselPanel.Core
//
//  GitHub 网络请求镜像 fallback。中国大陆用户直连 GitHub 常被 403/超时/SSL 错误拦截，
//  本工具先尝试直连，失败后再逐个尝试公共镜像，任一成功即返回。
//
//  策略：
//    1. 普通 GET / 下载：原始 URL → 镜像 URL
//    2. Release 最新版本：HEAD releases/latest（从重定向解析 tag）
//                        → 镜像 API（JSON tag_name）
//                        → 镜像 release 页面（从 final URL 解析 tag）
//    3. Commit 最新 SHA：API 直连 → 镜像 API → commits 页面 HTML 解析
//
//  ── 相对 macOS 版的差异 ──
//
//  1) 原实现是全局静态（依赖 URLSession.shared），无法注入。
//     此处改为实例类并允许注入 HttpMessageHandler，使全部逻辑可在不联网的
//     单元测试中用桩处理器验证（含镜像回退顺序、失败重试、zip 签名校验）。
//     纯函数部分（镜像 URL 生成、tag/SHA 解析）仍是静态，便于单独测试。
//
//  2) User-Agent 改为 WeaselPanel/1.0.0（原为 SquirrelPanel/1.0.0）。
//
//  3) 超时单位：Swift 用秒（TimeInterval），C# 统一用秒（int），
//     内部转成 TimeSpan 设置到 HttpClient.Timeout 上。
//

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeaselPanel.Core.Net;

/// <summary>一次成功请求的返回。</summary>
public sealed record FetchResult(byte[] Data, int StatusCode, string UsedUrl);

/// <summary>Release 最新版本信息。</summary>
public sealed record LatestRelease(string Tag, string? HtmlUrl, string UsedUrl);

/// <summary>分支最新提交信息。</summary>
public sealed record LatestCommit(string Sha, DateTimeOffset? Date, string UsedUrl);

/// <summary>GitHub 请求失败。</summary>
public sealed class GitHubMirrorFetchException : Exception
{
    public int StatusCode { get; }
    public string Url { get; }

    public GitHubMirrorFetchException(int statusCode, string url)
        : base($"HTTP {statusCode}: {url}")
    {
        StatusCode = statusCode;
        Url = url;
    }

    public GitHubMirrorFetchException(string message) : base(message)
    {
        StatusCode = 0;
        Url = string.Empty;
    }
}

/// <summary>
/// GitHub 网络请求镜像 fallback。
/// </summary>
public sealed class GitHubMirrorFetch
{
    /// <summary>内置的 GitHub 镜像前缀列表（按优先级）。把原始 URL 拼到前缀后即可访问。</summary>
    /// <remarks>
    /// 2026-09 实测现状：ghproxy.com / mirror.ghproxy.com / github.moeyy.xyz / ghp.ci 均返回 502；
    /// gh.api.99988866.xyz 只代理 api.github.com，不代理 release asset，对下载无意义。
    /// 唯一实测可用的公共代理是 gh-proxy.com，故仅保留它。镜像会漂移，后续若失效需重新 curl 实测。
    /// </remarks>
    public static readonly string[] MirrorPrefixes =
    [
        "https://gh-proxy.com/",
    ];

    private const string UserAgent = "WeaselPanel/1.0.0";

    private readonly HttpClient _http;

    /// <param name="handler">可选的消息处理器，用于测试注入桩。</param>
    public GitHubMirrorFetch(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    // MARK: - 纯函数：URL 生成与解析

    /// <summary>判断某个 URL 是否指向 GitHub 域名（含 api.github.com / raw.githubusercontent.com）。</summary>
    public static bool IsGitHubUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return host.EndsWith("github.com", StringComparison.Ordinal)
            || host.EndsWith("githubusercontent.com", StringComparison.Ordinal);
    }

    /// <summary>把原始 GitHub URL 用指定镜像前缀包裹，生成镜像 URL 字符串。</summary>
    public static string MirroredUrl(string original, string prefix)
    {
        var trimmed = original.Trim();
        return prefix.EndsWith('/') ? prefix + trimmed : prefix + "/" + trimmed;
    }

    /// <summary>生成候选 URL 列表：原始 URL + 各个镜像 URL。</summary>
    public static List<string> CandidateUrls(string original)
    {
        var result = new List<string> { original };
        if (IsGitHubUrl(original))
        {
            result.AddRange(MirrorPrefixes.Select(p => MirroredUrl(original, p)));
        }
        return result;
    }

    /// <summary>
    /// 生成候选 URL 列表：镜像前缀在前、原始 URL 在后。
    /// 国内用户优先命中快速镜像，避免直连 api.github.com 长时间超时。
    /// </summary>
    public static List<string> MirrorFirstCandidates(string original)
    {
        if (!IsGitHubUrl(original)) return [original];
        var result = MirrorPrefixes.Select(p => MirroredUrl(original, p)).ToList();
        result.Add(original);
        return result;
    }

    /// <summary>
    /// 从 release 页面 final URL 中解析 tag，例如：
    /// https://github.com/rime/weasel/releases/tag/1.0.2  → 1.0.2
    /// https://ghproxy.com/https://github.com/rime/weasel/releases/tag/1.0.2  → 1.0.2
    /// </summary>
    public static string? ParseTagFromReleaseUrl(string urlString)
    {
        var match = ReleaseTagPattern.Match(urlString);
        if (!match.Success) return null;
        var tag = match.Groups[1].Value;
        return tag.Length == 0 ? null : tag;
    }

    private static readonly Regex ReleaseTagPattern = new(
        @"github\.com/[^/]+/[^/]+/releases/tag/([^/?#]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 从 GitHub commit 页面 HTML 中解析最新 commit SHA。
    /// 匹配形如 href="/{owner}/{repo}/commit/abcdef123..." 的链接。
    /// </summary>
    public static string? ParseShaFromCommitPage(string html, string owner, string repo)
    {
        var pattern = new Regex(
            "href=\"/?" + Regex.Escape(owner) + "/" + Regex.Escape(repo) + "/commit/([a-fA-F0-9]{7,40})\"",
            RegexOptions.Compiled);
        var match = pattern.Match(html);
        if (!match.Success) return null;
        var sha = match.Groups[1].Value;
        return sha.Length == 40 ? sha : null;
    }

    // MARK: - 网络请求

    /// <summary>
    /// 使用 GET 请求获取数据，自动按候选 URL fallback。
    /// 所有候选均失败时抛出最后一个错误。
    /// </summary>
    public async Task<FetchResult> FetchAsync(
        string originalUrl,
        IReadOnlyDictionary<string, string>? headers = null,
        int timeoutSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        var candidates = CandidateUrls(originalUrl);
        if (candidates.Count == 0) throw new GitHubMirrorFetchException("无效的 URL");

        Exception? lastError = null;
        foreach (var urlString in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
                ApplyHeaders(request, headers);
                var (data, status, finalUrl) = await SendAsync(request, timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                if (status is >= 200 and <= 299)
                {
                    return new FetchResult(data, status, finalUrl);
                }
                // 403/401 等通常意味着该镜像也被限流或不可用，继续下一个候选
                lastError = new GitHubMirrorFetchException(status, urlString);
            }
            catch (GitHubMirrorFetchException e) { lastError = e; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException e) { lastError = e; }
            catch (TaskCanceledException e) { lastError = e; }
        }

        throw lastError ?? new GitHubMirrorFetchException("无法连接到任何候选地址");
    }

    /// <summary>
    /// 用 HEAD 请求获取远程文件大小（Content-Length），自动按候选 URL fallback。
    /// 用于语法模型的「有更新」判定：比对远程 .gram 大小与本地记录。
    /// 全部失败返回 null。
    /// </summary>
    public async Task<long?> ContentLengthAsync(
        IEnumerable<string> urls,
        int timeoutSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        foreach (var urlString in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, urlString);
                ApplyHeaders(request, null);
                var response = await SendRawAsync(request, timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                using (response)
                {
                    if ((int)response.StatusCode is < 200 or > 399) continue;
                    if (response.Content.Headers.ContentLength is { } len && len > 0) return len;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { continue; }
            catch (TaskCanceledException) { continue; }
        }
        return null;
    }

    /// <summary>
    /// 获取 GitHub Release 最新版本信息：
    /// 1) HEAD releases/latest 重定向页，从 302 Location / 最终 URL 直接解析 tag（不下载正文）；
    /// 2) 失败再回退到 Releases API（JSON）；
    /// 3) 仍失败再回退到 release 页面 GET。
    /// 后两级均「镜像优先」。
    /// </summary>
    public async Task<LatestRelease> FetchLatestReleaseAsync(
        string repo,
        CancellationToken cancellationToken = default)
    {
        var pageUrl = $"https://github.com/{repo}/releases/latest";
        var apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        const int fastTimeout = 8;

        // 1) HEAD 快速路径：国内镜像优先，仅取重定向后的 tag，不下载正文。
        foreach (var urlString in MirrorFirstCandidates(pageUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = await HeadReleaseTagAsync(urlString, fastTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (tag is not null)
            {
                return new LatestRelease(tag, $"https://github.com/{repo}/releases/tag/{tag}", urlString);
            }
        }

        Exception? lastError = null;

        // 2) API 回退：镜像优先，解析 JSON 中的 tag_name。
        foreach (var urlString in MirrorFirstCandidates(apiUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
                ApplyHeaders(request, new Dictionary<string, string>
                {
                    ["Accept"] = "application/vnd.github+json",
                });
                var (data, status, _) = await SendAsync(request, fastTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (status is < 200 or > 299)
                {
                    lastError = new GitHubMirrorFetchException(status, urlString);
                    continue;
                }
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                    && tagEl.ValueKind == JsonValueKind.String)
                {
                    var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var htmlEl)
                        && htmlEl.ValueKind == JsonValueKind.String ? htmlEl.GetString() : null;
                    return new LatestRelease(tagEl.GetString()!, htmlUrl, urlString);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (JsonException e) { lastError = e; }
            catch (GitHubMirrorFetchException e) { lastError = e; }
            catch (HttpRequestException e) { lastError = e; }
            catch (TaskCanceledException e) { lastError = e; }
        }

        // 3) release 页面 GET 回退（镜像优先）。
        foreach (var urlString in MirrorFirstCandidates(pageUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
                ApplyHeaders(request, null);
                var (_, status, finalUrl) = await SendAsync(request, fastTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (status is < 200 or > 299) continue;
                if (ParseTagFromReleaseUrl(finalUrl) is { } tag)
                {
                    return new LatestRelease(tag, finalUrl, urlString);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (GitHubMirrorFetchException) { continue; }
            catch (HttpRequestException) { continue; }
            catch (TaskCanceledException) { continue; }
        }

        throw lastError ?? new GitHubMirrorFetchException("未能获取 Release 信息");
    }

    /// <summary>
    /// 获取 GitHub 仓库某分支的最新 commit。
    /// 先尝试 API（镜像优先 + 直连兜底），失败后再从 commits 页面 HTML 解析 SHA。
    /// </summary>
    public async Task<LatestCommit> FetchLatestCommitAsync(
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/commits?sha={branch}&per_page=1";
        Exception? apiLastError = null;

        foreach (var urlString in MirrorFirstCandidates(apiUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
                ApplyHeaders(request, new Dictionary<string, string>
                {
                    ["Accept"] = "application/vnd.github+json",
                });
                var (data, status, _) = await SendAsync(request, 8, cancellationToken).ConfigureAwait(false);
                if (status is < 200 or > 299)
                {
                    apiLastError = new GitHubMirrorFetchException(status, urlString);
                    continue;
                }
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.ValueKind == JsonValueKind.Array
                    && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    if (first.TryGetProperty("sha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String)
                    {
                        DateTimeOffset? date = null;
                        if (first.TryGetProperty("commit", out var commitEl)
                            && commitEl.TryGetProperty("author", out var authorEl)
                            && authorEl.TryGetProperty("date", out var dateEl)
                            && DateTimeOffset.TryParse(dateEl.GetString(), out var parsed))
                        {
                            date = parsed;
                        }
                        return new LatestCommit(shaEl.GetString()!, date, urlString);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (JsonException e) { apiLastError = e; }
            catch (GitHubMirrorFetchException e) { apiLastError = e; }
            catch (HttpRequestException e) { apiLastError = e; }
            catch (TaskCanceledException e) { apiLastError = e; }
        }

        // 页面回退。注意：/commit/{branch} 会被 CDN 缓存并返回旧提交，
        // /commits/{branch} 才是分支提交列表。
        var commitsPage = $"https://github.com/{owner}/{repo}/commits/{branch}";
        foreach (var urlString in MirrorFirstCandidates(commitsPage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
                ApplyHeaders(request, null);
                var (data, status, finalUrl) = await SendAsync(request, 8, cancellationToken)
                    .ConfigureAwait(false);
                if (status is < 200 or > 299) continue;
                var html = System.Text.Encoding.UTF8.GetString(data);
                if (ParseShaFromCommitPage(html, owner, repo) is { } sha)
                {
                    return new LatestCommit(sha, null, finalUrl);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (GitHubMirrorFetchException) { continue; }
            catch (HttpRequestException) { continue; }
            catch (TaskCanceledException) { continue; }
        }

        throw apiLastError ?? new GitHubMirrorFetchException("未能获取最新提交");
    }

    /// <summary>
    /// 下载文件（zip 等），自动 fallback 镜像。
    /// 对 .zip 文件额外校验文件签名（PK\x03\x04），避免镜像返回 HTML 错误页或截断数据。
    /// </summary>
    public async Task DownloadAsync(
        string originalUrl,
        string destination,
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchAsync(originalUrl, null, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (result.Data.Length == 0) throw new GitHubMirrorFetchException("响应为空");

        if (originalUrl.ToLowerInvariant().EndsWith(".zip", StringComparison.Ordinal))
        {
            byte[] zipSignature = [0x50, 0x4B, 0x03, 0x04];
            if (result.Data.Length < zipSignature.Length
                || !result.Data.AsSpan(0, zipSignature.Length).SequenceEqual(zipSignature))
            {
                throw new GitHubMirrorFetchException("响应不是有效的 zip 文件");
            }
        }

        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = destination + ".tmp";
        await File.WriteAllBytesAsync(temp, result.Data, cancellationToken).ConfigureAwait(false);
        File.Move(temp, destination, overwrite: true);
    }

    // MARK: - 内部

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? extra)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (extra is null) return;
        foreach (var (key, value) in extra)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private async Task<(byte[] Data, int Status, string FinalUrl)> SendAsync(
        HttpRequestMessage request,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? request.RequestUri?.ToString() ?? string.Empty;
            return (data, (int)response.StatusCode, finalUrl);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException($"请求超时（{timeoutSeconds} 秒）");
        }
        catch (HttpRequestException e) when (e.StatusCode is null)
        {
            throw;
        }
    }

    /// <summary>HEAD 请求 releases/latest 重定向页，从最终 URL（或其 302 Location）解析出 tag。</summary>
    private async Task<string?> HeadReleaseTagAsync(
        string urlString,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, urlString);
            ApplyHeaders(request, null);
            var response = await SendRawAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                // 302 重定向：部分代理会保留 Location 头
                if ((int)response.StatusCode is >= 300 and <= 399
                    && response.Headers.Location is { } location
                    && ParseTagFromReleaseUrl(location.ToString()) is { } locatedTag)
                {
                    return locatedTag;
                }
                // HttpClient 默认自动跟随重定向，最终 URL 即 /releases/tag/<tag>
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
                return ParseTagFromReleaseUrl(finalUrl);
            }
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }
}

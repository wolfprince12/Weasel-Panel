//
//  ReleaseUpdateChecker.cs
//  WeaselPanel.Core
//
//  单一目标的 GitHub Release 更新检查器。
//  对齐 macOS 鼠须管面板 UpdateCenter 的 UpdateCheckState 状态机，
//  复用 WeaselPanel.Core.Net.GitHubMirrorFetch.FetchLatestReleaseAsync
//  （与词典包更新检查使用同一镜像 fallback 链路，行为一致）。
//
//  ── 与 macOS UpdateCenter 的差异 ──
//
//  macOS 那版是一个 Store 同时管三类检查（自身 / 输入法 / 词典包）；
//  此处按单一目标拆开，三处独立持有。原因：
//   1) 关于面板只需关心自身 + 输入法本体，词典包更新已在「输入方案管理」页
//      有一套 PackageUpdateState，并入会导致 about 页要 fork 一份。
//   2) 单一目标便于单元测试（mock GitHubMirrorFetch 注入桩处理器即可）。
//
//  ── 线程模型 ──
//
//  State / LatestVersion / HtmlUrl 在任意线程上变更（GitHub 拉取在
//  线程池上完成）。订阅方需 marshal 回 UI 线程——标准做法是
//  AboutViewModel 收到 PropertyChanged 后用 Application.Current.Dispatcher.BeginInvoke。
//

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WeaselPanel.Core.Net;

/// <summary>
/// 单目标（面板自身 / 输入法本体）的更新检查状态。
/// 与 macOS 鼠须管面板 <c>UpdateCheckState</c> 一一对应，
/// 命名小写拼写化为大写驼峰。
/// </summary>
public enum UpdateCheckState
{
    /// <summary>尚未发起检查。</summary>
    Idle,

    /// <summary>正在拉取 GitHub Release。</summary>
    Checking,

    /// <summary>本地版本不低于远程。</summary>
    UpToDate,

    /// <summary>远程有更高版本。</summary>
    Available,

    /// <summary>检查失败（网络、镜像全挂、反序列化异常等）。</summary>
    Failed,
}

/// <summary>
/// 单一目标的 GitHub Release 更新检查器。
/// 用法：注入当前版本提供器与仓库名，调 <see cref="CheckAsync"/> 即可；
/// UI 端订阅 <see cref="PropertyChanged"/> 后自行 marshal 回 UI 线程。
/// </summary>
public sealed class ReleaseUpdateChecker : INotifyPropertyChanged
{
    private readonly GitHubMirrorFetch _fetch;
    private readonly Func<string> _currentVersionProvider;
    private readonly string _repo;

    private CancellationTokenSource? _cts;
    private UpdateCheckState _state = UpdateCheckState.Idle;
    private string? _latestVersion;
    private string? _htmlUrl;

    /// <param name="fetch">已构造好的 GitHub 镜像拉取器（单例复用）。</param>
    /// <param name="currentVersionProvider">
    ///   返回「本地版本字符串」，允许空串。空串时视为「版本未知」——
    ///   检查后降级为 <see cref="UpdateCheckState.UpToDate"/>，避免误报「有更新」。
    /// </param>
    /// <param name="repo">目标仓库，格式 <c>owner/name</c>。</param>
    public ReleaseUpdateChecker(
        GitHubMirrorFetch fetch,
        Func<string> currentVersionProvider,
        string repo)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _currentVersionProvider = currentVersionProvider ?? throw new ArgumentNullException(nameof(currentVersionProvider));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public string Repo => _repo;

    public UpdateCheckState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCheckInFlight));
        }
    }

    public string? LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (_latestVersion == value) return;
            _latestVersion = value;
            OnPropertyChanged();
        }
    }

    public string? HtmlUrl
    {
        get => _htmlUrl;
        private set
        {
            if (_htmlUrl == value) return;
            _htmlUrl = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否正处于「检查中」，用于 UI 在 spinner 阶段禁用按钮。</summary>
    public bool IsCheckInFlight => _state == UpdateCheckState.Checking;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 发起一次新的检查。会取消任何正在进行中的旧检查（用户连点按钮时仅生效最后一次）。
    /// </summary>
    /// <param name="externalCt">外部取消令牌，用于 about 页关闭时一并中止 in-flight 检查。</param>
    public async Task CheckAsync(CancellationToken externalCt = default)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        State = UpdateCheckState.Checking;

        try
        {
            var currentRaw = (_currentVersionProvider() ?? string.Empty).Trim();
            var current = StripPrefixV(currentRaw);

            var release = await _fetch.FetchLatestReleaseAsync(_repo, ct).ConfigureAwait(false);
            var remoteRaw = (release.Tag ?? string.Empty).Trim();
            var remote = StripPrefixV(remoteRaw);

            HtmlUrl = release.HtmlUrl is { Length: > 0 } h
                ? h
                : $"https://github.com/{_repo}/releases/tag/{remoteRaw}";

            if (string.IsNullOrEmpty(current))
            {
                // 本地版本未知：保守返回 UpToDate，避免无依据弹"有更新"吓用户。
                LatestVersion = remote;
                State = UpdateCheckState.UpToDate;
                return;
            }

            LatestVersion = remote;
            State = CompareVersion(current, remote)
                ? UpdateCheckState.Available
                : UpdateCheckState.UpToDate;
        }
        catch (OperationCanceledException)
        {
            // 用户/外部取消：回到 Idle，不计入 Failed。
            LatestVersion = null;
            State = UpdateCheckState.Idle;
        }
        catch
        {
            LatestVersion = null;
            // HtmlUrl 保留上次成功值——Failed 时仍可点击"在 GitHub 上查看"
            State = UpdateCheckState.Failed;
        }
    }

    /// <summary>
    /// 取消正在进行的检查。不改变 State——留给 UI 在外部决定如何显示
    /// （通常是因为 about 页即将销毁，不需要再被动触发任何回调）。
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    private static string StripPrefixV(string s) =>
        s.Length > 0 && (s[0] == 'v' || s[0] == 'V') ? s[1..] : s;

    /// <summary>
    /// 简单段比较：<paramref name="remote"/> > <paramref name="current"/> 返回 true。
    /// 任一参数无法解析（空字符串或非数字段）则返回 false，退化为"无更新"。
    /// </summary>
    internal static bool CompareVersion(string current, string remote)
    {
        if (!TryParse(current, out var c)) return false;
        if (!TryParse(remote, out var r)) return false;
        var max = Math.Max(c.Length, r.Length);
        for (var i = 0; i < max; i++)
        {
            var cv = i < c.Length ? c[i] : 0;
            var rv = i < r.Length ? r[i] : 0;
            if (rv != cv) return rv > cv;
        }
        return false; // 完全相等
    }

    private static bool TryParse(string s, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var split = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var list = new int[split.Length];
        for (var i = 0; i < split.Length; i++)
        {
            if (!int.TryParse(split[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out list[i]))
            {
                return false;
            }
        }
        parts = list;
        return true;
    }
}

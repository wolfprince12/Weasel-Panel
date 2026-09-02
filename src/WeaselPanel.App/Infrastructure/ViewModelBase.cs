using System.ComponentModel;
using System.Runtime.CompilerServices;
using WeaselPanel.App.Localization;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// 轻量 MVVM 基类。本项目 UI 规模不大，不引入 MVVM 框架（也避免 GPL 兼容性问题）。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── 本地化状态文本 ────────────────────────────────────────────────
    // 状态栏文案是「赋值那一刻拼好的 string」，不像 XAML 的 {l10n:L} 能随
    // 语言切换自动重取。这里记下 key 与参数，切换语言时可原样重建 ——
    // 否则用户切到英文后，状态栏会继续显示上一句中文。

    private string _statusKey = "";
    private object?[] _statusArgs = Array.Empty<object?>();

    /// <summary>按 key 取出状态文本，并记住它以便语言切换后重建。</summary>
    protected string StatusFromKey(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        return L10n.Instance.T(key, args);
    }

    /// <summary>语言切换后重建当前状态文本。没有记过 key 时返回空串。</summary>
    protected string Restatus() =>
        _statusKey.Length == 0 ? "" : L10n.Instance.T(_statusKey, _statusArgs);

    /// <summary>用「上一次的 key + 新参数」重取（参数在语言切换之外也会变时用）。</summary>
    protected string Restatus(params object?[] args) => L10n.Instance.T(_statusKey, args);

    protected bool HasStatusKey => _statusKey.Length > 0;

    /// <summary>在后台线程执行后把结果派回 UI 线程赋值。</summary>
    protected static async Task RunBusyAsync(Func<bool> isBusySetter, Func<Task> work)
    {
        isBusySetter();
        try { await work(); }
        finally { isBusySetter(); }
    }
}

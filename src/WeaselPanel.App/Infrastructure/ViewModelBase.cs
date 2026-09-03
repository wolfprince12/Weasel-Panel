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
        HasUnsavedChanges = true;
        OnPropertyChanged(name);
        return true;
    }

    // ── 未保存标记（全局部署栏用）───────────────────────────────────
    // 全局「应用并重新部署」只应用有未保存改动的面板，避免把用户没改过的
    // 配置用内存里的默认值覆盖回磁盘（灾难性）。注意：部分面板（雾凇/紫毫/
    // 配色/应用选项/词典）已有自己的 baseline 版 IsDirty，本字段只服务
    // 没有自带脏追踪的面板；二者命名不同，不会冲突。各面板在 Load 末尾
    // 调 MarkLoaded() 把标记清零。
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>
    /// 未保存标记的统一出口（给 <see cref="IPanelActions"/> 用）。
    /// 自带 baseline 版 IsDirty 的面板（雾凇/紫毫/配色/应用选项/词典/方案）
    /// 用 <c>new</c> 隐藏本属性，以各自的基线比较口径为准；其余面板直接继承本值。
    /// </summary>
    public bool IsDirty => HasUnsavedChanges;

    /// <summary>加载/重新加载完成后调用，清零未保存标记。</summary>
    protected void MarkLoaded() => HasUnsavedChanges = false;

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

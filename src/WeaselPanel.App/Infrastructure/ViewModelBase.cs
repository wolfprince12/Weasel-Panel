using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    /// <summary>在后台线程执行后把结果派回 UI 线程赋值。</summary>
    protected static async Task RunBusyAsync(Func<bool> isBusySetter, Func<Task> work)
    {
        isBusySetter();
        try { await work(); }
        finally { isBusySetter(); }
    }
}

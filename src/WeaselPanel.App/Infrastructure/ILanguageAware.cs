//
//  ILanguageAware.cs
//  WeaselPanel.App
//
//  ViewModel 里「取值那一刻拼好的字符串」（状态栏、环境行标签、diff 提示等）
//  不会像 XAML 的 {l10n:L} 那样随语言切换自动刷新 —— 它们在构造/赋值时就
//  已经把文案固化进 string 了。实现本接口的 ViewModel 会被 MainWindow 在
//  语言切换时统一调一次 RefreshTexts()。
//
//  不要试图用别的方式绕开：让每个状态字符串都改成 XAML 绑定会把 ViewModel
//  变成一堆零碎属性的集合，可读性反而更差。
//

namespace WeaselPanel.App.Infrastructure;

public interface ILanguageAware
{
    /// <summary>按当前语言重建内部已固化的文案。必须是幂等的、且不做磁盘 I/O。</summary>
    void RefreshTexts();
}

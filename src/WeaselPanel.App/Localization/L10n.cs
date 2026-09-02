//
//  L10n.cs
//  WeaselPanel.App
//
//  运行时本地化。程序名、导航、各页文案全部走这里，随系统语言自动切换，
//  也允许用户在「关于」页手动覆盖（覆盖值存 %APPDATA%\WeaselPanel\settings.json）。
//
//  ── 为什么是"嵌入式 txt + ResourceManager 手写"而不是 .resx ──────────────
//  .resx 会为每种语言生成一个卫星程序集（zh-Hans/WeaselPanel.App.resources.dll）。
//  PublishSingleFile 是否把卫星程序集打进 exe 并不稳定：一旦没打进去，
//  中文 Windows 上会静默退化成英文 —— 正是用户反馈的"为什么还显示 WeaselPanel"。
//  三个 lang.*.txt 是主程序集的 EmbeddedResource，永远在 exe 内部，装不装得进
//  bundle 这个问题从根上不存在。代价是失去强类型键与 IDE 补全，
//  换来的是"语言包必然随 exe 走"这个硬保证。
//
//  ── 语言解析顺序 ───────────────────────────────────────────────────────
//  1. 用户在设置里选的语言（"auto" = 跟随系统）
//  2. CultureInfo.CurrentUICulture：zh-CN → zh-Hans → zh → 英文包
//  3. 英文包（lang.en.txt）—— 它是默认包，键最全
//  4. 键名本身：屏幕上出现 "Nav.Apperance" 这种字串 = 键拼错了，比空白好定位
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Markup;

namespace WeaselPanel.App.Localization;

public sealed class L10n : INotifyPropertyChanged
{
    /// <summary>语言包资源的嵌入名前缀（RootNamespace + 目录 + 文件名前缀）。</summary>
    private const string ResourcePrefix = "WeaselPanel.App.Localization.lang.";

    /// <summary>设置里的"跟随系统"。语言码绝不可能是这个词，故可直接当哨兵值用。</summary>
    public const string AutoLanguage = "auto";

    /// <summary>英文包是默认包：任何语言缺键都回落到这里。</summary>
    private const string DefaultLanguage = "en";

    /// <summary>内置语言包的代码，顺序即语言选择器的显示顺序。</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages =
        new[] { AutoLanguage, "zh-Hans", "zh-Hant", "en" };

    private static readonly Lazy<L10n> Lazy = new(() => new L10n());
    public static L10n Instance => Lazy.Value;

    private readonly Dictionary<string, Dictionary<string, string>> _packs =
        new(StringComparer.OrdinalIgnoreCase);

    private string _language = DefaultLanguage;

    private L10n()
    {
        LoadAllPacks();
        _language = ResolveSystemLanguage();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前生效的语言代码（不会是 <see cref="AutoLanguage"/>）。</summary>
    public string Language => _language;

    // ── 取值 ────────────────────────────────────────────────────────────

    /// <summary>XAML 索引器绑定的入口：<c>{Binding Source={StaticResource L}, Path=[Nav.Diagnostics]}</c>。</summary>
    public string this[string key] => T(key);

    public string T(string key)
    {
        if (Lookup(_language, key, out var value)) return value;
        if (Lookup(DefaultLanguage, key, out value)) return value;
        return key;
    }

    /// <summary>带占位符：<c>L10n.Instance.T("Diag.ProbeDone", 7)</c> → "探测完成，共 7 项"。</summary>
    public string T(string key, params object?[] args)
    {
        var template = T(key);
        if (args is null || args.Length == 0) return template;
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    private bool Lookup(string language, string key, out string value)
    {
        value = "";
        if (_packs.TryGetValue(language, out var pack) && pack.TryGetValue(key, out var hit))
        {
            value = hit;
            return true;
        }
        return false;
    }

    // ── 切换 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 切换语言。传 null / "auto" 表示跟随系统。
    /// 持久化由调用方负责（<c>PanelSettings</c>）—— 本类不碰磁盘，
    /// 免得单元测试一 new 它就去写 AppData。
    /// </summary>
    public void SetLanguage(string? language)
    {
        var resolved = string.IsNullOrWhiteSpace(language) || language == AutoLanguage
            ? ResolveSystemLanguage()
            : Normalize(language!);

        if (resolved == _language) return;

        _language = resolved;

        // "Item[]" 是 WPF 索引器绑定的通知名（Binding.IndexerName）。
        // 少了这一行，切换语言后界面上的文字不会重取 —— 这是个只在
        // "运行时切换" 才暴露的问题，启动即定语言时看不出来。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>把系统 / 用户给的语言码收敛到内置包之一，命中不了就用英文包。</summary>
    private string Normalize(string language)
    {
        foreach (var supported in SupportedLanguages)
        {
            if (supported == AutoLanguage) continue;
            if (string.Equals(supported, language, StringComparison.OrdinalIgnoreCase)) return supported;
        }

        // zh-CN / zh-SG → zh-Hans；zh-TW / zh-HK / zh-MO → zh-Hant
        try
        {
            var culture = new CultureInfo(language);
            var name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                for (var c = culture; c is not null && c != CultureInfo.InvariantCulture; c = c.Parent)
                {
                    if (c.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
                    if (c.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
                }
                // ICU 下 zh-CN 的 Parent 是 zh-Hans；没有 Hant/S 标记时按国别兜底
                if (name.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
                return "zh-Hans";
            }
        }
        catch (CultureNotFoundException)
        {
            // 语言码不合法 → 走下面的英文兜底
        }

        return DefaultLanguage;
    }

    private string ResolveSystemLanguage()
    {
        try { return Normalize(CultureInfo.CurrentUICulture.Name); }
        catch { return DefaultLanguage; }
    }

    /// <summary>语言选择器用的显示名（语言名本身不翻译，跟 Windows 语言列表惯例一致）。</summary>
    public static string DisplayNameOf(string code) => code switch
    {
        AutoLanguage => FollowSystemLabel(),
        "zh-Hans" => "简体中文",
        "zh-Hant" => "繁體中文",
        "en" => "English",
        _ => code,
    };

    /// <summary>
    /// 「跟随系统」这一项本身要显示成什么语言：按**当前系统语言**决定，
    /// 而不是按当前选中语言 —— 否则用户切到英文后，这一项会变成 "Follow system"，
    /// 而它旁边的中文系统用户正需要看懂它。
    /// </summary>
    private static string FollowSystemLabel()
    {
        var sys = Instance.ResolveSystemLanguage();
        return sys switch
        {
            "zh-Hant" => "跟隨系統",
            "zh-Hans" => "跟随系统",
            _ => "Follow system",
        };
    }

    // ── 载入 ────────────────────────────────────────────────────────────

    private void LoadAllPacks()
    {
        var assembly = typeof(L10n).Assembly;
        string[] names;
        try { names = assembly.GetManifestResourceNames(); }
        catch { return; }

        foreach (var resourceName in names)
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resourceName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;

            var code = resourceName.Substring(
                ResourcePrefix.Length,
                resourceName.Length - ResourcePrefix.Length - ".txt".Length);

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                _packs[code] = ParsePack(reader.ReadToEnd());
            }
            catch
            {
                // 单个语言包读不出来不能让程序起不来 —— 缺键会回落英文包，再缺显示键名
            }
        }
    }

    /// <summary>
    /// 解析 <c>Key = Value</c> 行。 rules：# 开头是注释；按第一个 " = " 切分；\n 转真换行。
    /// </summary>
    internal static Dictionary<string, string> ParsePack(string text)
    {
        var pack = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            var sep = line.IndexOf(" = ", StringComparison.Ordinal);
            if (sep <= 0) continue;

            var key = line.Substring(0, sep).Trim();
            var value = line.Substring(sep + 3);
            if (key.Length == 0) continue;

            pack[key] = value.Replace("\\n", "\n");
        }
        return pack;
    }
}

/// <summary>
/// XAML 里的本地化标记扩展：<c>{l10n:L Nav.Diagnostics}</c>。
/// 返回的是 Binding 而非字符串，所以语言切换时界面会跟着刷新 ——
/// 直接返回字符串的话只能一次性求值，切语言后旧文字会留在屏幕上。
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class L : MarkupExtension
{
    public L() { }

    public L(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // "[(0)]" 是 PropertyPath 的索引器参数语法：即便键里带点（Nav.Diagnostics）
        // 也不会被当成属性路径分隔符。
        var binding = new Binding
        {
            Source = L10n.Instance,
            Path = new System.Windows.PropertyPath("[(0)]", Key),
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

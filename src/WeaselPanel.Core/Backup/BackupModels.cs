//
//  BackupModels.cs
//  WeaselPanel.Core
//
//  备份与差异对比的数据模型。由 macOS 版 Squirrel Panel 的
//  BackupManager.swift 顶部模型段直译。
//

namespace WeaselPanel.Core.Backup;

/// <summary>差异行的性质。</summary>
public enum DiffKind
{
    /// <summary>当前版本新增（备份版本没有）。</summary>
    Added,

    /// <summary>备份版本存在、当前缺失。</summary>
    Removed,

    /// <summary>两版相同。</summary>
    Equal,
}

/// <summary>行级差异的一行（unified / 单列格式）。</summary>
public sealed class DiffLine
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Text { get; init; } = string.Empty;
    public DiffKind Kind { get; init; }

    public DiffLine() { }

    public DiffLine(string text, DiffKind kind)
    {
        Text = text;
        Kind = kind;
    }
}

/// <summary>左右双栏 diff 的一行。</summary>
public sealed class SideBySideLine
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>备份版行号（1-based）；null 表示该行是新增行。</summary>
    public int? LeftNo { get; init; }

    /// <summary>当前版行号（1-based）；null 表示该行已被删除。</summary>
    public int? RightNo { get; init; }

    public string LeftText { get; init; } = string.Empty;
    public string RightText { get; init; } = string.Empty;
    public DiffKind Kind { get; init; }
}

/// <summary>一次备份的元信息。</summary>
public sealed class BackupInfo
{
    /// <summary>备份目录名（同时作为唯一标识）。</summary>
    public required string DirName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>备份标签；null 表示自动备份。</summary>
    public string? Label { get; init; }

    public int FileCount { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>界面展示用的体积文本（如 "132 KB"）。</summary>
    public string SizeText => FormatSize(SizeBytes);

    /// <summary>界面展示用的时间文本（zh-CN 中长格式）。</summary>
    public string CreatedText =>
        CreatedAt.LocalDateTime.ToString("yyyy年M月d日 HH:mm");

    /// <summary>界面展示用的标签文本；无标签时回退为「自动备份」。</summary>
    public string LabelText =>
        string.IsNullOrEmpty(Label) ? "自动备份" : Label;

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{(long)value} {units[unit]}" : $"{value:F1} {units[unit]}";
    }
}

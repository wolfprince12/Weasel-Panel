using System.Windows.Media;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 外观页 3 列色卡网格里的一张「方案卡」。预览色一律走 Core 的
/// <see cref="WeaselPanel.Core.Rime.ColorSchemeResolver"/> 回退链（与候选窗一致），
/// 保证「卡片所见」=「候选窗所得」。
/// </summary>
public sealed class SchemeCardItem
{
    public string Name { get; }
    public bool IsActive { get; }
    public SolidColorBrush BackBrush { get; }
    public SolidColorBrush BorderBrush { get; }
    public SolidColorBrush TextBrush { get; }
    public SolidColorBrush CandidateTextBrush { get; }
    public SolidColorBrush HilitedBackBrush { get; }
    public SolidColorBrush HilitedTextBrush { get; }
    public SolidColorBrush LabelBrush { get; }
    public SolidColorBrush CommentBrush { get; }

    public SchemeCardItem(
        string name, bool isActive,
        SolidColorBrush back, SolidColorBrush border, SolidColorBrush text,
        SolidColorBrush candidate, SolidColorBrush hilitedBack, SolidColorBrush hilitedText,
        SolidColorBrush label, SolidColorBrush comment)
    {
        Name = name;
        IsActive = isActive;
        BackBrush = back;
        BorderBrush = border;
        TextBrush = text;
        CandidateTextBrush = candidate;
        HilitedBackBrush = hilitedBack;
        HilitedTextBrush = hilitedText;
        LabelBrush = label;
        CommentBrush = comment;
    }
}

//
//  LineSplitter.cs
//  WeaselPanel.Core
//
//  统一的文本行切分。全项目只允许有这一处实现，避免各模块对换行的处理出现漂移。
//
//  ── 语义 ──
//  等价于 Swift 的 `text.components(separatedBy: .newlines)` / 
//  `split(separator: "\n", omittingEmptySubsequences: false)`：
//    - "\r\n"、"\r"、"\n" 三种换行一律视为行分隔；
//    - 保留空行（连续的换行产生空元素）；
//    - 结果不含行尾换行符。
//
//  ── 为什么 Windows 侧必须显式处理 \r ──
//  macOS 上 Rime 的配置文件几乎都是 LF，Swift 的 .newlines 会正确处理 CRLF；
//  Windows 上用户可能用记事本等工具编辑过 custom_phrase.txt 而留下 CRLF。
//  若只按 '\n' 切分，每行末尾会残留 '\r'，导致词条的「码」字段带脏字符、
//  写回时又被二次追加 \r\n，最终词典错乱。故读取时一律归一化。
//
//  ── 写盘约定 ──
//  本面板写出的文件一律 LF（与上游 weasel.yaml 及 macOS 版保持一致），
//  不因运行在 Windows 上就写 CRLF。
//

namespace WeaselPanel.Core.Text;

public static class LineSplitter
{
    /// <summary>按 LF 换行符连接（本面板所有写盘路径的唯一出口）。</summary>
    public static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

    /// <summary>切分为行，兼容 LF / CRLF / CR，保留空行。</summary>
    public static string[] Split(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

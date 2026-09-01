//
//  LineDiffer.cs
//  WeaselPanel.Core
//
//  基于 LCS（最长公共子序列）的行级差异算法。
//  由 macOS 版 BackupManager.swift 的 diffLines / diffLinesSideBySide 直译，
//  行为逐行对齐：同样优先「先输出删除行、再输出新增行」的归并顺序。
//
//  ── 已知限制（与 macOS 版一致，未擅自更改算法）──
//  采用 O(n×m) 的 DP 表，n/m 为两版行数。对几千行的配置尚可接受，
//  但数万行（如超大的 custom_phrase.txt）会造成百 MB 级内存占用与明显卡顿。
//  此处刻意保持与 macOS 版相同的行为，避免在移植阶段引入结果差异；
//  若后续需要优化，应改为 Myers / 分治 LCS 并补交叉验证测试。
//

namespace WeaselPanel.Core.Backup;

/// <summary>
/// 纯函数式行级 diff，不含任何文件 I/O，便于单元测试。
/// </summary>
public static class LineDiffer
{
    /// <summary>
    /// 把文本切分为行。委托给 <see cref="Text.LineSplitter"/>，全项目共用一处实现。
    /// 语义与 Swift 的 <c>split(separator: "\n", omittingEmptySubsequences: false)</c>
    /// 一致：保留空行，不保留行尾的 "\n"。
    /// </summary>
    public static string[] SplitLines(string text) => Text.LineSplitter.Split(text);

    /// <summary>
    /// LCS 合并 diff：返回按阅读顺序排列的差异行（单列 unified 风格）。
    /// </summary>
    /// <param name="a">旧版本（备份版）行序列。</param>
    /// <param name="b">新版本（当前版）行序列。</param>
    public static List<DiffLine> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = BuildLcsTable(a, b);
        var n = a.Count;
        var m = b.Count;
        var result = new List<DiffLine>(n + m);

        int i = 0, j = 0;
        while (i < n && j < m)
        {
            if (a[i] == b[j])
            {
                result.Add(new DiffLine(a[i], DiffKind.Equal));
                i++;
                j++;
            }
            else if (dp[i + 1][j] >= dp[i][j + 1])
            {
                result.Add(new DiffLine(a[i], DiffKind.Removed));
                i++;
            }
            else
            {
                result.Add(new DiffLine(b[j], DiffKind.Added));
                j++;
            }
        }
        for (; i < n; i++) result.Add(new DiffLine(a[i], DiffKind.Removed));
        for (; j < m; j++) result.Add(new DiffLine(b[j], DiffKind.Added));

        return result;
    }

    /// <summary>
    /// LCS 左右双栏 diff：返回左右对齐的差异行（备份版左 / 当前版右），
    /// 行号为 1-based（与 git diff / 常见对比工具一致）。
    /// </summary>
    public static List<SideBySideLine> DiffSideBySide(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var dp = BuildLcsTable(a, b);
        var n = a.Count;
        var m = b.Count;
        var result = new List<SideBySideLine>(n + m);

        int li = 1, rj = 1;
        int i = 0, j = 0;
        while (i < n && j < m)
        {
            if (a[i] == b[j])
            {
                result.Add(new SideBySideLine
                {
                    LeftNo = li,
                    RightNo = rj,
                    LeftText = a[i],
                    RightText = b[j],
                    Kind = DiffKind.Equal
                });
                i++; j++; li++; rj++;
            }
            else if (dp[i + 1][j] >= dp[i][j + 1])
            {
                // 备份版有、当前版无 → 删除行
                result.Add(new SideBySideLine
                {
                    LeftNo = li,
                    RightNo = null,
                    LeftText = a[i],
                    RightText = string.Empty,
                    Kind = DiffKind.Removed
                });
                i++; li++;
            }
            else
            {
                // 当前版有、备份版无 → 新增行
                result.Add(new SideBySideLine
                {
                    LeftNo = null,
                    RightNo = rj,
                    LeftText = string.Empty,
                    RightText = b[j],
                    Kind = DiffKind.Added
                });
                j++; rj++;
            }
        }
        for (; i < n; i++, li++)
        {
            result.Add(new SideBySideLine
            {
                LeftNo = li,
                RightNo = null,
                LeftText = a[i],
                RightText = string.Empty,
                Kind = DiffKind.Removed
            });
        }
        for (; j < m; j++, rj++)
        {
            result.Add(new SideBySideLine
            {
                LeftNo = null,
                RightNo = rj,
                LeftText = string.Empty,
                RightText = b[j],
                Kind = DiffKind.Added
            });
        }
        return result;
    }

    /// <summary>
    /// 构建后缀 LCS 表：dp[i][j] = a[i..] 与 b[j..] 的最长公共子序列长度。
    /// 与原 Swift 实现一致，自底向上（自后向前）填充。
    /// </summary>
    private static int[][] BuildLcsTable(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var dp = new int[n + 1][];
        for (var k = 0; k <= n; k++) dp[k] = new int[m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i][j] = a[i] == b[j]
                    ? dp[i + 1][j + 1] + 1
                    : Math.Max(dp[i + 1][j], dp[i][j + 1]);
            }
        }
        return dp;
    }
}

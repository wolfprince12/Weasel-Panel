//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  面板异常。Core 层只负责给出**错误码与原始参数**，不做本地化；
//  面向用户的文案由 App 层按 ErrorCode 查 .resx 渲染。
//  这样单元测试可以直接比错误码，不受语言环境影响。

using System;
using System.Collections.Generic;
using System.Linq;

namespace WeaselPanel.Core;

public enum PanelErrorCode
{
    /// <summary>文件无法解析，拒绝覆盖（安全底线：绝不拿空内容覆盖用户配置）。</summary>
    RefusedToOverwrite,
    /// <summary>未检测到小狼毫安装。</summary>
    WeaselNotInstalled,
    /// <summary>外部命令执行失败。</summary>
    CommandFailed,
    /// <summary>部署前预检发现方案的 .schema.yaml 源文件缺失，已中止部署。</summary>
    SchemaSourcesMissing,
    /// <summary>写后回读校验未通过。</summary>
    WriteVerificationFailed,
    /// <summary>无法确定小狼毫用户目录。</summary>
    UserDirectoryUnavailable
}

public sealed class PanelException : Exception
{
    public PanelErrorCode Code { get; }
    public IReadOnlyList<string> Arguments { get; }

    public PanelException(PanelErrorCode code, string message, params string[] arguments)
        : base(message)
    {
        Code = code;
        Arguments = arguments;
    }

    public static PanelException RefusedToOverwrite(string fileName) =>
        new(PanelErrorCode.RefusedToOverwrite,
            "Refusing to overwrite unparsable file: " + fileName, fileName);

    public static PanelException WeaselNotInstalled() =>
        new(PanelErrorCode.WeaselNotInstalled, "Weasel (Rime) is not installed.");

    public static PanelException CommandFailed(string command, int exitCode) =>
        new(PanelErrorCode.CommandFailed,
            "Command failed: " + command + " (exit " + exitCode + ")", command, exitCode.ToString());

    public static PanelException SchemaSourcesMissing(IEnumerable<string> schemaIds)
    {
        var ids = schemaIds.ToList();
        return new PanelException(PanelErrorCode.SchemaSourcesMissing,
            "Missing .schema.yaml sources for: " + string.Join(", ", ids), ids.ToArray());
    }

    public static PanelException WriteVerificationFailed(string fileName) =>
        new(PanelErrorCode.WriteVerificationFailed,
            "Write verification failed: " + fileName, fileName);

    public static PanelException UserDirectoryUnavailable(string reason) =>
        new(PanelErrorCode.UserDirectoryUnavailable,
            "Cannot determine Rime user directory: " + reason, reason);
}

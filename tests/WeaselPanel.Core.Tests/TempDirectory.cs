//
//  TempDirectory.cs
//  WeaselPanel.Core.Tests
//
//  每个用例独享一个临时目录，用完即删。
//  对应 macOS 版「测试跑在 fixture 上、不依赖本机是否装了输入法」的原则：
//  本项目的全部路径均由构造参数注入，因此测试无需（也绝不允许）触碰真实的用户目录。
//

namespace WeaselPanel.Core.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Root { get; }

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "weaselpanel-t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    /// <summary>在临时目录内写入一个文件（自动建父目录），返回完整路径。</summary>
    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    public string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool Exists(string relativePath) =>
        File.Exists(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* 清理失败不影响测试结果 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }
}

#!/usr/bin/env python3
"""
check_uri_consts.py — VM 端「URL 字面量被声明成 const string」体检。

扫 src/WeaselPanel.App/**/*.cs 找形如：

    ... const string Xxx = "https://..."

    的字段 / 成员。一旦存在，建议改为 static readonly Uri ——

理由：
WPF 的 Hyperlink.NavigateUri / Image.Source / Icon 属性都是 System.Uri 类型。
XAML 端用 `{x:Static vm:Class.Xxx}` 或 `{Binding Xxx}` 把 const string 喂过去，
会走 **string → Uri TypeConverter** 路径。该 TypeConverter 对无尾斜杠主机名
（"https://rime.im"、"https://example.com"）以及对某些 .NET 子版本（.NET 8 上的
特定 hotfix），抛 ArgumentException("... 不是属性 'NavigateUri' 的有效值")——
InitializeComponent() 时立刻 XamlParseException，真机启动崩盘。

v0.2.5 真机踩过（2026-09-03 15:32 截图）：
  RimeUrl/WeaselUrl/RepoUrl/IssuesUrl/LuaDownloadUrl/PromoteItem.Url 都是 const string，
  XAML 端 NavigateUri=... 全部炸。

精准捕捉：本 lint 只报 URL scheme 开头（http/https/ftp/file/pack）的 const string 字面量
——几乎肯定是被 XAML 当 Uri 用。其它 const string（正则在用、键名、文件名）一律不动。

排除：
  · 注释里 /** const string X = "https://... */  →  仍被正则抓到，但 grep -rn 时
    一眼能看出是注释；脚本不区分（提高覆盖率优先于一点点误报）。
  · 字符串变量拼接：public string X => "/path" + BASE_URL  →  无法用纯正则静态判定，
    本 lint 不覆盖（建议人工审）。可考虑后续扩展支持多文件 + 控制流分析。

用法：python3 tools/check_uri_consts.py
退出码：存在违规时非 0。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "WeaselPanel.App"

# ── 抽取正则 ─────────────────────────────────────────────────────────────

# 拦 ① public const string X = "https://..." / ② private const / ③ static readonly string X = "https://..."
#   · const string 优先级最高（这是最常见埋雷姿势）
#   · static readonly string X = "https://..." 也是雷（XAML binding 同样走 TypeConverter），
#     单独覆盖以防有人用 readonly 躲 const 但没改类型
# URL scheme 严格匹配：(http|https|ftp|file|pack)://
URL_SCHEMES = r"(?:https?|ftp|file|pack)://"

# const string（关键字顺序可能是 public/private/internal const string 也可能直接 const string）
CONST_STRING_RE = re.compile(
    r'\b(?:public|private|internal|protected|static|\s)*\bconst\s+string\s+(\w+)\s*=\s*"('
    + URL_SCHEMES + r'[^"]*)"\s*;',
    re.MULTILINE,
)

# static readonly string（readonly URL 同样埋雷）
STATIC_READONLY_STRING_RE = re.compile(
    r'\bstatic\s+readonly\s+string\s+(\w+)\s*=\s*"('
    + URL_SCHEMES + r'[^"]*)"\s*;',
    re.MULTILINE,
)

# 类级正则：匹配 `public string PropName => "https://...";` 风格的 instance 计算属性
#   （vector: XAML 用 {Binding Prop} 拿 string 喂 Uri 类型属性——同样雷）
INSTANCE_PROP_URL_RE = re.compile(
    r'\bpublic\s+string\s+(\w+)\s*=>\s*"('
    + URL_SCHEMES + r'[^"]*)"\s*;',
    re.MULTILINE,
)


def strip_csharp_comments(text):
    """只 strip 块注释 /* ... */（保留行号），不去碰 // 行注释。

    为什么不处理 //？
    C# 里 // 大量出现在 URL 字面量中（http://、https://、ftp://、file://），天真地
    把两个 / 视为行注释起始会把 URL 字面量当注释吃掉。本 lint 关心 URL 字面量、
    这种 strip 会自废武功。改用「// 前必须空白/标点/行首」的预查复杂度高、收益低，
    故放弃。

    块注释里基本不会嵌 const string 字面量。
    """

    out = []
    i = 0
    n = len(text)
    while i < n:
        if text[i:i + 2] == "/*":
            j = text.find("*/", i + 2)
            if j < 0:
                j = n
            else:
                j += 2
            seg = text[i:j]
            out.append(re.sub(r"[^\n]", " ", seg))
            i = j
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def scan():
    """返回 list[ (file, line, decl_kind, member_name, url) ]。"""
    findings = []

    for path in sorted(APP.rglob("*.cs")):
        if any(p in path.parts for p in ("obj", "bin")):
            continue
        rel = path.relative_to(ROOT)
        text = path.read_text(encoding="utf-8")
        body = strip_csharp_comments(text)

        for lineno, line in enumerate(body.split("\n"), 1):
            for m in CONST_STRING_RE.finditer(line):
                findings.append((str(rel), lineno, "const string", m.group(1), m.group(2)))
            for m in STATIC_READONLY_STRING_RE.finditer(line):
                # 还要排除 const string（避免重复抓）—— STATIC_READONLY_STRING_RE 不会
                # 抓到 const，因为 const 不是 readonly。但 const 后有「= "https://..."」
                # 的形式会被上面 CONST_STRING_RE 抓到，STATIC_READONLY_STRING_RE 不会。
                findings.append((str(rel), lineno, "static readonly string", m.group(1), m.group(2)))
            for m in INSTANCE_PROP_URL_RE.finditer(line):
                findings.append((str(rel), lineno, "instance property (=>)", m.group(1), m.group(2)))

    return findings


def main():
    findings = scan()

    if not findings:
        print("OK：未发现「URL 字面量被声明成 string」字段 / 属性。")
        print("   （Hyperlink.NavigateUri 等 Uri 类型属性的 string→Uri 雷区已清空。）")
        return 0

    print(f"[高风险 URL 字面量] 共 {len(findings)} 处 —— 被 XAML 给 Uri 类型属性引用时会运行时崩\n")

    # 分类聚合
    by_kind = {}
    for file, line, kind, member, url in findings:
        by_kind.setdefault(kind, []).append((file, line, member, url))

    for kind in ("const string", "static readonly string", "instance property (=>)"):
        items = by_kind.get(kind, [])
        if not items:
            continue
        print(f"── {kind} （{len(items)} 处）────────────────────────────")
        for file, line, member, url in sorted(items, key=lambda x: (x[0], x[1])):
            print(f"   {file}:{line}  {member}  =  {url}")
        print()

    print("── 修法 ──────────────────────────────────────────────────")
    print("   const string XxxUrl = \"https://...\";")
    print("           ↓")
    print("   static readonly Uri XxxUrl = new(\"https://.../\");  // ← 删 const、加尾斜杠")
    print()
    print("   注：")
    print("     · Uri 不是编译期常量，所以不能用 const —— 必须 readonly 或普通 static。")
    print("     · 一律带尾斜杠，规避 UriTypeConverter 对无路径主机名的边界 case。")
    print("     · 计算属性 public string IssuesUrl => RepoUrl + \"/issues\";  改为：")
    print("       public Uri IssuesUrl => new(RepoUrl, \"/issues\");")
    print()
    print("   XAML 端零改动 —— Uri 类型直接喂给 NavigateUri，跳过 string→Uri TypeConverter。")
    return 1


if __name__ == "__main__":
    sys.exit(main())

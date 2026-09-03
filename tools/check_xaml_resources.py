#!/usr/bin/env python3
"""
check_xaml_resources.py — XAML 静态资源体检。

扫 src/WeaselPanel.App 下所有 .xaml，把:

  · x:Key="X" 声明的资源（覆盖 App.xaml + 各 View 的 <UserControl.Resources> /
                   <Window.Resources> / 任何内联 ResourceDictionary）
  · {StaticResource X} / {DynamicResource X} 引用

各列一份，对照「**引用方所有 Key 必须在声明方里找得到**」——引用了但不存在的 Key，
直接失败。

为什么必须做这件事：

WPF 的 {StaticResource X} 是**运行期**才解析的（BAML loader 在 InitializeComponent()
阶段才向上查 X 的 x:Key）。XAML 编译期不验证、dotnet build 不报错、IDE 偶尔飘红
但 build 还是过。所以「把 VM 的 public const string（RimeUrl / WeaselUrl 等）当成
{StaticResource RimeUrl} 引用」这种错误只有真机 WPF 启动时才会抛
XamlParseException("无法解析资源 'RimeUrl'") → 启动崩溃。

2026-09-03 真机踩坑两次：
  · v0.2.4：<Hyperlink> 直接放在 <WrapPanel>（Hyperlink 是 Inline 不是 UIElement）
  · v0.2.5：{StaticResource RimeUrl}（RimeUrl 是 VM 的 const，不是资源字典的 x:Key）
本 lint 拦住第二类。第一类由 XAML 编译器的语法检查在 publish 阶段处理（但 Hyperlink
错误是纯运行时，连编译器都未必抓到——见 weasel-panel memory 2026-09-03 的笔记）。

排除的扩展语法（不是资源引用）：
  · {x:Static ns:X.Y}    — 引用 C# 静态成员
  · {Binding ...}         — 数据绑定
  · {l10n:L Key}          — 本地化标记扩展
  · {RelativeSource ...}  / {TemplateBinding ...} / {x:Reference ...} / {x:Type ...}

用法：python3 tools/check_xaml_resources.py
退出码：存在「引用未声明」时非 0。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "WeaselPanel.App"
SCAN_SUFFIX = ".xaml"

# ── 抽取正则 ─────────────────────────────────────────────────────────────

# x:Key="X"。X 是双引号里的字面量（资源键通常 PascalCase / camelCase / 带点，
# 但 XAML 没硬性约束；这里不挑字符，全段抓出再交给 WPF 解析时挑）。
KEY_DECL_RE = re.compile(r'\bx:Key="([^"]+)"')

# {StaticResource X} / {DynamicResource X}。X 不含空白和花括号。
STATIC_REF_RE = re.compile(r'\{StaticResource\s+([^\s{}]+)\s*\}')
DYNAMIC_REF_RE = re.compile(r'\{DynamicResource\s+([^\s{}]+)\s*\}')


def strip_xaml_comments(text):
    """把 XAML 注释 <!-- ... --> 整段挖空（保留换行，行号不变）。

    真有用：v0.2.5 修复时给 AboutView.xaml 加了 ⚠️ 注释，里面贴了 "{StaticResource RimeUrl}"
    字样——不剔除会被当引用、报一屏假阳性。
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        if text[i:i + 4] == "<!--":
            j = text.find("-->", i + 4)
            if j < 0:
                # 没闭合——WPF 自己会报错，但脚本层先吞掉，避免语法错把后面全废。
                j = n
            else:
                j += 3
            seg = text[i:j]
            # 用同长度空白替换，换行保留 → 行号漂移 = 0
            out.append(re.sub(r"[^\n]", " ", seg))
            i = j
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def scan():
    """返回 (declared: dict[key, file:line], refs: dict[key, list[file:line]])。"""
    declared = {}     # key -> 首次声明的 file:line（多次声明只记第一次）
    refs = {}         # key -> 所有引用位置

    for path in sorted(APP.rglob(f"*{SCAN_SUFFIX}")):
        # 排除 obj/（BAML 生成的中间目录）和 bin/（publish 产物），都是脚本自己也会读到的副本
        if any(p in path.parts for p in ("obj", "bin")):
            continue
        rel = path.relative_to(ROOT)
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        body = strip_xaml_comments(text)

        for lineno, line in enumerate(body.split("\n"), 1):
            for m in KEY_DECL_RE.finditer(line):
                k = m.group(1)
                # StaticResource 引用 target 不一定是 ResourceDictionary 里的 x:Key，
                # 也可能直接引用了一个有 x:Name 的元素——但本项目不这么用，且 x:Name
                # 不带 x:Key，本正则抓不到。安全。
                declared.setdefault(k, f"{rel}:{lineno}")
            for m in STATIC_REF_RE.finditer(line):
                refs.setdefault(m.group(1), []).append(f"{rel}:{lineno}")
            for m in DYNAMIC_REF_RE.finditer(line):
                refs.setdefault(m.group(1), []).append(f"{rel}:{lineno}")

    return declared, refs


def main():
    declared, refs = scan()

    ref_count = sum(len(v) for v in refs.values())
    print(f"资源声明：{len(declared)} 个 x:Key")
    print(f"资源引用：{ref_count} 处（{len(refs)} 个不同 Key）")

    # 引用了但声明方里没有 = 启动崩溃候选
    missing = {k: locs for k, locs in refs.items() if k not in declared}

    if not missing:
        print("\nOK：所有 {StaticResource}/{DynamicResource} 引用都对应了 x:Key 声明。")
        return 0

    print(f"\n[引用未声明] {len(missing)} 个 Key —— 真机 InitializeComponent() 会抛 XamlParseException")
    # 按引用次数倒序排，最常被引用的错排前
    for k in sorted(missing, key=lambda x: (-len(missing[x]), x)):
        locs = missing[k]
        print(f"   {k}   ({len(locs)} 处引用)")
        for loc in locs[:3]:
            print(f"      ← {loc}")
        if len(locs) > 3:
            print(f"      … 还有 {len(locs) - 3} 处")

    # 提示：也告诉用户去哪些常见位置登记 x:Key
    print("\n   在 App.xaml 的 <Application.Resources> 里加 <Style>/<Color>/<Brush> "
          "等 x:Key，或在对应 View 的 <UserControl.Resources> 里声明。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
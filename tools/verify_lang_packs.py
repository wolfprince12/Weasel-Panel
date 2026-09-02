#!/usr/bin/env python3
"""
verify_lang_packs.py — 出包后验收：三个语言包是否真的进了 exe。

── 为什么要有这道关卡 ────────────────────────────────────────────────
2026-09-02 踩过一个坑：csproj 只写了 <EmbeddedResource Include="Resources\\logo.png" />，
三个 Localization/lang.*.txt 从没被声明为嵌入资源（.NET SDK 的默认 glob 只收
**/*.resx，.txt 归 None）。语言包一个字节都没进 exe，L10n.LoadAllPacks() 拿到的
GetManifestResourceNames() 里只有 logo.png，于是 _packs 为空、T(key) 原样返回键名——
界面上会显示 "App.Name"、"Nav.Diagnostics" 这种裸键。

更阴险的是它在自检时**看起来是好的**：往 exe 里搜「小狼毫控制面板」能搜到，
但那其实是 csproj 的 <Product> 写进 PE 版本信息的字符串，纯假阳性。

所以本脚本不用「程序名」当凭据，而是拿每个语言包里**最长、最具区分度的若干行**
（完整 "Key = Value" 原文）去 exe 里真搜 UTF-8 字节。语言包是 EmbeddedResource，
内容原样存储、不压缩，只要嵌入成功就一定搜得到。

用法：
    python3 tools/verify_lang_packs.py dist/win-x64/WeaselPanel.exe
退出码：
    0 = 三包齐全    1 = 有包缺失/缺行    2 = 用法或文件错误
"""

from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PACK_DIR = ROOT / "src" / "WeaselPanel.App" / "Localization"

# 每个包抽多少行去验收。抽全量（291 键 × 3 包）要扫 140GB，太慢；
# 取最长的若干行足够区分「包没进去」和「包被截断」。
SAMPLES_PER_PACK = 8

# 这些键的取值会跟 CLR / WPF 的本地化资源串撞车，或者本身就是配置字面量，
# 不适合当哨兵。
SKIP_KEYS = {"App.Name", "App.NameShort", "App.Subtitle"}


def pick_sentinels(pack_path: pathlib.Path) -> list[str]:
    """挑出最长、最具区分度的若干条 'Key = Value' 原文行。"""
    lines: list[str] = []
    for raw in pack_path.read_text(encoding="utf-8").split("\n"):
        line = raw.rstrip("\r")
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        sep = line.find(" = ")
        if sep <= 0:
            continue
        key = line[:sep].strip()
        if key in SKIP_KEYS:
            continue
        lines.append(line)
    # 按行长度降序 —— 长行最不容易跟 exe 里的其他字符串巧合重复
    lines.sort(key=len, reverse=True)
    return lines[:SAMPLES_PER_PACK]


def main() -> int:
    if len(sys.argv) != 2:
        print(f"用法：{sys.argv[0]} <WeaselPanel.exe 路径>", file=sys.stderr)
        return 2

    exe = pathlib.Path(sys.argv[1])
    if not exe.is_file():
        print(f"找不到 exe：{exe}", file=sys.stderr)
        return 2

    packs = sorted(PACK_DIR.glob("lang.*.txt"))
    if not packs:
        print(f"找不到语言包：{PACK_DIR}/lang.*.txt", file=sys.stderr)
        return 2

    print(f"exe：{exe}  ({exe.stat().st_size / 1024 / 1024:.1f} MB)")
    blob = exe.read_bytes()

    failed = False
    for pack in packs:
        code = pack.name[len("lang."): -len(".txt")]
        sentinels = pick_sentinels(pack)
        missing = [s for s in sentinels if blob.find(s.encode("utf-8")) < 0]

        if missing:
            failed = True
            print(f"  ❌ {code:<8} 缺 {len(missing)}/{len(sentinels)} 条哨兵 —— 语言包没进 exe")
            for s in missing:
                shown = s if len(s) <= 90 else s[:87] + "..."
                print(f"       未命中：{shown}")
        else:
            print(f"  ✅ {code:<8} {len(sentinels)}/{len(sentinels)} 条哨兵命中")

    if failed:
        print()
        print("语言包未随 exe 分发。检查 csproj 是否有：")
        print('    <EmbeddedResource Include="Localization\\lang.*.txt" />')
        print("（.NET SDK 默认只把 **/*.resx 当 EmbeddedResource，.txt 必须显式声明）")
        return 1

    print(f"\n✅ {len(packs)} 个语言包均已嵌入 exe")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

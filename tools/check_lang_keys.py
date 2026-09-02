#!/usr/bin/env python3
"""
check_lang_keys.py — 本地化键体检。

扫 src/WeaselPanel.App 下的 .cs / .xaml，把所有「长得像本地化键」的字符串字面量
（点分 PascalCase，如 Nav.Diagnostics）抓出来，逐个去三个语言包里查：

  * 缺键        —— 代码引用了，语言包里没有。界面上会直接显示键名。
  * 重复键      —— 同一个键在包里定义了多次（后定义的赢，前面是死行）。
  * 三包不一致  —— en 有、zh-Hans / zh-Hant 没有 → 中文界面会漏出英文。
  * 孤儿键      —— 包里有、代码没引用（只提示，可能是有意为之）。

用法：python3 tools/check_lang_keys.py
退出码：有「缺键」或「重复键」时非 0。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "WeaselPanel.App"
LANG_DIR = APP / "Localization"
PACKS = ["lang.en.txt", "lang.zh-Hans.txt", "lang.zh-Hant.txt"]
SCAN_SUFFIX = (".cs", ".xaml")

def load_pack(path):
    pack, dup = {}, []
    for ln in path.read_text(encoding="utf-8").split("\n"):
        s = ln.strip()
        if not s or s.startswith("#"):
            continue
        i = ln.find(" = ")
        if i <= 0:
            continue
        key = ln[:i].strip()
        if key in pack:
            dup.append(key)
        pack[key] = ln[i + 3:]
    return pack, dup


# 只认明确的调用点，不猜 —— 宁可漏报几个孤儿键，也不要几百条噪音。
#   C#  : T("Key")  /  L10n.Instance.T("Key")  /  StatusFromKey("Key")  /  Add("Key")
#   XAML: {l10n:L Key}
CALL_RES = [
    re.compile(r"\bT\(\s*\"([A-Za-z0-9_.\-]+)\""),
    re.compile(r"\bStatusFromKey\(\s*\"([A-Za-z0-9_.\-]+)\""),
    re.compile(r"\bAdd\(\s*\"([A-Za-z0-9_.\-]+)\""),
    re.compile(r"\{l10n:L\s+([A-Za-z0-9_.\-]+)\s*\}"),
]

# 注释里的示例键（L10n.cs 顶部写着的 "Nav.Apperance"）不算引用
COMMENT_RES = re.compile(r"^\s*(//|///|#)")


def scan_keys():
    found = {}
    for path in sorted(APP.rglob("*")):
        if path.suffix not in SCAN_SUFFIX:
            continue
        if "obj" in path.parts:
            continue
        for n, line in enumerate(path.read_text(encoding="utf-8").split("\n"), 1):
            if COMMENT_RES.match(line):
                continue
            for rx in CALL_RES:
                for m in rx.finditer(line):
                    k = m.group(1)
                    # 语言码（zh-Hans / en / auto）不是本地化键
                    if k in ("auto", "en", "zh-Hans", "zh-Hant"):
                        continue
                    found.setdefault(k, f"{path.relative_to(ROOT)}:{n}")
    return found


def main():
    packs = {}
    for name in PACKS:
        packs[name.split(".")[1]], _ = load_pack(LANG_DIR / name)  # en / zh-Hans / zh-Hant

    dups = {}
    for name in PACKS:
        _, d = load_pack(LANG_DIR / name)
        if d:
            dups[name] = d

    used = scan_keys()
    en = packs["en"]

    missing = {k: v for k, v in used.items() if k not in en}
    orphans = sorted(k for k in en if k not in used)

    hans, hant = packs["zh-Hans"], packs["zh-Hant"]
    gaps = []
    for k in sorted(en):
        for label, pack in (("zh-Hans", hans), ("zh-Hant", hant)):
            if k not in pack:
                gaps.append((k, label))

    print(f"键总数：en={len(en)} zh-Hans={len(hans)} zh-Hant={len(hant)} 代码引用={len(used)}")

    if dups:
        print(f"\n[重复键] {sum(len(v) for v in dups.values())} 处")
        for name, d in dups.items():
            for k in d:
                print(f"   {name}: {k}")

    if missing:
        print(f"\n[缺键] {len(missing)} —— 界面会直接显示键名")
        for k, v in sorted(missing.items()):
            print(f"   {k}   ← {v}")

    if gaps:
        print(f"\n[中文包缺键] {len(gaps)} 处 —— 会漏出英文")
        for k, label in gaps:
            print(f"   {k}   ({label})")

    if orphans:
        print(f"\n[孤儿键] {len(orphans)} —— 包里有但代码没引用（未必是问题）")
        for k in orphans:
            print(f"   {k}")

    if not (dups or missing or gaps):
        print("\nOK：无缺键、无重复、三包齐全。")

    return 1 if (dups or missing) else 0


if __name__ == "__main__":
    sys.exit(main())

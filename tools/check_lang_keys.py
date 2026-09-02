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
CALL_NAME_RES = re.compile(r"\b(T|StatusFromKey|Add)\(")
XAML_RES = re.compile(r"\{l10n:L\s+([A-Za-z0-9_.\-]+)\s*\}")

# 键形字面量：至少含一个点，且首段首字母大写 —— 本仓库所有键无一例外都是
# PascalCase（"auto"/"en" 这类语言码、".yaml" 这类扩展名天然被排除）。
STRING_LIT_RES = re.compile(r"\"([A-Z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_\-]+)+)\"")

# ── 间接引用白名单 ──────────────────────────────────────────────────────
# 这几组键在代码里不是以 T("字面量") 的形式出现，扫描器抓不到，但确实在用。
# 不登记的话它们会混进孤儿列表 —— 孤儿一多就没人看了，真孤儿反而藏得住。
INDIRECT_RES = [
    # 三态下拉的三档：存在 AppOptionsViewModel 的 InlineKeys 数组里，靠下标查 L10n
    (re.compile(r"^AppOptions\.Inline\.(Follow|On|Off)$"), "AppOptionsViewModel.InlineKeys"),
    # 预设显示名：LabelKey 定义在 Core 的 AppOptionsFile.Presets。
    # Core 是跨平台纯逻辑层，不带语言包，键只有字面量没有 T() 调用，扫不到。
    (re.compile(r"^AppOptions\.Preset\.[A-Za-z]+$"), "Core/Rime/AppOptionsFile.cs Presets"),
]


def _indirect_reason(key):
    for res, why in INDIRECT_RES:
        if res.match(key):
            return why
    return None

# 文件名不算键。T("...", Path.Combine(dir, "default.custom.yaml"), ex.Message)
# 这种把路径当参数传进来的写法很常见，不过滤就会被当成缺键。
# 判定必须收紧：本仓库有 Maintenance.Folder.Log 这种以 ".Log" 结尾的真键，
# 所以一要求首字符小写（真正的键都是 PascalCase），二要求扩展名小写比对
# （代码里写路径不会写成 .YAML）。
FILE_EXT_RE = re.compile(r"\.(yaml|yml|txt|exe|dll|json|xml|log|md|bat|cmd|ps1|ini|conf)$")


def _is_file_name(key):
    return not key[0].isupper() and bool(FILE_EXT_RE.search(key))

# 注释里的示例键（L10n.cs 顶部写着的 "Nav.Apperance"）不算引用
COMMENT_RES = re.compile(r"^\s*(//|///|#)")


def _arg_span(text, open_idx):
    """从 open_idx（指向 '('）出发，返回匹配 ')' 的下标；找不齐就返回文件尾。

    只做括号配平，不处理字符串里的括号 —— 本地化调用点不会在字符串里塞括号，
    真塞了只会多扫一截，不会漏。
    """
    depth = 0
    for i in range(open_idx, len(text)):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                return i
    return len(text) - 1


def scan_keys():
    found = {}
    for path in sorted(APP.rglob("*")):
        if path.suffix not in SCAN_SUFFIX:
            continue
        if "obj" in path.parts:
            continue
        text = path.read_text(encoding="utf-8")

        # 注释整段剔除 —— 逐行判断会漏掉「调用点跨行」的情况，
        # 这里先把注释行挖空（保留行数，行号才不会漂）。
        lines = text.split("\n")
        cleaned = ["" if COMMENT_RES.match(ln) else ln for ln in lines]
        body = "\n".join(cleaned)

        def record(key, offset):
            if key in ("auto", "en", "zh-Hans", "zh-Hant") or _is_file_name(key):
                return  # 语言码、文件名，不是本地化键
            found.setdefault(key, f"{path.relative_to(ROOT)}:{body.count(chr(10), 0, offset) + 1}")

        # C#：先定位调用点，再在实参区间里捞所有键形字面量。
        # 这么绕是因为三元写法很常见 —— T(IsBuiltIn ? "Schema.OriginBuiltIn"
        # : "Schema.OriginUser") 里两个键都得算引用，而只匹配 T(" 的正则会整条漏掉，
        # 结果是「键明明在用，体检却报孤儿；真缺了键也查不出来」。
        for m in CALL_NAME_RES.finditer(body):
            end = _arg_span(body, m.end() - 1)
            for lit in STRING_LIT_RES.finditer(body, m.end() - 1, end + 1):
                record(lit.group(1), lit.start())

        # XAML：标记扩展里只有一个键，直接抓。
        for m in XAML_RES.finditer(body):
            record(m.group(1), m.start())

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
    indirect = sorted(k for k in en if k not in used and _indirect_reason(k) is not None)
    orphans = sorted(k for k in en if k not in used and _indirect_reason(k) is None)

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

    if indirect:
        print(f"\n[间接引用] {len(indirect)} —— 不是 T(\"...\") 字面量，扫描器抓不到，已登记")
        for k in indirect:
            print(f"   {k}   ← {_indirect_reason(k)}")

    if orphans:
        print(f"\n[孤儿键] {len(orphans)} —— 包里有但代码没引用（未必是问题）")
        for k in orphans:
            print(f"   {k}")

    if not (dups or missing or gaps):
        print("\nOK：无缺键、无重复、三包齐全。")

    return 1 if (dups or missing) else 0


if __name__ == "__main__":
    sys.exit(main())

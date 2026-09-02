#!/usr/bin/env python3
"""把「应用选项页」的键插进 en / zh-Hans 语言包。

插入位置与侧栏顺序一致：应用选项排在「行为」之后、「词典」之前。
段落标题横线统一 69 划（既有段落都是这个长度，短了会在编辑器里参差）。

只做插入，不负责繁体 —— 繁体由 tools/make_zh_hant.py 从简体转换。
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LANGS = ROOT / "src" / "WeaselPanel.App" / "Localization"
RULE = "─" * 69

NAV = {
    "en": "Per-app Options",
    "zh-Hans": "应用选项",
}

# ── 正文 ──────────────────────────────────────────────────────────────
# 顺序即文件里的顺序：页面骨架 → 表格 → 说明 → 状态 → 预设。
# 预设值带 {0} 占位符，由 VM 用 exe 名填进去 —— 不在这里写死 exe 名，
# 否则三份语言包各写一遍，改一个要改三处。
EN = {
    "AppOptions.Title": "Per-app options",
    "AppOptions.Intro": "Set how the input method behaves when it starts in a given program. Written to the app_options section of weasel.custom.yaml; the key is the program's .exe file name. Deploy after changing.",
    "AppOptions.AddLabel": "Program",
    "AppOptions.Add": "Add",
    "AppOptions.PresetTip": "Pick a common program to fill the box on the right",
    "AppOptions.NewExeTip": "The .exe file name, e.g. notepad.exe — not the window title or the product name",
    "AppOptions.FilterTip": "Filter by .exe file name",
    "AppOptions.Remove": "Remove",
    "AppOptions.RemoveTip": "Removes a program you added. Factory entries can't be removed — they live in weasel.yaml.",
    "AppOptions.ResetAll": "Reset all",
    "AppOptions.ResetAllTip": "Deletes every app_options key this panel wrote; keys you wrote by hand are kept",
    "AppOptions.Column.Exe": "Program (.exe)",
    "AppOptions.Column.Ascii": "English",
    "AppOptions.Column.Vim": "Vim mode",
    "AppOptions.Column.Inline": "Inline preedit",
    "AppOptions.Column.Origin": "Source",
    "AppOptions.AsciiTip": "Start in this program with English input",
    "AppOptions.VimTip": "Esc / Ctrl+C / Ctrl+[ drops back to English automatically",
    "AppOptions.InlineTip": "Draw the pre-edit string inside the program instead of following the global setting",
    "AppOptions.Inline.Follow": "Follow global",
    "AppOptions.Inline.On": "On",
    "AppOptions.Inline.Off": "Off",
    "AppOptions.OriginBuiltIn": "Factory",
    "AppOptions.OriginUser": "Custom",
    "AppOptions.NoRows": "No per-app options yet. Add a program above to give it its own defaults.",
    "AppOptions.NoMatch": "No program matches the filter.",
    "AppOptions.RowCountFormat": "{0} programs",
    "AppOptions.Save": "Save",
    "AppOptions.NoteTitle": "What the three switches do",
    "AppOptions.NoteIntro": "Weasel reads every key under app_options/<exe> and feeds it to Rime. Any other name is accepted without complaint and simply does nothing.",
    "AppOptions.NoteAscii": "Start in English. The factory weasel.yaml already sets this for cmd.exe and conhost.exe.",
    "AppOptions.NoteVim": "Esc, Ctrl+C or Ctrl+[ returns to English — handy in Vim and in terminals.",
    "AppOptions.NoteInline": "Draw the in-progress string inside the program itself. \"Follow global\" leaves it to the appearance page; the other two override it for this program only.",
    "AppOptions.NoteDeploy": "Changes take effect after deploying (Maintenance page → Deploy). Programs already running must be restarted: these options are read once, when the program connects.",
    "AppOptions.Status.Loading": "Reading weasel.custom.yaml…",
    "AppOptions.Status.Loaded": "{0} programs loaded.",
    "AppOptions.Status.LoadFailed": "Read failed: {0}",
    "AppOptions.Status.Saving": "Writing…",
    "AppOptions.Status.Saved": "Saved. {0} programs in effect.",
    "AppOptions.Status.SaveFailed": "Write failed: {0}",
    "AppOptions.Status.Added": "{0} added — save to write it.",
    "AppOptions.Status.Removed": "{0} removed — save to write it.",
    "AppOptions.Status.Duplicate": "{0} is already in the list.",
    "AppOptions.Status.Reset": "Restored to factory behaviour.",
    "AppOptions.DiscardTitle": "Discard changes?",
    "AppOptions.DiscardBody": "You have unsaved changes. Reloading will discard them.",
    "AppOptions.ResetAllTitle": "Reset per-app options?",
    "AppOptions.ResetAllBody": "This deletes every app_options key this panel wrote in weasel.custom.yaml, restoring factory behaviour everywhere.\\n\\nKeys you wrote by hand are kept.",
    "AppOptions.Preset.Cmd": "Command Prompt ({0})",
    "AppOptions.Preset.Conhost": "Console window ({0})",
    "AppOptions.Preset.Powershell": "Windows PowerShell ({0})",
    "AppOptions.Preset.Pwsh": "PowerShell 7 ({0})",
    "AppOptions.Preset.WindowsTerminal": "Windows Terminal ({0})",
    "AppOptions.Preset.Wsl": "WSL ({0})",
    "AppOptions.Preset.Bash": "Git Bash ({0})",
    "AppOptions.Preset.Vscode": "Visual Studio Code ({0})",
    "AppOptions.Preset.VisualStudio": "Visual Studio ({0})",
    "AppOptions.Preset.Idea": "IntelliJ IDEA ({0})",
    "AppOptions.Preset.Pycharm": "PyCharm ({0})",
    "AppOptions.Preset.Vim": "Vim ({0})",
}

ZH = {
    "AppOptions.Title": "应用选项",
    "AppOptions.Intro": "给单个程序单独设定输入法进入时的默认状态。写在 weasel.custom.yaml 的 app_options 段，键是程序的 exe 文件名。改完需要重新部署。",
    "AppOptions.AddLabel": "程序",
    "AppOptions.Add": "添加",
    "AppOptions.PresetTip": "选一个常见程序，右边的输入框会自动填好",
    "AppOptions.NewExeTip": "填 exe 文件名，比如 notepad.exe —— 不是窗口标题，也不是软件名",
    "AppOptions.FilterTip": "按 exe 文件名筛选",
    "AppOptions.Remove": "移除",
    "AppOptions.RemoveTip": "只能移除自己添加的条目。出厂条目在 weasel.yaml 里，移不掉。",
    "AppOptions.ResetAll": "恢复默认",
    "AppOptions.ResetAllTip": "删掉本面板写过的全部 app_options 键；你自己手写的键会保留",
    "AppOptions.Column.Exe": "程序（exe）",
    "AppOptions.Column.Ascii": "默认英文",
    "AppOptions.Column.Vim": "Vim 模式",
    "AppOptions.Column.Inline": "行内显示",
    "AppOptions.Column.Origin": "来源",
    "AppOptions.AsciiTip": "进入这个程序时默认英文",
    "AppOptions.VimTip": "按 Esc / Ctrl+C / Ctrl+[ 时自动切回英文",
    "AppOptions.InlineTip": "把正在拼的字直接画在程序里，而不是跟随全局设置",
    "AppOptions.Inline.Follow": "跟随全局",
    "AppOptions.Inline.On": "开",
    "AppOptions.Inline.Off": "关",
    "AppOptions.OriginBuiltIn": "出厂",
    "AppOptions.OriginUser": "自定义",
    "AppOptions.NoRows": "还没有给任何程序单独设过。在上面加一个程序，就能给它单独定默认值。",
    "AppOptions.NoMatch": "没有匹配的程序。",
    "AppOptions.RowCountFormat": "共 {0} 个程序",
    "AppOptions.Save": "保存",
    "AppOptions.NoteTitle": "三个开关分别管什么",
    "AppOptions.NoteIntro": "小狼毫会把 app_options/<程序> 下的所有键都当成开关交给 Rime。写别的名字不会报错，但也不会有任何效果。",
    "AppOptions.NoteAscii": "进入时默认英文。出厂的 weasel.yaml 已经给 cmd.exe 和 conhost.exe 设了这一条。",
    "AppOptions.NoteVim": "按 Esc / Ctrl+C / Ctrl+[ 时自动切回英文 —— 在 Vim 和终端里很有用。",
    "AppOptions.NoteInline": "把正在拼的字直接画在程序里。「跟随全局」沿用外观页的设置，另外两档只对这一个程序生效。",
    "AppOptions.NoteDeploy": "改完要到「维护」页重新部署才会生效。已经开着的程序要重启：这些选项是程序连上输入法时读一次的。",
    "AppOptions.Status.Loading": "正在读取 weasel.custom.yaml…",
    "AppOptions.Status.Loaded": "已载入 {0} 个程序。",
    "AppOptions.Status.LoadFailed": "读取失败：{0}",
    "AppOptions.Status.Saving": "正在写入…",
    "AppOptions.Status.Saved": "已保存，当前生效 {0} 个程序。",
    "AppOptions.Status.SaveFailed": "写入失败：{0}",
    "AppOptions.Status.Added": "已添加 {0}，记得保存。",
    "AppOptions.Status.Removed": "已移除 {0}，记得保存。",
    "AppOptions.Status.Duplicate": "{0} 已经在列表里了。",
    "AppOptions.Status.Reset": "已恢复为出厂行为。",
    "AppOptions.DiscardTitle": "放弃改动？",
    "AppOptions.DiscardBody": "有未保存的改动，重新载入会丢掉。",
    "AppOptions.ResetAllTitle": "恢复应用选项默认值？",
    "AppOptions.ResetAllBody": "这会删掉本面板在 weasel.custom.yaml 里写过的全部 app_options 键，所有程序回到出厂行为。\\n\\n你自己手写的键会保留。",
    "AppOptions.Preset.Cmd": "命令提示符（{0}）",
    "AppOptions.Preset.Conhost": "控制台窗口（{0}）",
    "AppOptions.Preset.Powershell": "Windows PowerShell（{0}）",
    "AppOptions.Preset.Pwsh": "PowerShell 7（{0}）",
    "AppOptions.Preset.WindowsTerminal": "Windows 终端（{0}）",
    "AppOptions.Preset.Wsl": "WSL（{0}）",
    "AppOptions.Preset.Bash": "Git Bash（{0}）",
    "AppOptions.Preset.Vscode": "Visual Studio Code（{0}）",
    "AppOptions.Preset.VisualStudio": "Visual Studio（{0}）",
    "AppOptions.Preset.Idea": "IntelliJ IDEA（{0}）",
    "AppOptions.Preset.Pycharm": "PyCharm（{0}）",
    "AppOptions.Preset.Vim": "Vim（{0}）",
}

PACKS = {"en": EN, "zh-Hans": ZH}
SECTION_TITLE = {"en": "App options page", "zh-Hans": "应用选项页"}
# 段落标题在两个包里语言不同：英文包写 "Dictionary page"，简体包写「词典页」。
DICT_HEADER = {
    "en": re.compile(r"^# ── Dictionary page\b"),
    "zh-Hans": re.compile(r"^# ── 词典页\b"),
}


def existing_keys(text: str) -> set[str]:
    return {
        line.split(" = ", 1)[0].strip()
        for line in text.splitlines()
        if " = " in line and not line.lstrip().startswith("#")
    }


def insert_nav(text: str, label: str) -> str:
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if line.startswith("Nav.Behavior "):
            lines.insert(i + 1, f"Nav.AppOptions = {label}")
            return "\n".join(lines) + "\n"
    raise SystemExit("找不到 Nav.Behavior，导航键插不进去")


def insert_section(text: str, pack: str, keys: dict[str, str]) -> str:
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if DICT_HEADER[pack].match(line):
            block = [f"# ── {SECTION_TITLE[pack]} {RULE}"]
            block += [f"{k} = {v}" for k, v in keys.items()]
            block.append("")
            lines[i:i] = block
            return "\n".join(lines) + "\n"
    raise SystemExit("找不到 Dictionary page 段落，应用选项段插不进去")


def main() -> int:
    for pack, keys in PACKS.items():
        path = LANGS / f"lang.{pack}.txt"
        text = path.read_text(encoding="utf-8")

        dup = existing_keys(text) & set(keys)
        if dup:
            # 幂等：键已经在了就跳过这个包，而不是报错中断 ——
            # 逐包写入的循环里，第一个包写完后第二个包才失败时，
            # 重跑一次不该把已经写好的那个再写一遍。
            print(f"{path.name}: 已有 {len(dup)} 键，跳过")
            continue

        text = insert_nav(text, NAV[pack])
        text = insert_section(text, pack, keys)
        path.write_text(text, encoding="utf-8")
        print(f"{path.name}: 写入 {len(keys) + 1} 键")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

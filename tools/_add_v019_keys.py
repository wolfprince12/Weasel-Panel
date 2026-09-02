#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
一次性脚本：为 v0.1.9 的三个新页面 + 排版改版补上全部缺失的本地化键。

按「键前缀 → 所属段落」插入，新页面（词典 / 维护 / 配置查看器）整体插到
「备份页」之前 —— 与侧栏导航顺序一致，方便以后按页找文案。

值里的 \\n 会被 L10n.ParsePack 还原成真换行（确认框正文用）。
"""

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
LANG = ROOT / "src" / "WeaselPanel.App" / "Localization"

# ── 文案：key -> (en, zh-Hans) ──────────────────────────────────────────
# 繁体由 tools/make_zh_hant.py 从简体转换，不要在这里手写。
TXT = {
    # 通用
    "Common.Working": ("Working…", "处理中……"),

    # 侧边导航
    "Nav.Dictionary": ("Dictionary", "词典"),
    "Nav.Maintenance": ("Maintenance", "维护"),
    "Nav.Inspector": ("Inspector", "配置查看"),

    # 诊断页
    "Diag.Title": ("Diagnostics", "环境诊断"),
    "Diag.Intro": (
        "Check where Weasel is installed, whether the user directory is ready, "
        "and whether the deployer can be invoked. Run this before changing anything "
        "- it rules out most \"nothing happened\" cases.",
        "先看本机小狼毫装在哪个目录、用户目录是否就绪、部署器能否调用。"
        "改配置前跑一遍，能避开大部分「改了没反应」。",
    ),
    "Diag.NoResults": ("No probe results yet - click \"Run probes\".",
                       "还没有探测结果 —— 点「开始探测」跑一遍。"),
    "Diag.ReportPanel": ("Panel version", "面板版本"),
    "Diag.ReportBuild": ("Build time", "构建时间"),
    "Diag.ReportLang": ("Language pack", "语言包"),

    # 外观页
    "Appearance.Title": ("Appearance", "外观"),
    "Appearance.Intro": (
        "Fonts, colours and layout of the candidate window. Every value is derived "
        "with the same rules as Weasel itself - what you see here is what actually "
        "gets rendered.",
        "候选窗口的字体、配色与排版。所有取值都用与小狼毫本体同一套规则推导，"
        "这里看到的就是实际渲染结果。",
    ),

    # 方案页
    "Schema.NoActive": ("No schema is active yet.", "还没有启用任何方案。"),
    "Schema.NoAvailable": ("Every installed schema is already active.",
                           "所有已安装的方案都已启用。"),
    "Schema.AvailableNote": (
        "\"Available\" lists schemas installed on this machine but not currently active.",
        "「可用方案」列出的是本机已安装、但当前未启用的方案。",
    ),

    # 输入页
    "Input.PageSizeGroup": ("Page size & candidates", "翻页与候选数"),

    # 行为页
    "Behavior.Title": ("Behaviour", "行为"),
    "Behavior.Intro": (
        "Toggles for typing habits: Chinese/English switching, what Shift commits, "
        "notification duration. Deploy after changing.",
        "中英切换、Shift 上屏、通知时长等输入习惯开关。改完记得部署。",
    ),

    # 备份页
    "Backup.NoBackups": ("No backups yet - consider creating one before you change anything.",
                         "还没有备份 —— 改动配置前建议先建一个。"),
    "Backup.NoFiles": ("This backup contains no files.", "这个备份里没有任何文件。"),

    # 关于页
    "About.LanguageIntro": ("The interface follows your system language by default; "
                            "override it here.",
                            "界面语言默认跟随系统，可在此手动覆盖。"),

    # ── 词典页 ──────────────────────────────────────────────────────────
    "Dictionary.Title": ("User dictionary", "用户词典"),
    "Dictionary.Intro": (
        "Edit custom_phrase.txt (full pinyin) and custom_phrase_double.txt "
        "(double pinyin). One entry per line: word<Tab>code<Tab>weight.",
        "编辑 custom_phrase.txt（全拼）与 custom_phrase_double.txt（双拼）。"
        "一行一条：词<Tab>编码<Tab>权重。",
    ),
    "Dictionary.File": ("File", "文件"),
    "Dictionary.File.Full": ("custom_phrase.txt (full pinyin)", "custom_phrase.txt（全拼）"),
    "Dictionary.File.Double": ("custom_phrase_double.txt (double pinyin)",
                               "custom_phrase_double.txt（双拼）"),
    "Dictionary.FilterTip": ("Filter by word, code or weight", "按词、编码或权重过滤"),
    "Dictionary.Add": ("Add", "添加"),
    "Dictionary.Remove": ("Remove", "删除"),
    "Dictionary.Save": ("Save", "保存"),
    "Dictionary.Column.Word": ("Word", "词"),
    "Dictionary.Column.Code": ("Code", "编码"),
    "Dictionary.Column.Weight": ("Weight", "权重"),
    "Dictionary.EntryCountFormat": ("{0} entries", "共 {0} 条"),
    "Dictionary.NoEntries": ("No entries in this file yet.", "这个文件里还没有词条。"),
    "Dictionary.NoMatch": ("No entries match the current filter.",
                           "没有符合当前搜索条件的词条。"),
    "Dictionary.Status.Loading": ("Loading…", "正在读取……"),
    "Dictionary.Status.Loaded": ("Loaded {0} entries", "已读取 {0} 条"),
    "Dictionary.Status.NewFile": ("The file does not exist yet - it will be created "
                                  "when you save.",
                                  "文件尚不存在，保存时会自动创建。"),
    "Dictionary.Status.LoadFailed": ("Failed to read: {0}", "读取失败：{0}"),
    "Dictionary.Status.Added": ("Added a blank entry - fill it in at the bottom of the list.",
                                "已添加一条空白条目，请在列表末尾填写。"),
    "Dictionary.Status.Removed": ("Entry removed.", "已删除该条目。"),
    "Dictionary.Status.Saving": ("Saving…", "正在保存……"),
    "Dictionary.Status.Saved": ("Saved {0} entries. Deploy to apply.",
                                "已保存 {0} 条。需要部署后生效。"),
    "Dictionary.Status.SaveFailed": ("Failed to save: {0}", "保存失败：{0}"),
    "Dictionary.CaveatTitle": ("When do these entries take effect?", "什么时候才会生效？"),
    "Dictionary.CaveatBody": (
        "Rime only loads custom_phrase.txt when the active schema references it via "
        "translator/dictionary. rime-ice ships with that reference; the schemas bundled "
        "with Weasel do not. This page only tells you - it never edits your schema, "
        "because one wrong entry there breaks the whole schema and you would be unable "
        "to type Chinese at all.",
        "只有当当前方案在 translator/dictionary 里挂了 custom_phrase，Rime 才会加载"
        "这个词库。雾凇拼音（rime-ice）出厂就挂了；小狼毫自带的方案默认没挂。"
        "此页只做提示、不代改方案 —— 方案里写错一格会导致整个方案编译失败，"
        "直接打不出中文。",
    ),

    # ── 维护页 ──────────────────────────────────────────────────────────
    "Maintenance.Title": ("Maintenance", "维护"),
    "Maintenance.Intro": (
        "Deploy, sync, browse the directories and clean up logs. Deploying is what "
        "makes every change on the other pages take effect.",
        "部署、同步、查看目录、清理日志。部署是让其他页面的改动真正生效的那一步。",
    ),
    "Maintenance.DeployTitle": ("Deploy", "部署"),
    "Maintenance.DeployIntro": (
        "Writing a *.custom.yaml only records your change; Rime does not read it until "
        "you deploy. The first deploy can take a few minutes because it compiles "
        "dictionaries.",
        "写 *.custom.yaml 只是把改动记下来，Rime 要等你部署后才会去读。"
        "首次部署可能要几分钟，因为要编译词典。",
    ),
    "Maintenance.Deploy": ("Deploy", "部署"),
    "Maintenance.Sync": ("Sync user data", "同步用户数据"),
    "Maintenance.Reinstall": ("Initial deployment", "初始部署"),
    "Maintenance.FolderTitle": ("Directories", "目录"),
    "Maintenance.FolderIntro": (
        "Where Weasel keeps program files, shared data, your configuration and its logs.",
        "小狼毫的程序文件、共享数据、你的配置与日志分别放在哪里。",
    ),
    "Maintenance.Folder.User": ("User directory", "用户目录"),
    "Maintenance.Folder.Program": ("Program directory", "程序目录"),
    "Maintenance.Folder.Shared": ("Shared data", "共享数据"),
    "Maintenance.Folder.Sync": ("Sync directory", "同步目录"),
    "Maintenance.Folder.Log": ("Log directory", "日志目录"),
    "Maintenance.Open": ("Open", "打开"),
    "Maintenance.LogTitle": ("Logs", "日志"),
    "Maintenance.LogIntro": (
        "librime writes one log file per process. They are safe to delete - Weasel "
        "recreates them on the next start.",
        "librime 每个进程写一个日志文件。这些文件可以放心删除，小狼毫下次启动会重新生成。",
    ),
    "Maintenance.NoLogs": ("No log files.", "没有日志文件。"),
    "Maintenance.LogTotal": ("{0} files, {1} in total", "共 {0} 个文件，{1}"),
    "Maintenance.ClearLogs": ("Clear logs", "清空日志"),
    "Maintenance.ClearConfirmTitle": ("Clear logs?", "清空日志？"),
    "Maintenance.ClearConfirmBody": (
        "Delete {0} log file(s) from:\\n{1}\\n\\nThis cannot be undone.",
        "将删除 {0} 个日志文件，位置：\\n{1}\\n\\n此操作不可撤销。",
    ),
    "Maintenance.Status.Cancelled": ("Cancelled.", "已取消。"),
    "Maintenance.Status.Cleared": ("Deleted {0} log file(s).", "已删除 {0} 个日志文件。"),
    "Maintenance.Status.ClearPartial": (
        "Deleted {0} file(s); {1} could not be deleted (in use, or no permission).",
        "已删除 {0} 个，另有 {1} 个删不掉（被占用或没有权限）。",
    ),
    "Maintenance.Status.LogsRefreshed": ("Found {0} log file(s).", "找到 {0} 个日志文件。"),
    "Maintenance.Status.LogScanFailed": ("Failed to scan the log directory: {0}",
                                         "扫描日志目录失败：{0}"),
    "Maintenance.Status.NoDeployer": ("WeaselDeployer.exe was not found - is Weasel "
                                      "installed?",
                                      "没有找到 WeaselDeployer.exe —— 小狼毫装好了吗？"),
    "Maintenance.Status.Done": ("Done.", "已完成。"),
    "Maintenance.Status.AnotherInstance": (
        "The deployer is already running (exit code 1) - this is not a failure. "
        "Wait for it to finish.",
        "部署器已在运行（退出码 1），这不是失败 —— 等它跑完即可。",
    ),
    "Maintenance.Status.Timeout": ("Timed out after 3 minutes.", "超过 3 分钟仍未结束，已超时。"),
    "Maintenance.Status.ExitCode": ("The deployer exited with code {0}.", "部署器退出码 {0}。"),
    "Maintenance.Status.Exception": ("Failed to run the deployer: {0}", "调用部署器失败：{0}"),
    "Maintenance.InstallConfirmTitle": ("Run initial deployment?", "执行初始部署？"),
    "Maintenance.InstallConfirmBody": (
        "Initial deployment rebuilds the whole user workspace and takes much longer "
        "than a normal deploy. Only use it when the user directory is broken or was "
        "wiped.",
        "初始部署会重建整个用户工作区，比普通部署慢得多。"
        "只在用户目录损坏或被清空时才需要它。",
    ),
    "Maintenance.Action.Deploying": ("Deploying - this can take a few minutes…",
                                     "正在部署，可能需要几分钟……"),
    "Maintenance.Action.Syncing": ("Syncing user data…", "正在同步用户数据……"),
    "Maintenance.Action.Installing": ("Running initial deployment - this can take "
                                      "several minutes…",
                                      "正在执行初始部署，可能需要数分钟……"),
    "Maintenance.Err.StartFailed": ("Failed to start the deployer process.",
                                    "部署器进程启动失败。"),
    "Maintenance.Err.Timeout": ("Timed out (process killed).", "已超时（进程被终止）。"),
    "Maintenance.Out.CommandLine": ("Command line: \"{0}\" {1}", "命令行：\"{0}\" {1}"),
    "Maintenance.Out.ExitCode": ("Exit code: {0}", "退出码：{0}"),
    "Maintenance.Out.Elapsed": ("Elapsed: {0} s", "耗时：{0} 秒"),
    "Maintenance.Out.StdOut": ("Standard output: {0}", "标准输出：{0}"),
    "Maintenance.Out.StdErr": ("Standard error: {0}", "标准错误：{0}"),

    # ── 配置查看器 ──────────────────────────────────────────────────────
    "Inspector.Title": ("Inspector", "配置查看"),
    "Inspector.Intro": (
        "The values Rime actually reads, after merging the base file with your "
        "*.custom.yaml patch. Read-only - to change something, use the page that "
        "owns it.",
        "这里显示的是 Rime 实际读到的值 —— 基础文件与你的 *.custom.yaml 补丁合并"
        "之后的结果。只读；要改请去对应的编辑页。",
    ),
    "Inspector.File": ("File", "文件"),
    "Inspector.FilterTip": ("Filter by key path or value", "按键路径或值过滤"),
    "Inspector.Copy": ("Copy all", "复制全部"),
    "Inspector.Locate": ("Locate", "定位"),
    "Inspector.ClearFilter": ("Clear filter", "清除搜索"),
    "Inspector.BaseFile": ("Base file", "基础文件"),
    "Inspector.PatchFile": ("Patch file", "补丁文件"),
    "Inspector.Column.Path": ("Key path", "键路径"),
    "Inspector.Column.Value": ("Effective value", "生效值"),
    "Inspector.Column.Origin": ("Origin", "来源"),
    "Inspector.Origin.Base": ("base", "基础"),
    "Inspector.Origin.Patch": ("patch", "补丁"),
    "Inspector.EntryCountFormat": ("{0} keys", "共 {0} 个键"),
    "Inspector.OverrideCountFormat": ("{0} overridden", "其中 {0} 项被覆盖"),
    "Inspector.NoEntries": ("This file has no keys, or it could not be parsed.",
                            "这个文件里没有键，或者解析失败。"),
    "Inspector.NoMatch": ("No keys match the current filter.", "没有符合当前搜索条件的键。"),
    "Inspector.Status.Loaded": ("Read {0}; merged {1} keys.", "已读取 {0}，合并后共 {1} 个键。"),
    "Inspector.Status.NoBaseFile": ("{0} was not found.", "没有找到 {0}。"),
    "Inspector.Status.Copied": ("Copied {0} keys to the clipboard.",
                                "已复制 {0} 个键到剪贴板。"),
    "Inspector.Status.ClipboardBusy": ("The clipboard is busy - try again.",
                                       "剪贴板被占用，请再试一次。"),
    "Inspector.Copy.BaseLine": ("Base file: {0}", "基础文件：{0}"),
    "Inspector.Copy.PatchLine": ("Patch file: {0}", "补丁文件：{0}"),
    "Inspector.RuleTitle": ("How these values are merged", "这些值是怎么合并出来的"),
    "Inspector.RuleBody": (
        "A same-named file in the user directory replaces the one in the shared data "
        "directory entirely - they are not merged key by key. Then the patch: node of "
        "<name>.custom.yaml is merged in: maps are merged deeply (changing "
        "style/font_point will not wipe style/layout), but lists are replaced as a "
        "whole (schema_list is overwritten completely once you write it).",
        "用户目录里的同名文件会整份取代共享数据目录里的那份，不是逐键合并。"
        "然后再并入 <name>.custom.yaml 的 patch 节点：映射是深度合并"
        "（只改 style/font_point 不会抹掉 style/layout），列表则是整体替换"
        "（schema_list 一写就整份覆盖）。",
    ),
}

# 键前缀 → 归属段落（按段落标题里的关键词匹配）
SECTION_OF = {
    "Common.": "GENERIC",
    "Nav.": "NAV",
    "Diag.": "DIAG",
    "Appearance.": "APPEARANCE",
    "Schema.": "SCHEMA",
    "Input.": "INPUT",
    "Behavior.": "BEHAVIOR",
    "Backup.": "BACKUP",
    "About.": "ABOUT",
    "Dictionary.": "NEW_DICTIONARY",
    "Maintenance.": "NEW_MAINTENANCE",
    "Inspector.": "NEW_INSPECTOR",
}

# 段落关键词（按文件里段落标题出现的顺序）
MARKERS = [
    ("NAV", ("navigation", "导航")),
    ("GENERIC", ("Generic", "通用按钮")),
    ("DIAG", ("Diagnostics page", "诊断页")),
    ("APPEARANCE", ("Appearance page", "外观页")),
    ("SCHEMA", ("Schema page", "方案页")),
    ("INPUT", ("Input / keys", "按键与输入页")),
    ("BEHAVIOR", ("Behavior page", "行为页")),
    ("BACKUP", ("Backup page", "备份页")),
    ("ABOUT", ("About page", "关于页")),
]

BAR = "─" * 69


def make_section(title):
    return [f"# ── {title} " + "─" * max(0, 69 - len(title) - 8)]


def group_keys(lang_index):
    """按归属段落把待插入的 (key, value) 分组，保持 TXT 的书写顺序。"""
    buckets = {}
    for key, pair in TXT.items():
        prefix = next(p for p in SECTION_OF if key.startswith(p))
        buckets.setdefault(SECTION_OF[prefix], []).append(f"{key} = {pair[lang_index]}")
    return buckets


def apply(path, lang_index, new_titles):
    lines = path.read_text(encoding="utf-8").split("\n")
    buckets = group_keys(lang_index)

    # 定位每个段落的标题行下标
    header_at = {}
    for i, ln in enumerate(lines):
        if ln.startswith("# ── "):
            for name, (en_word, zh_word) in MARKERS:
                needle = en_word if lang_index == 0 else zh_word
                if needle in ln:
                    header_at.setdefault(name, i)

    out = []
    i = 0
    while i < len(lines):
        ln = lines[i]

        # 到「备份页」之前插入三个新页面的段落（与侧栏顺序一致）
        if "BACKUP" in header_at and i == header_at["BACKUP"]:
            for name, title in new_titles:
                out.extend(make_section(title))
                out.extend(buckets.get(f"NEW_{name}", []))
                out.append("")
            out.append("")

        out.append(ln)

        # 段落结束时（遇到下一个段落标题或文件尾）把该段的新键追加进去
        for name in header_at:
            if i == header_at[name]:
                # 找到本段的最后一行
                j = i + 1
                while j < len(lines) and not lines[j].startswith("# ── "):
                    j += 1
                body = lines[i + 1:j]
                while body and body[-1].strip() == "":
                    body.pop()
                pending = buckets.get(name, [])
                if pending:
                    body.append("")
                    body.extend(pending)
                body.append("")
                out.extend(body)
                i = j
                break
        else:
            i += 1

    path.write_text("\n".join(out), encoding="utf-8")
    print(f"{path.name}: 写入 {len(TXT)} 键")


def main():
    new_titles = [("DICTIONARY", "Dictionary page"), ("MAINTENANCE", "Maintenance page"),
                  ("INSPECTOR", "Inspector page")]
    new_titles_zh = [("DICTIONARY", "词典页"), ("MAINTENANCE", "维护页"),
                     ("INSPECTOR", "配置查看器页")]

    apply(LANG / "lang.en.txt", 0, new_titles)
    apply(LANG / "lang.zh-Hans.txt", 1, new_titles_zh)
    return 0


if __name__ == "__main__":
    sys.exit(main())

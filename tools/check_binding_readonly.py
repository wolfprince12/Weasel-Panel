#!/usr/bin/env python3
"""
check_binding_readonly.py — XAML 默认 TwoWay 绑定 vs VM 只读属性 体检。

第 5 类「编译期不报、运行期抛 InvalidOperationException」的 XAML 错误：

    <Run Text="{Binding RepoDisplay}" />      ← 默认 BindingMode = TwoWay
    public string RepoDisplay => "...";        ← expression-bodied，只读
    启动即抛：无法对 "...AboutViewModel" 类型的只读属性 "RepoDisplay"
            进行 TwoWay 或 OneWayToSource 绑定。
    （v0.2.5 真机栽过：仓库栏空白、链接可点但文字空白；
     WPF 错误对话框闪退，详细堆栈写到 %TEMP%\\WeaselPanel\\startup.log。）

为什么必须做这件事：
  · WPF 的 Binding 是**运行期**通过反射在 DataContext 上找 public 成员的；
    XAML 编译期甚至 Roslyn 分析器都不验证 {Binding Foo} 的 Foo 是否可写。
  · Run.Text / TextBox.Text / CheckBox.IsChecked / ListBox.SelectedItem 等
    十几个常用属性的 BindingMode 默认 = TwoWay（FrameworkPropertyMetadata
    BindsTwoWayByDefault=true）。一旦绑到的属性是 get-only（expression-bodied
    `=>`、 或 `{ get; }`、 `{ get; init; }`、 `{ get; private set; }`），
    启动时 BIndingExpression.AttachToContext → CheckReadOnly 即崩。
  · 这类错误的隐蔽之处在于「同源代码经常能跑」——比如 `{Binding Path}` 只指向
    Path 一段、TextBlock.Text 默认不是 TwoWay 就不爆；但凡哪天把 TextBlock
    换 Run、或属性从 `{ get; private set; }` 改成 `{ get; }`、或 VM 加了
    expression-bodied 别名... 都是隐性雷。

哪些 target 默认 TwoWay（本 lint 仅盯这些，避免误报）：
    Run / TextBox.Text / RichTextBox.Document
    CheckBox / RadioButton / ToggleButton 的 IsChecked
    ListBox / ComboBox 的 SelectedItem / SelectedIndex / SelectedValue / SelectedItems
    ComboBox.Text / Slider.Value / DatePicker.SelectedDate

显式 Mode= 全部跳过（用户已明知风险）。

排除（不报警）：
    · {Binding RelativeSource=...} / {Binding ElementName=...}
    · {Binding Source=...} / {Binding Converter=...} / {Binding Path=..., ...}
    · 显式 Mode=OneWay / OneTime / OneWayToSource / TwoWay
    · DataContext.* 起始（按 VM 字段引用模式约定不管）
    · WPF 内置属性白名单（沿用 check_binding_paths.py 那一套）

用法：python3 tools/check_binding_readonly.py
退出码：存在风险绑定时非 0。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "WeaselPanel.App"
VM_DIR = APP / "ViewModels"
VIEW_DIR = APP / "Views"


# WPF 内置 target 默认 BindingMode = TwoWay 的常用属性（ElementName, PropertyName）。
TWOWAY_BINDS_BY_DEFAULT = {
    ("Run", "Text"),
    ("TextBox", "Text"),
    ("RichTextBox", "Document"),
    ("CheckBox", "IsChecked"),
    ("RadioButton", "IsChecked"),
    ("ToggleButton", "IsChecked"),
    ("ListBox", "SelectedItem"),
    ("ListBox", "SelectedIndex"),
    ("ListBox", "SelectedValue"),
    ("ListBox", "SelectedItems"),
    ("ComboBox", "SelectedItem"),
    ("ComboBox", "SelectedIndex"),
    ("ComboBox", "SelectedValue"),
    ("ComboBox", "Text"),
    ("Slider", "Value"),
    ("DatePicker", "SelectedDate"),
    ("MenuItem", "IsChecked"),
}


def parse_vm_members(text):
    """分析 .cs 源文件，返回三个集合：
       - readonly: 「{Binding} 拉不显示值」的成员名（按成员名存，不区分大小写）。
         包括三类:
           ① instance expression-bodied `=>`
           ② instance property accessors 中完全无 public set / init / required（即
              `{ get; }` / `{ get; private set; }` 等所有 RW 缺位的实例属性）
           ③ instance / static 字段（`public T Name = ...;`、 `public static readonly T Name = ...;`），
              因为 {Binding} 通过 instance 反射取 public 成员，static 字段根本不被解析，
              等同 silently no-op；本 lint 一并标记
       - writable: 有 public set / init / required 的**实例**属性（WPF Binding 可写回）
       - all: 全部 public 成员（fallback 给 check_binding_paths.py，本 lint 不介入）

    哪种都不会抛。两类只是「崩 vs 空白」区别，本 lint 一并报，目的是阻止所有「
    {Binding} 不会按意图显示」的情况，催作者改 {x:Static} 或加 setter。
    """
    readonly = set()
    writable = set()
    all_pub = set()

    # 模式 A：expression-bodied instance 属性 `public T Name => ...;`
    for m in re.finditer(r"\bpublic\s+(?:[\w<>?,\.\[\]\s]+?)\s+([A-Za-z_]\w*)\s*=>", text):
        name = m.group(1)
        all_pub.add(name)
        readonly.add(name)

    # 模式 B：instance property accessor block `public T Name { ... }`（显式排除 static）
    for m in re.finditer(
        r"\bpublic\s+(?!static\b)([\w<>?,\.\[\]\s]+?)\s+([A-Za-z_]\w*)\s*\{\s*([^}]+?)\s*\}",
        text,
    ):
        prop_type = m.group(1).strip()
        name = m.group(2)
        accessors = m.group(3)
        all_pub.add(name)
        has_setter = bool(
            re.search(r"\bset\b", accessors)
            or re.search(r"\binit\b", accessors)
            or re.search(r"\brequired\b", accessors)
        )
        if has_setter:
            writable.add(name)
        else:
            readonly.add(name)

    # 模式 C：public 字段（含 instance 和 static），一律视为 {Binding} 风险：
    #   · instance 字段有 setter 还好；无 setter（罕见）的 = readonly
    #   · static 字段（即使有 readonly）= silently no-op（Binding 是 instance 反射）
    # 简化为：对所有 public ... = ...; 字段直接归 readonly。设计意图：催作者改
    # `public static readonly T Name = ...;` + `{x:Static vm:Class.Name}` 而非 {Binding}。
    for m in re.finditer(
        r"\bpublic\s+(?:static\s+)?(?:readonly\s+)?([\w<>?,\.\[\]]+?)\s+([A-Za-z_]\w*)\s*[=;]",
        text,
    ):
        name = m.group(2)
        all_pub.add(name)
        readonly.add(name)

    return readonly, writable, all_pub


def find_vm_for_view(xaml_cs):
    code = xaml_cs.read_text(encoding="utf-8")
    m = re.search(r"DataContext\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_]*)", code)
    if not m:
        m = re.search(r"new\s+([A-Z][A-Za-z0-9_]*ViewModel)\s*\(", code)
    return m.group(1) if m else None


def binding_default_mode(binding_arg):
    """如果 binding_arg 显式声明 Mode=...，返回 (mode_str, None)；否则返回 (None, path)。"""
    has_mode = re.search(r",\s*Mode\s*=\s*([A-Za-z]+)", binding_arg)
    if has_mode:
        return has_mode.group(1), None
    return None, None


def extract_binding_path(binding_arg):
    """从 binding_arg 提取 path 第一段。
    - 跳过 {Binding RelativeSource=...}, Source=..., ElementName=..., Converter=...
    - 支持 Path=Foo[, ...]  形
    """
    a = binding_arg.strip()
    if a.startswith(("RelativeSource", "Source", "ElementName", "Converter", "AncestorType")):
        return None
    # 去掉 `Path=` 前缀
    a = re.sub(r"^Path\s*=\s*", "", a)
    # 取第一个逗号前
    head = a.split(",")[0].strip()
    if not head:
        return None
    # 取第一段（Xxx.Yyy 取 Xxx）
    first = head.split(".")[0].strip()
    return first or None


def binding_arg_has_explicit_mode(binding_arg):
    return bool(re.search(r"\bMode\s*=", binding_arg))


def main():
    # 1. 收集每个 VM 的 readonly / writable 集
    vm_props = {}
    for cs in sorted(VM_DIR.glob("*.cs")):
        ro, wr, all_pub = parse_vm_members(cs.read_text(encoding="utf-8"))
        vm_props[cs.stem] = {"readonly": ro, "writable": wr, "all": all_pub}

    # 2. 收集全部 VM 的 public 成员联合（用于 fallback —— 当 binding 第一段
    #    不在对应 VM 时，去兄弟 VM 里找。这与 check_binding_paths.py 一致。）
    all_union_readonly = set().union(*(p["readonly"] for p in vm_props.values())) if vm_props else set()
    all_union_writable = set().union(*(p["writable"] for p in vm_props.values())) if vm_props else set()
    all_union_all = set().union(*(p["all"] for p in vm_props.values())) if vm_props else set()

    violations = []
    scanned = 0

    for xaml in sorted(VIEW_DIR.glob("*.xaml")):
        xaml_cs = xaml.with_suffix(".xaml.cs")
        if not xaml_cs.exists():
            continue
        vm = find_vm_for_view(xaml_cs)
        if not vm:
            continue
        vm_pack = vm_props.get(vm, {"readonly": set(), "writable": set(), "all": set()})

        body = xaml.read_text(encoding="utf-8")
        # 按行扫，匹配 `<ElementName... AttrName="{Binding ...}"` 模式
        # 同时满足 Element-Attr 属于 TWOWAY_BINDS_BY_DEFAULT
        for line_no, line in enumerate(body.split("\n"), 1):
            for em in re.finditer(
                r"<(\w+)([^<>]*?)\b(\w+)\s*=\s*\"\{Binding\s+([^}]+?)\}\"",
                line,
            ):
                element = em.group(1)
                attr = em.group(3)
                binding_arg = em.group(4)

                # 只看默认 TwoWay 的 target
                if (element, attr) not in TWOWAY_BINDS_BY_DEFAULT:
                    continue

                # 用户显式 Mode=... → 不报警（明知风险）
                if binding_arg_has_explicit_mode(binding_arg):
                    continue

                first = extract_binding_path(binding_arg)
                if not first:
                    continue
                if first == "DataContext":
                    continue

                # 在对应 VM 找：先看是否 readonly；若 writable 或 all 都没出现，
                # 留给 check_binding_paths.py 处理。本 lint 只命中「VM 上确认 readonly」。
                if first in vm_pack["writable"] or first in all_union_writable:
                    continue
                if first not in vm_pack["readonly"] and first not in all_union_readonly:
                    continue

                # 命中风险
                violations.append({
                    "file": xaml.name,
                    "line": line_no,
                    "element": element,
                    "attribute": attr,
                    "binding": first,
                    "vm": vm,
                    "snippet": line.strip()[:120],
                })

        scanned += 1

    print(f"审计范围：{scanned} 个 View（每个 View 对应一个 VM 类）")
    if not violations:
        print("OK：所有默认 TwoWay target 的 {Binding X} 引用的 X 都是 VM 上的 writable 属性。")
        return 0

    print(f"\n[TwoWay+get-only 绑定风险] {len(violations)} 个 —— 启动时会抛 "
          f"InvalidOperationException，请改：")
    print("  · VM 字段改 `public static readonly T X = ...;` （属性无依赖时）")
    print("  · XAML 端改 `{x:Static vm:Class.X}`")
    print("  · 或给 X 加 public setter")
    print()
    for v in violations:
        print(f"  {v['file']}:{v['line']}  <{v['element']} {v['attribute']}=\"{{Binding {v['binding']}}}\" />  (VM: {v['vm']})")
        print(f"     ↳ {v['snippet']}")
    return 1


if __name__ == "__main__":
    sys.exit(main())

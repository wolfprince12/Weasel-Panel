#!/usr/bin/env python3
"""
check_binding_paths.py — XAML {Binding} 路径 vs ViewModel 真实属性 体检。

第 4 类「编译期不报、运行期 silently no-op」的 XAML 错误：
    <TextBlock Text="{Binding DeveloperRole}" />
    —— VM 里这个属性实际叫 DeveloperTitle（改名时语言键改了、字段名没跟）。
       编译期不报错、IDE 不一定飘红；运行期该绑定 silently no-op，
       真机上那一栏直接空白，用户还以为「设计如此」。

    <Hyperlink NavigateUri="{Binding RepoUrl}" />
    —— RepoUrl 是 static readonly 字段，但 {Binding} 走 instance 反射，不解析
       static 成员，silently 不绑定（链接区空白，不崩但错）。

为什么必须做这件事：
WPF 的 Binding 是**运行期**通过反射在 DataContext 上找 public 属性/字段的。
XAML 编译期（甚至 Roslyn 分析器）都不验证{Binding Foo} 的 Foo 是否真的存在、
是否是实例成员。所以「拼写错」「字段名改了没跟」「绑了 static 字段」这类错
只有真机看得见，而且常常是「不崩、只是空白」——比崩更难发现。

v0.2.5 真机排查时补的这道体检。核对逻辑：
    · 对每个 View.xaml，找它的 .xaml.cs 里 `DataContext = new XxxViewModel(...)`
      或 `new XxxViewModel(...)` → 确定 View 对应的 VM 类（DataContext 来源）
    · 收集该 VM 类的 public 成员名（字段/属性/init/get/private set 都算）
    · 扫 View.xaml 里所有 {Binding X.Y} / {Binding Path=X.Y}，取第一段 X
    · 若 X 不在 VM 成员、也不在 WPF 内置属性白名单 → 报可疑

排除（避免误报）：
    · {Binding RelativeSource=...}  /  {Binding ElementName=...}  —— 不是属性绑定
    · 属性路径第二段起（X.Y 的 Y）省略
    · WPF FrameworkElement / 通用属性（Margin / Text / ItemsSource / Command ...）

用法：python3 tools/check_binding_paths.py
退出码：存在可疑 Binding 路径时非 0。
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "WeaselPanel.App"
VM_DIR = APP / "ViewModels"
VIEW_DIR = APP / "Views"


def collect_vm_members(cs_path):
    """从 VM .cs 抓 public 字段名 / 属性名（含 init/get/private set/auto-property）。"""
    text = cs_path.read_text(encoding="utf-8")
    members = set()
    pat = re.compile(r"\bpublic\b[^{;=\n]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>|\s*$)", re.MULTILINE)
    for m in pat.finditer(text):
        members.add(m.group(1))
    return members


WPF_BUILTIN = {
    # FrameworkElement
    "Margin", "Padding", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
    "ActualWidth", "ActualHeight",  # 实际宽高：layout 阶段自动更新，可单向绑定
    "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment",
    "Visibility", "Opacity", "IsEnabled", "IsHitTestVisible", "IsVisible",
    "ToolTip", "Tag", "Name", "Uid", "Focusable", "IsTabStop", "TabIndex",
    # 布局
    "Orientation", "Spacing", "ItemContainerStyle",
    # 内容 / 文本
    "Content", "ContentTemplate", "Text", "FontSize", "FontWeight", "FontStyle", "FontFamily",
    "TextWrapping", "LineHeight", "TextAlignment", "TextDecorations", "Foreground", "Background",
    # 图像 / 边框
    "Source", "Stretch", "StretchDirection", "CornerRadius", "BorderThickness", "BorderBrush",
    # Items 控件
    "ItemsSource", "ItemsPanel", "ItemTemplate", "DisplayMemberPath", "SelectedValuePath",
    "SelectedItem", "SelectedIndex", "SelectedValue", "SelectedItems", "IsSelected",
    # 命令
    "Command", "CommandParameter", "CommandTarget", "IsCancel", "IsDefault", "IsPressed",
    "ClickMode", "ContentStringFormat", "IsChecked", "SelectionMode", "ItemContainerStyleSelector",
    # 值范围
    "Value", "Minimum", "Maximum", "IsIndeterminate",
    # 变换 / 滚动
    "RenderTransform", "RenderTransformOrigin",
    "CanContentScroll", "HorizontalScrollBarVisibility", "VerticalScrollBarVisibility",
    # 窗口 / 杂项
    "Title", "WindowStartupLocation", "ShowInTaskbar", "ResizeMode", "WindowState", "SizeToContent",
    "Header", "Description", "Icon", "IsExpanded", "HasItems", "DataContext",
}


def find_vm_for_view(xaml_cs):
    """从 View.xaml.cs 找 DataContext 绑定的 VM 类名。"""
    code = xaml_cs.read_text(encoding="utf-8")
    m = re.search(r"DataContext\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_]*)", code)
    if not m:
        m = re.search(r"new\s+([A-Z][A-Za-z0-9_]*ViewModel)\s*\(", code)
    return m.group(1) if m else None


def main():
    all_vm_members = {}
    for cs in sorted(VM_DIR.glob("*.cs")):
        all_vm_members[cs.stem] = collect_vm_members(cs)
    all_vm_union = set().union(*all_vm_members.values()) if all_vm_members else set()

    violations = []
    scanned = 0

    for xaml in sorted(VIEW_DIR.glob("*.xaml")):
        xaml_cs = xaml.with_suffix(".xaml.cs")
        if not xaml_cs.exists():
            continue
        vm_name = find_vm_for_view(xaml_cs)
        if not vm_name:
            continue
        vm_members = all_vm_members.get(vm_name, all_vm_union)

        body = xaml.read_text(encoding="utf-8")
        binding_segments = set()
        for m in re.finditer(r"\{Binding\s+([^}]+?)\}", body):
            arg = m.group(1).strip()
            if arg.startswith("RelativeSource") or arg.startswith("ElementName") \
               or arg.startswith("Source") or arg.startswith("Converter"):
                continue
            path = arg
            mm = re.match(r"Path\s*=\s*", path)
            if mm:
                path = path[mm.end():]
            path = path.split(",")[0].strip()
            if not path:
                continue
            first = path.split(".")[0]
            if first:
                binding_segments.add(first)

        scanned += 1
        for seg in sorted(binding_segments):
            if seg in vm_members or seg in WPF_BUILTIN:
                continue
            line_no = None
            for i, line in enumerate(body.split("\n"), 1):
                if f"{{Binding {seg}" in line or f"{{Binding Path={seg}" in line:
                    line_no = i
                    break
            violations.append((xaml.stem, vm_name, seg, line_no))

    print(f"审计范围：{scanned} 个 View（各对应一个 VM 类）")
    if not violations:
        print("OK：所有 {Binding} 路径第一段都映射到 View 对应 VM 的 public 成员（或 WPF 内置属性）。")
        return 0

    print(f"\n[可疑 Binding 路径] {len(violations)} 个 —— VM 类上找不到同名属性，也不在 WPF 内置白名单：")
    print("   （典型原因：VM 字段改名未同步 XAML、绑了 static 字段、拼写错）")
    for vclass, vm, seg, ln in violations:
        loc = f"{vclass}.xaml" + (f":{ln}" if ln else "")
        print(f"   {loc:42}  {seg}   (VM: {vm})")
    return 1


if __name__ == "__main__":
    sys.exit(main())

# Weasel Panel（小狼毫控制面板）

Windows 下 [小狼毫（Rime Weasel）](https://github.com/rime/weasel) 输入法的图形化设置面板。

> **姊妹项目**：[`wolfprince12/squirrel-Panel`](https://github.com/wolfprince12/squirrel-Panel) —— macOS 下鼠须管（Squirrel）的同形态控制面板。
> Weasel Panel 是其在 Windows 平台的独立实现，**设计思路与算法资产源自该项目**。

---

## 当前状态

🚧 **开发初期** —— 尚无可用版本，请勿期待下载。

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| P0-0 | 建仓与隔离验证 | ✅ 完成 |
| P1 | Core 内核（YAML 编辑器 / 补丁文件 / 路径探测 / 备份对比 / 自定义短语 / 镜像回退） | ✅ 完成 |
| P1.5 | 外观内核（颜色字面量 / 配色回退链与 alpha 混合 / 内置配色目录 / 出厂默认值表） | ✅ 完成 |
| P1.6 | 布局派生（配置深度合并 / 布局类型覆盖链 / 全屏副作用 / 内边距修正 / 全局与方案两层） | ✅ 完成（**279 测试全绿**） |
| **P2.0** | **首个可运行 exe**（WPF 骨架 + 环境诊断页 + P0 四项探针 + 外观页与配色预览） | ✅ **已出包，待真机验证** |
| P2 | 完整界面（骨架 + 外观页定稿） | 🔶 进行中 |
| P0 | Windows 侧接入面探针（路径定位 / 命名管道 / 部署日志） | ⬜ 待开始（需在 Windows 虚拟机人工验证） |
| P2–P7 | 界面 → 方案与雾凇 → 词库 → 备份 → 紫毫 → 打包发布 | ⬜ 待开始 |

### Core 已完成的模块

| 模块 | 内容 |
| --- | --- |
| `Yaml/` | 逐行手术式编辑器（保注释/引号风格/行尾注释）、最小发射器、缩进归一化、`0x` 颜色保护、只读加载器 |
| `Rime/` | 补丁文件读写（解析失败拒写 / 写前 `.bak` / 写后回读校验）、自定义短语（tabledb）、颜色字面量解析、配色回退链与 alpha 混合、内置配色目录、出厂默认值表、**配置深度合并**、**布局派生状态** |
| `Backup/` | 快照备份 / 整量与部分恢复 / LCS 行级 diff（单列 + 左右双栏） |
| `Platform/` | 小狼毫路径探测（注册表 + 环境变量回退），非 Windows 上自动降级不抛异常 |
| `Net/` | GitHub 请求镜像 fallback（Release / Commit / 下载 + zip 签名校验） |
| `IO/` | 写后回读校验 |

#### 外观内核的三条上游事实

这三点是读上游 `RimeWithWeasel/RimeWithWeasel.cpp` 逐行核实得来的，任何一条搞错，
面板预览就会和真实候选窗对不上：

1. **颜色字面量支持 4 种长度，不只是 6/8 位** —— `#` 或 `0x` 开头 + 3/4/6/8 位十六进制；
   3、4 位按「每位重复两次」扩展成 6、8 位（`0xabc` → `0xaabbcc`）。
   字节序开关是 **per-scheme** 的 `preset_color_schemes/<scheme>/color_format`
   （`argb` / `rgba` / `abgr`，默认 abgr），不是全局键。
2. **绝大多数配色只写 6–10 个键，其余全靠回退链补齐**，且回退链有**顺序依赖** ——
   `comment_text_color` 回退的是 `label_text_color`（前一步的结果），不是 `candidate_text_color`。
   其中序号色与注释色不是简单继承，而是 `blend_colors(前景, 背景)` 的 alpha 混合。
3. **存在别名回退，`border` 会覆盖 `border_width`** ——
   `style/layout/border` ← `border_width`、`corner_radius` ← `round_corner`、
   `hilite_padding_x/y` ← `hilite_padding`。而 weasel.yaml 出厂写的正是 `border_width`，
   面板按 UIStyle 字段名去写 `border` 会让用户的 `border_width` 静默失效。

已以上游原始 `weasel.yaml` 全量验证：36 套内置配色全部解析成功、0 异常。

#### 布局派生的四条上游事实

小狼毫渲染用的不是配置里写的原值，而是**派生后的值**。这四条是读
`RimeWithWeasel.cpp:1164-1361` 逐行核实得来的，缺一条预览就会失真：

1. **布局类型是一条 5 步覆盖链**，后写的键覆盖先写的，且 `fullscreen` 的分支
   依赖上一步 `horizontal` 的结果。链尾 `style/layout/type` 优先级最高。
   ⚠️ 反直觉：`vertical_text: true` 能压掉 `fullscreen`，但 `vertical_text: false`
   **无法**取消全屏 —— 因为 `false` 的映射值就是「当前值」，等于什么都没做。
2. **全屏有三处副作用** —— `max_width = 0`、`inline_preedit = false`、`shadow_radius = 0`。
   前两处都有历史教训（全屏被最大宽度卡住、无候选时死锁）。
3. **内边距会把间距顶上去**（`max` 修正），且横排与竖排文字看的轴相反：
   横排 `spacing ← y`、竖排文字 `spacing ← x`；`hilite_spacing` 更是横排看 x、竖排文字看 y。
   `margin_x` 会被 `max(hilite_padding_x, |margin_x|)` 顶高，但**负号保持** ——
   用户设 `margin_x: -3` 而 `hilite_padding: 5`，实际生效的是 `-5`。
4. **样式分两层** —— 全局层（`weasel.yaml` + 用户 patch，键缺失填出厂默认）
   与**方案层**（输入方案自己的 `style` 段，只覆盖显式存在的键）。
   方案层能覆盖全局外观，面板不区分这两层就会出现「改了半天没反应」。

已以上游原始 `weasel.yaml` 验证：三组别键回退（`border_width` / `round_corner` /
`hilite_padding`）全部命中，派生值与预期逐项一致。

---

## 设计原则

1. **不链接 librime** —— 全部控制走「写 `*.custom.yaml` 补丁 → 调用官方部署器 / 命名管道 → 读产物」。
   librime 升级不会造成本面板崩溃，这是与"直接调 Rime API"方案的根本区别。
2. **只写用户目录** —— 绝不写入 `%PROGRAMFILES%\Rime`。程序目录只读，规避 UAC 与系统目录污染。
3. **核心功能不依赖 IPC** —— 命名管道用于"外观即时生效"的增强体验；即使管道不可用，
   也能降级到 `WeaselDeployer.exe /deploy` 完成全部功能。
4. **保注释写入** —— YAML 修改采用逐行外科手术式编辑，不动用户的注释、键序与排版习惯。
   写前 `.bak` 备份，写后回读校验，解析失败一律拒写。
5. **与出厂默认相同的值不落盘** —— 保持用户配置文件清爽。

## 技术栈

| 项 | 选型 |
| --- | --- |
| 语言 / 框架 | C# / .NET 8 / WPF（Windows 原生窗口） |
| MVVM | CommunityToolkit.Mvvm（MIT） |
| YAML | 只读解析用 YamlDotNet（MIT）；**写入用自研逐行编辑器**（YamlDotNet 不保证注释往返） |
| 注册表 | Microsoft.Win32.Registry（MIT），调用置于平台守卫内，非 Windows 自动降级 |
| 测试 | xunit（**不使用 FluentAssertions** —— 其 v8 起改为 Xceed 商业许可，与 GPL-3.0 冲突） |
| 打包 | zip 绿色版为主（装至 `%LOCALAPPDATA%\Programs\WeaselPanel`） |

## 构建与测试

`WeaselPanel.Core` 是 **net8.0 纯类库、零 Windows 依赖**，因此 macOS / Linux 上也能直接编译并跑测试，
不必先准备 Windows 环境：

```bash
dotnet build WeaselPanel.sln     # 含 WPF 界面层（见下）
dotnet test  WeaselPanel.sln
```

> 解决方案刻意使用传统 `.sln`（而非 .NET 10 引入的 `.slnx`）—— .NET 8 SDK 的 CLI 不识别 slnx。

### 在 macOS 上直接产出 Windows exe

**这是本项目的一个关键前提突破**：WPF 界面层**不需要** Windows 机器来构建。

只要在 csproj 里打开 `EnableWindowsTargeting`，.NET SDK 就会自动下载 Windows 桌面引用包，
在 macOS / Linux 上完成 WPF 项目（含 XAML 编译）的完整构建与发布：

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
<EnableWindowsTargeting>true</EnableWindowsTargeting>
```

一键构建（**只出 x64**，推荐）：

```bash
./build.sh              # 跑测试 + 产出 dist/win-x64/WeaselPanel.exe
./build.sh --no-test    # 跳过测试，改文案/资源时更快
./build.sh --clean      # 清空 dist 后重建
```

等价的原始命令（需要其他架构时用 `-r` 覆盖）：

```bash
dotnet publish src/WeaselPanel.App -c Release -r win-x64 -o dist/win-x64
```

产物为 `WeaselPanel.exe`，**自包含、免安装 .NET 运行时，双击即可运行**。

> **只交付 x64**（2026-09-02 拍板）：Win11 在 x64 上占绝对多数，
> 且 x64 版 exe 在 ARM 版 Windows 上仍可靠模拟层运行。此前曾同时出 arm64，
> 现已废弃 —— `build.sh` 遇到 `dist/win-arm64` 会主动删除，避免误拿。

> `dist/` 已在 `.gitignore` 中排除 —— 单个 exe 约 156 MB，不可入库。
> **Windows 机器仅用于运行验证，不用于构建。**

### WPF 项目的一个坑

`UseWPF=true` 会把隐式 using 集替换为 WPF 专用集（System.Windows 等），
**不再包含 `System.IO` / `System.Linq`**，因此 App 项目有独立的 `GlobalUsings.cs` 补齐。

## Windows 侧的三条反直觉事实

以下均据 `rime/weasel` 源码核实，与「想当然」相反，改动前请先看 `src/WeaselPanel.Core/Platform/WeaselPaths.cs` 文件头：

1. **`RimeUserDir` 注册表值不做环境变量展开。**
   `RimeWithWeasel/WeaselUtility.cpp` 的 `WeaselUserDataPath()` 取到值后直接返回，
   `ExpandEnvironmentStringsW` 只出现在回退分支里。本面板严格复刻——
   若自作聪明去展开 `%AppData%`，当注册表里存的正是含 `%` 的字面值时，
   面板与本体就会指向不同目录，比不展开严重得多。

2. **`InstallDir` 实际写在 `WOW6432Node` 下。**
   `output/install.nsi` 默认 `SetRegView 32`（脚本里有一行 `; recover back to 32bit view`），
   因此 64 位系统上 `WriteRegStr HKLM SOFTWARE\Rime\Weasel "InstallDir"` 落到了
   `HKLM\SOFTWARE\WOW6432Node\Rime\Weasel`——脚本第 208 行的注释正是这个意思。
   故注册表读取 **32 与 64 两个视图都要试**，且优先 32 位视图。

3. **共享数据目录是「模块同级 `data\`」，不是程序目录下的固定子目录。**
   依据是 `WeaselSharedDataPath()` 里的 `GetModuleFileName(NULL)` → 去文件名 → 加 `data`。

## 与 squirrel-Panel 的关系

- **协议**：两者同为 **GPL-3.0**，无协议冲突。
- **代码**：**零耦合**。本仓库不通过 submodule / subtree / symlink 引用 squirrel-Panel，
  也不包含其 Swift 源码；仅复用其设计、算法与测试用例，产出为 C# 原生实现。
- **署名**：`YamlLineEditor` 一脉的算法源自 TriFecta（GPL-3.0，thesadbee），
  移植实现中保留原署名与协议声明。

---

## 许可证

**GPL-3.0**，详见 [LICENSE](./LICENSE)。

- 参考了 [`rime/weasel`](https://github.com/rime/weasel)（GPL-3.0）的协议常量与行为设计。
- 设计参考 [`wolfprince12/squirrel-Panel`](https://github.com/wolfprince12/squirrel-Panel)（GPL-3.0）。
- 第三方依赖：YamlDotNet（MIT）、CommunityToolkit.Mvvm（MIT）。

---

Mr大狼 · 先求稳定，再求创新。

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
| P1 | Core 内核（YAML 编辑器 / 补丁文件 / 颜色 / 写后校验 / 路径探测 / 备份对比 / 自定义短语 / 镜像回退） | ✅ 完成（**173 测试全绿**） |
| P0 | Windows 侧接入面探针（路径定位 / 命名管道 / 部署日志） | ⬜ 待开始（需在 Windows 虚拟机人工验证） |
| P2–P7 | 界面 → 方案与雾凇 → 词库 → 备份 → 紫毫 → 打包发布 | ⬜ 待开始 |

### Core 已完成的模块

| 模块 | 内容 |
| --- | --- |
| `Yaml/` | 逐行手术式编辑器（保注释/引号风格/行尾注释）、最小发射器、缩进归一化、`0x` 颜色保护、只读加载器 |
| `Rime/` | 补丁文件读写（解析失败拒写 / 写前 `.bak` / 写后回读校验）、配色与字节序、自定义短语（tabledb） |
| `Backup/` | 快照备份 / 整量与部分恢复 / LCS 行级 diff（单列 + 左右双栏） |
| `Platform/` | 小狼毫路径探测（注册表 + 环境变量回退），非 Windows 上自动降级不抛异常 |
| `Net/` | GitHub 请求镜像 fallback（Release / Commit / 下载 + zip 签名校验） |
| `IO/` | 写后回读校验 |

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
dotnet build WeaselPanel.sln
dotnet test  WeaselPanel.sln
```

> 解决方案刻意使用传统 `.sln`（而非 .NET 10 引入的 `.slnx`）—— .NET 8 SDK 的 CLI 不识别 slnx。
> 依赖 Windows 的 WPF 界面层与平台适配层（P2 起）只能在 Windows 上构建。

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

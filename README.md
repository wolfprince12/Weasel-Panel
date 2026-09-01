# Weasel Panel（小狼毫控制面板）

Windows 下 [小狼毫（Rime Weasel）](https://github.com/rime/weasel) 输入法的图形化设置面板。

> **姊妹项目**：[`wolfprince12/squirrel-Panel`](https://github.com/wolfprince12/squirrel-Panel) —— macOS 下鼠须管（Squirrel）的同形态控制面板。
> Weasel Panel 是其在 Windows 平台的独立实现，**设计思路与算法资产源自该项目**。

---

## 当前状态

🚧 **规划 / 开发初期（P0）** —— 尚无可用版本，请勿期待下载。

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| P0-0 | 建仓与隔离验证 | ✅ 完成 |
| P0 | Windows 侧接入面探针（路径定位 / 命名管道 / 部署日志） | ⬜ 待开始 |
| P1–P7 | 内核 → 面板 → 打包发布 | ⬜ 待开始 |

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
| 测试 | xunit + FluentAssertions |
| 打包 | zip 绿色版为主（装至 `%LOCALAPPDATA%\Programs\WeaselPanel`） |

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

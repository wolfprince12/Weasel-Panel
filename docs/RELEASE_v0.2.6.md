# Weasel Panel v0.2.6 — Release Notes

## What's new

- **Sidebar cleanup** — removed the misplaced `NavGroup` section labels
  (Appearance / Input / Data / …) that should not have been there; sidebar
  is back to a pure `List`.
- **Appearance panel no longer freezes** — the live candidate-window preview
  module was removed (it was the main cause of the freeze on page open).
- **About panel** — brought into alignment with macOS Squirrel Panel:
  developer card · "more projects" promo cards · runtime status card ·
  project links card.
- **Input page** — added a "Tab / Shift+Tab page-flip" toggle. Semantics
  match Squirrel: `Tab` → `Page_Down`, `Shift+Tab` → `Page_Up`, gated
  by `when: has_menu`.
- **紫毫 (amethyst) correction page** — added a "Lua runtime (librime-lua)"
  detection card: state (Ready / Missing / Not installed) + the Weasel
  install directory + a one-click link to the official `librime-lua`
  release page + a 5-step manual install guide.
  The panel **never** auto-overwrites the system `rime.dll` —
  a wrong-version rime.dll would break the entire IME and there is no way
  to verify a Windows install from macOS.

## Bug fixes

The most important thing in this release is the crash-squashing that happened
during the 0.2.4 → 0.2.5 window. Three separate `XamlParseException` classes
were fixed that the macOS cross-compile build couldn't detect at all:

1. `<Hyperlink>` was placed inside `<WrapPanel>` / `<Border>` /
   `<StackPanel>` — invalid because `Hyperlink` is an `Inline`, not a
   `UIElement`. Moved to `<TextBlock.Inlines>`.
2. `{StaticResource RimeUrl}` was used where `{x:Static vm:...RimeUrl}`
   was meant — `RimeUrl` is a C# `const`, not an `x:Key` in the resource
   dictionary.
3. `RimeUrl` / `WeaselUrl` / `RepoUrl` / `IssuesUrl` / `LuaDownloadUrl` /
   `PromoItem.Url` were declared as `const string` and fed to
   `Hyperlink.NavigateUri` (which is `System.Uri`). The string→Uri
   `TypeConverter` rejects bare hosts like `https://rime.im` on some
   .NET 8 sub-versions. Migrated to `static readonly Uri` and added
   trailing slashes.
4. The "About" page had two `silently no-op` binding bugs that the build
   also couldn't see: `{Binding DeveloperRole}` while the VM property was
   actually `DeveloperTitle` (the language-pack key was renamed, the field
   wasn't), and `{Binding RepoUrl}` against a `static readonly` field
   (WPF `{Binding}` does not resolve static members). Both fixed.
5. `SchemaViewModel.IsDirty` was missing the `HasLoaded` guard, so the
   status bar always showed "unsaved changes" the moment the page opened.
   Now `IsDirty => HasLoaded && Signature() != _baseline`.
6. The "输入方案" (Schemas) page had its center action buttons
   ("启用" / "→ 移除") clipped: the middle `ColumnDefinition Width="52"`
   was narrower than the buttons actually needed, and the side `Width="*"`
   columns had no `MinWidth` floor. Long `Subtitle` strings from the right
   list (`https://github.com/...`) were also pushing the layout across
   the boundary. Fixed via three layered guards: `Auto MinWidth="92"` on
   the middle column, `MinWidth="220"` on both sides, and
   `MaxWidth={Binding ActualWidth, RelativeSource={RelativeSource AncestorType=ListBoxItem}}`
   on every TextBlock inside the ItemTemplates, plus
   `TextTrimming="CharacterEllipsis"` so long subtitles can actually
   truncate inside the column instead of forcing the column to grow.

## 7. Schema panel buttons greyed out / unclickable (command-refresh bug)

After the layout fix above, the "启用" / "→ 移除" / "上移" / "下移" /
"设为默认" buttons in the **Input Schemes** panel rendered correctly but
stayed **greyed out and unclickable** even after selecting a list item.
The toolbar "重新扫描" button (a `Click` handler, not a command) kept
working — which proved the fault was every command's `CanExecute`
returning `false`, not a binding or layout problem.

Root cause: this project's `DelegateCommand` / `RelayCommand`
(`Infrastructure/RelayCommand.cs`) do **not** bridge their
`CanExecuteChanged` event to `CommandManager.RequerySuggested` (grep the
repo — there is no `CommandManager.RequerySuggested += value` wiring).
`DeployCoordinator.cs` even documents this: *"本项目 RelayCommand 的
CanExecuteChanged 不链 CommandManager.RequerySuggested，必须显式
RaiseCanExecuteChanged 才刷新按钮启用态。"* So
`CommandManager.InvalidateRequerySuggested()` is a **no-op** for these
commands. `SchemaViewModel` was the only ViewModel that relied on it
(its `SelectedActive` / `SelectedAvailable` setters and both
`CollectionChanged` handlers called only `InvalidateRequerySuggested`),
whereas every other ViewModel in the repo calls
`RaiseCanExecuteChanged()` explicitly.

Fix: added `RefreshSelectionCommands()` / `RefreshAllCommands()` helpers
in `SchemaViewModel` and call them from the selection setters and the
collection-change handlers, each doing
`((DelegateCommand)XxxCommand).RaiseCanExecuteChanged()` (the `ICommand`
interface has no such method, so the field must be cast to the concrete
type). Selecting an item now immediately enables the relevant buttons.

## 8. 外观字体列表：打开卡顿 + 看不到系统字体

用户真机报外观面板「看不到系统字体」且「面板特别卡顿」。

Root cause: `AppearanceViewModel.FontFamilies` 原是 `static readonly`，在
**类型加载时于 UI 线程同步枚举** `Fonts.SystemFontFamilies`（几百~上千字体，
每个 `FontFamily.Source` 访问会触发字体元数据加载）。字体多的机器上阻塞 UI
数秒 → 面板卡顿；字体服务未就绪或个别字体 `Source` 抛异常时，整个
`.ToArray()` 失败 / 返回空 → 下拉列表为空（「看不到系统字体」）。

Fix:
- 改为 ctor 触发 `LoadFontFamiliesAsync()`：在 `Task.Run` 后台线程枚举系统字体，
  **不再阻塞 UI**（解决卡顿）。
- 枚举内对每个字体 `try/catch` 容错，单个损坏字体不再拖垮整张列表。
- 若系统字体一个都没拿到（字体服务未就绪等），**兜底**填充常用字体
  （Microsoft YaHei / SimSun / Segoe UI / PingFang SC / Consolas 等），
  保证下拉不为空。
- XAML 字体 ComboBox 加 `VirtualizingStackPanel` + `MaxDropDownHeight="320"`，
  几百项下拉不再卡。

## Build system

The macOS `dotnet build` cannot validate any of the five XAML runtime
classes above. To prevent recurrence, `build.sh` now runs **four
additional lint scripts** before publish, and the repo has a
**GitHub Actions Windows runner** workflow for a real-machine smoke
launch:

| Lint | What it catches | Added in |
|---|---|---|
| `tools/check_xaml_resources.py` | `{StaticResource X}` references not in `x:Key` pool | `faf5fd8` |
| `tools/check_uri_consts.py` | `const string X = "https://…"` / `static readonly string X = "https://…"` (latent URI-vs-string bug) | `088b906` |
| `tools/check_binding_paths.py` | `{Binding X}` where `X` is not an instance public member of the ViewModel (field renamed, static field, typo) | `4827cc5` |
| `tools/check_binding_readonly.py` | `{Binding X}` against a default-TwoWay target (`Run.Text` / `TextBox.Text` / `CheckBox.IsChecked` / …) when `X` is an expression-bodied property, a `{ get; }` property, or a `public static readonly` field | 2026-09-03 |
| `.github/workflows/wpf-smoke.yml` | `InitializeComponent()`-time crashes on a real Windows machine | `088b906` |

To activate the Windows smoke CI, the repo owner needs to enable
**Settings → Actions → General → Allow all actions and reusable
workflows** once. After that, every push to `main` will exercise the
launch path automatically.

## Known limitations

- 紫毫 correction can **detect** whether `librime-lua` is present, but
  the user must still **manually** install the Lua-capable `rime.dll`
  over the Weasel install directory. The panel refuses to do this
  automatically because a wrong version would brick the IME and there
  is no way to validate a Windows install from macOS.
- The Weasel panel is **not** code-signed / notarized. The first launch
  on a clean Windows will trigger SmartScreen; the included
  `tools/fix.command` is the one-time bypass.
- Only `win-x64` is shipped. Windows-on-ARM runs the x64 build via
  emulation. (This was an explicit decision: see commit history.)
- Currently the README and the release notes mention the 6-item
  change-list and the lint coverage; there is no separate CHANGELOG
  file — the project is small enough that the commit log serves.

## How to verify which build you're actually running

The `dist/win-x64/WeaselPanel_v0.2.6_<hash>.exe` filename embeds the
git short hash. After double-clicking, the title bar must read
exactly:

```
小狼毫控制面板 v0.2.6 · MM-DD HH:MM · #<hash>
```

The version line in the sidebar's footer will show the same `#<hash>`.
This is deliberate: previous fix-cycles (v0.1.15 → v0.1.16 → v0.2.0)
saw "the fix didn't take effect" reports that turned out to be the user
launching an old Start-menu pinned copy. The version-and-hash filename
ensures that double-clicking a stale shortcut can only fail with
"file not found", never silently run a stale build.

## Download

Once you cut the release, attach the single-file self-contained
executable:

```
dist/win-x64/WeaselPanel_v0.2.6_7b80ee44.exe
```

(`7b80ee44` is the hash of the most recent rebuild that includes every
hotfix and lint. Rebuild with `./build.sh` to refresh the hash on
demand.)

## System requirements

- Windows 10 1809 / Windows 11 (x64)
- .NET 8 runtime is **bundled** (self-contained single-file)
- 160 MB disk, 200 MB RAM while running

## License

GPL-3.0. See `LICENSE`.

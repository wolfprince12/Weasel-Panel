# Weasel Panel v0.2.7 — Release Notes

## What's new

- **Guided librime-lua installer on the 紫毫 (Amethyst) correction page.**
  When the panel detects that the Lua engine is **Absent**
  (`LuaState == Absent`), a highlighted **「安装 Lua 引擎」(Install Lua engine)**
  button appears on the correction card. Clicking it walks the user through a
  safe, backed-up install:

  1. **Confirm** — a Yes/No `MessageBox` explains that dropping a
     wrong-version `rime.dll` can disable the whole IME, so the user opts in
     knowingly.
  2. **Pick the file** — an `OpenFileDialog` accepts `.dll` or `.zip`
     (`.7z` is rejected with a "unzip first" hint). `.zip` is opened with the
     BCL `System.IO.Compression.ZipFile` to locate `rime.dll` + `lua54.dll`
     (no third-party compression library added).
  3. **Overwrite with elevation** — a temporary `.bat` is written that
     `taskkill /f /im WeaselServer.exe`, backs up the existing `rime.dll` to
     `rime.dll.bak`, then copies the Lua-capable `rime.dll` (and `lua54.dll`
     if present) over the Weasel install directory. The `.bat` runs via
     `Process.Start(Verb="runas")` so the UAC prompt grants write access to
     `Program Files`.
  4. **Redeploy** — `WeaselDeployer.RunAsync(env, "/deploy")` reloads Rime.
  5. **Re-detect** — `DetectLuaEngine()` re-runs and the status card updates.
     Success is judged by matching **byte counts** between source and target.

- The correction card's download link now points the user at a public,
  current Lua-capable `rime.dll` they can pick in step 2.

**Why guided, not automatic:** the panel still refuses to *download* or
*auto-pick* a `rime.dll`. A version-mismatched `rime.dll` bricks the entire
IME (candidate window vanishes) and there is no way to validate a Windows
install from macOS. The human stays in the loop for the download; the panel
only automates the risky overwrite + redeploy, with `rime.dll.bak` as the
rollback safety net.

## Bug fixes

- None in this release. v0.2.7 is a feature addition on top of the v0.2.6
  crash-fix baseline (6 XAML runtime classes + the schema command-refresh
  bug + the appearance font freeze).

## Build system

No new lint scripts were added in this release. The six checks from v0.2.6
(`check_lang_keys` / `embed_lang` / `check_xaml_resources` /
`check_uri_consts` / `check_binding_paths` / `check_binding_readonly`) plus
`dotnet publish` and `verify_lang_packs` still gate every build.

One implementation note worth recording so it isn't rediscovered:

- `Services/LuaInstaller.cs` is marked `[SupportedOSPlatform("windows")]`
  and therefore **requires** `using System.Runtime.Versioning;`. Without it
  the macOS cross-compile fails with `CS0246` (The type or namespace name
  'SupportedOSPlatformAttribute' could not be found).

## Known limitations

- The panel **guides** the install but does **not** fetch the Lua-capable
  `rime.dll` for you. Download it from the recommended source (see Download
  below) and pick the file. A `rime.dll` built for a different Weasel version
  can still disable the IME — `rime.dll.bak` is your rollback.
- `.7z` archives are not handled in place. Unzip them first, then pick the
  extracted `rime.dll` (and `lua54.dll` if the archive contains it).
- UAC elevation is required (writing into `Program Files`). If you cancel the
  UAC prompt the file is unchanged and the install reports failure (byte
  counts won't match).
- The Weasel panel is **not** code-signed / notarized. First launch on a
  clean Windows triggers SmartScreen; `tools/fix.command` is the one-time
  bypass.
- Only `win-x64` is shipped. Windows-on-ARM runs the x64 build via emulation.

## How to verify which build you're actually running

The `dist/win-x64/WeaselPanel_v0.2.7_<hash>.exe` filename embeds the git
short hash. After double-clicking, the title bar must read exactly:

```
小狼毫控制面板 v0.2.7 · MM-DD HH:MM · #69e771d88bf3
```

(The `#…` is the **first 12 hex** of the exe's SHA256. The filename uses the
same hash's **first 8 hex** → `69e771d8`. They are the same build; the
filename is just the shorter form.)

The version line in the sidebar's footer shows the same `#69e771d88bf3`.
This is deliberate: previous fix-cycles (v0.1.15 → v0.1.16 → v0.2.0) saw
"the fix didn't take effect" reports that turned out to be the user launching
an old Start-menu pinned copy. The version-and-hash filename ensures that
double-clicking a stale shortcut can only fail with "file not found", never
silently run a stale build.

## Download

Once you cut the release, attach the single-file self-contained executable:

```
dist/win-x64/WeaselPanel_v0.2.7_69e771d8.exe
```

(`69e771d8` is the first 8 hex of the SHA256 of this build. Rebuild with
`./build.sh` to refresh the hash on demand.)

## System requirements

- Windows 10 1809 / Windows 11 (x64)
- .NET 8 runtime is **bundled** (self-contained single-file)
- 160 MB disk, 200 MB RAM while running

## License

GPL-3.0. See `LICENSE`.

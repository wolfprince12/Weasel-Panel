# Weasel Panel v0.2.8 — Release Notes

## What's new

- **Recommended download source for the Lua engine updated.**
  On the 紫毫 (Amethyst) correction page, the **primary** download link now
  points at the official **`rime/librime` Releases**
  (`https://github.com/rime/librime/releases/`), which publishes a public,
  current `dist/lib/rime.dll` that bundles the three plugins
  (`lua` / `octagram` / `charcode`). The community
  `hchunhui/librime-lua` link is kept as a **fallback (备选)** — it now
  redirects to GitHub Actions artifacts that require login, so it is no
  longer reliable as the first choice.

  No behavior change to the guided installer added in v0.2.7; this release
  only steers users to a download they can actually reach and that matches
  what the "安装 Lua 引擎" (Install Lua engine) button expects to receive.

## Bug fixes

- None in this release. v0.2.8 is a documentation / source-URL adjustment on
  top of the v0.2.7 guided-installer feature.

## Build system

No build-script or lint changes in this release. The full gate from v0.2.6
(`check_lang_keys` / `embed_lang` / `check_xaml_resources` /
`check_uri_consts` / `check_binding_paths` / `check_binding_readonly` +
`dotnet publish` + `verify_lang_packs`) still runs on every build.

## Known limitations

- The panel **guides** the install (v0.2.7) but does **not** fetch the
  Lua-capable `rime.dll` for you. Download it from the recommended source
  below and pick the file via the "安装 Lua 引擎" button. A `rime.dll`
  built for a different Weasel version can still disable the IME —
  `rime.dll.bak` (written by the installer) is your rollback.
- `.7z` archives are not handled in place — unzip first, then pick the
  extracted `rime.dll` (+ `lua54.dll` if present).
- UAC elevation is required (writing into `Program Files`); cancelling the
  UAC prompt leaves the file unchanged and the install reports failure.
- The Weasel panel is **not** code-signed / notarized. First launch on a
  clean Windows triggers SmartScreen; `tools/fix.command` is the one-time
  bypass.
- Only `win-x64` is shipped.

## How to verify which build you're actually running

The `dist/win-x64/WeaselPanel_v0.2.8_<hash>.exe` filename embeds the git
short hash. After double-clicking, the title bar must read exactly:

```
小狼毫控制面板 v0.2.8 · MM-DD HH:MM · #1cbea4daf577
```

(The `#…` is the **first 12 hex** of the exe's SHA256. The filename uses the
same hash's **first 8 hex** → `1cbea4da`. They are the same build; the
filename is just the shorter form.)

The version line in the sidebar's footer shows the same `#1cbea4daf577`.

## Download

Attach the single-file self-contained executable:

```
dist/win-x64/WeaselPanel_v0.2.8_1cbea4da.exe
```

(`1cbea4da` is the first 8 hex of the SHA256 of this build. Rebuild with
`./build.sh` to refresh the hash on demand.)

## System requirements

- Windows 10 1809 / Windows 11 (x64)
- .NET 8 runtime is **bundled** (self-contained single-file)
- 160 MB disk, 200 MB RAM while running

## License

GPL-3.0. See `LICENSE`.

#!/usr/bin/env bash
#
#  build.sh — 小狼毫控制面板一键构建（仅 x64）
#
#  用法：
#    ./build.sh              跑测试，然后产出 dist/win-x64/WeaselPanel.exe
#    ./build.sh --no-test    跳过测试（改文案/资源时的快速迭代）
#    ./build.sh --no-verify  跳过「语言包是否真进了 exe」的出包验收（不建议）
#    ./build.sh --clean      清空 dist 后重建
#
#  ── 出包验收：为什么最后还要搜一遍 exe ─────────────────────────────
#  2026-09-02 出过一次事故：语言包漏声明 EmbeddedResource，一个字节都没进 exe，
#  而肉眼自检时往 exe 里搜「小狼毫控制面板」还能搜到 —— 那是 csproj <Product>
#  写进 PE 版本信息的假阳性。所以 tools/verify_lang_packs.py 改用每个语言包里
#  最长的若干条 "Key = Value" 原文当哨兵去真搜，搜不到就让构建失败。
#  这道关卡是「中文 Windows 上界面到底是中文还是裸键名」的最后一道保险，别关掉。
#
#  ── 为什么只出 x64 ────────────────────────────────────────────────
#  2026-09-02 用户明确拍板：不做双架构，只交付 x64。
#  理由充分：Win11 在 x64 上占绝对多数，且 x64 版 exe 在 ARM 版 Windows 上
#  仍可通过模拟层运行；每多出一个架构就多 168MB 产物、多一轮发布与校验成本，
#  而当前阶段收益为零。
#
#  若将来确需 arm64：把 RID 变量改成 win-arm64 再跑一次即可，csproj 本身
#  支持通过 -r 覆盖 RuntimeIdentifier，未写死。
#
#  ── 前提条件 ──────────────────────────────────────────────────────
#  可在 macOS 上直接构建 Windows exe，关键开关是 csproj 里的
#  <EnableWindowsTargeting>true</EnableWindowsTargeting>，勿删。
#

set -euo pipefail

cd "$(dirname "$0")"

RID="win-x64"
OUT="dist/${RID}"
RUN_TESTS=1
RUN_VERIFY=1

for arg in "$@"; do
  case "$arg" in
    --no-test) RUN_TESTS=0 ;;
    --no-verify) RUN_VERIFY=0 ;;
    --clean)   rm -rf dist; echo "已清空 dist/" ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "未知参数：$arg（用 --help 查看用法）" >&2; exit 1 ;;
  esac
done

# python3 有两个用途：构建前校验本地化键位、出包后验收语言包是否进了 exe。
# 缺 python3 时硬失败而不是静默跳过 —— 会静默跳过的关卡等于没有关卡。
PY=""
for cand in python3 /Users/wolfprince/.workbuddy/binaries/python/versions/3.13.12/bin/python3; do
  if command -v "$cand" >/dev/null 2>&1; then PY="$cand"; break; fi
  [ -x "$cand" ] && { PY="$cand"; break; }
done
if [ -z "$PY" ]; then
  echo "找不到 python3。校验与验收都需要它，装好再跑。" >&2
  exit 1
fi

# 旧的多架构产物不再维护，遇到就清掉，避免误拿
if [ -d "dist/win-arm64" ]; then
  echo "发现已废弃的 dist/win-arm64，删除（本项目只交付 x64）"
  rm -rf dist/win-arm64
fi

if [ "$RUN_TESTS" -eq 1 ]; then
  echo "═══ 运行 Core 测试 ═══"
  dotnet test tests/WeaselPanel.Core.Tests -c Release --nologo 2>&1 | tail -3
  echo ""
  echo "═══ 校验本地化键位 ═══"
  "$PY" tools/check_lang_keys.py || {
    echo "" >&2
    echo "本地化键位校验未通过 —— 缺键会在界面上印出裸键名，本次不出包。" >&2
    exit 1
  }
fi

echo "═══ 生成语言包内联常量（L10n 单文件兜底）═══"
"$PY" tools/embed_lang.py || {
  echo "" >&2
  echo "语言包内联常量生成失败 —— 不出包。" >&2
  exit 1
}

# ── XAML 资源引用体检 ──────────────────────────────────────────────────
# 拦 {StaticResource X} / {DynamicResource X} 引用了不存在的 x:Key。
# 这类错编译期不报、dotnet build 也过，但真机 InitializeComponent() 阶段才抛
# XamlParseException → 启动崩溃。v0.2.5 真机栽过两次：
  #   · <Hyperlink 放 <WrapPanel>（容器类型错，本 lint 不覆盖）
  #   · {StaticResource RimeUrl}（VM 的 const 不是资源字典的 x:Key，本 lint 拦的就是这个）
# 写在 publish 之前，fail fast。
echo "═══ 校验 XAML 资源引用 ═══"
"$PY" tools/check_xaml_resources.py || {
  echo "" >&2
  echo "XAML 资源引用未通过 —— 真机启动会因 InitializeComponent() 抛 XamlParseException 而崩，不出包。" >&2
  exit 1
}

# ── VM 端 URL 字面量 const/property 体检 ─────────────────────────────
# 拦 `const string X = "https://..."` 这类字段/属性。XAML 端 NavigateUri 等 Uri
# 类型属性若引用它们，会走 string→Uri TypeConverter，对无尾斜杠主机名或某些
# .NET 8 子版本会抛 ArgumentException，同样运行时崩盘。v0.2.5 #3 真机栽过：
#   RimeUrl/WeaselUrl/RepoUrl/IssuesUrl/LuaDownloadUrl 都是 const string →
#   NavigateUri={x:Static vm:...} 全炸。
# 修法是改 static readonly Uri（Uri 不支持 const，故 readonly）。XAML 端零改动。
echo "═══ 校验 VM 端 URL 字面量声明 ═══"
"$PY" tools/check_uri_consts.py || {
  echo "" >&2
  echo "VM 端 URL 字面量声明违规 —— XAML 端 NavigateUri 等 Uri 类型属性会运行时 TypeConverter 异常，不出包。" >&2
  exit 1
}

# ── XAML {Binding} 路径 vs VM 真实属性 体检 ─────────────────────────
# 拦 {Binding Xxx} 引用了 VM 上不存在的 public 成员 / 绑了 static 字段 /
# VM 字段改名后 XAML 端没跟改。这三类错编译期不报、运行期 silently no-op，
# 真机上相关行直接空白、不崩但严重。v0.2.5 真机排查时补这道：
#   DeveloperRole（VM 实际叫 DeveloperTitle）+ RepoUrl（static 字段不能 Binding）。
# 修法是改 VM 字段名同步 XAML，或 static 字段改 {x:Static} 引用。
echo "═══ 校验 XAML {Binding} 路径 ═══"
"$PY" tools/check_binding_paths.py || {
  echo "" >&2
  echo "XAML {Binding} 路径可疑 —— VM 上找不到对应 public 成员，运行期 silently no-op 不出包。" >&2
  exit 1
}

echo "═══ 发布 ${RID} ═══"
dotnet publish src/WeaselPanel.App -c Release -r "$RID" -o "$OUT" --nologo 2>&1 | tail -2

EXE="${OUT}/WeaselPanel.exe"
if [ ! -f "$EXE" ]; then
  echo "构建失败：未产出 $EXE" >&2
  exit 1
fi

# ── 出包验收：语言包必须真的在 exe 里 ────────────────────────────────
# 缺省开启（$PY 已在脚本前段定位并校验过）。
if [ "$RUN_VERIFY" -eq 1 ]; then
  echo ""
  echo "═══ 验收语言包是否嵌入 exe ═══"
  if ! "$PY" tools/verify_lang_packs.py "$EXE"; then
    echo "" >&2
    echo "出包验收未通过 —— 本次 exe 不可交付。" >&2
    exit 1
  fi
fi

echo ""
echo "✅ 构建完成"
ls -lh "$EXE"
echo "   md5: $(md5 -q "$EXE")"

# ── 把 dist 里的 exe 改名带版本号 + 哈希前 8 位 ─────────────────────────
# 历史教训：v0.1.15/v0.1.16/v0.2.0 三版用户都报"修复无效"，但字节搜索证明
# 新 exe 里硬编码中文全在——根因是 VM 跑的旧副本（Start 菜单/任务栏/资源管理器
# 缓存/快捷方式都可能）。本次构建后**彻底去掉 dist 里的纯名 WeaselPanel.exe**，
# 强制用户只能打开带版本号的文件：任何旧快捷方式字符都匹配不上，
# 双击必然「找不到文件」而不是静默跑旧版。
EXE_DIR="$(dirname "$EXE")"
VER="$(grep -oE '<Version>[^<]+' src/WeaselPanel.App/WeaselPanel.App.csproj | sed 's/<Version>//')"
HASH8="$(shasum -a 256 "$EXE" | awk '{print $1}' | cut -c1-8)"
VERSIONED_NAME="WeaselPanel_v${VER}_${HASH8}.exe"
VERSIONED_PATH="${EXE_DIR}/${VERSIONED_NAME}"

# 清掉 dist 里上一次构建留下的版本号副本（避免老副本和新副本同名冲突）
rm -f "${EXE_DIR}"/WeaselPanel_v*.exe

mv "$EXE" "$VERSIONED_PATH"
echo "   已重命名: WeaselPanel.exe -> ${VERSIONED_NAME}"

# ── 写一份「给用户在 Windows 资源管理器里看」的版本哨兵 ─────────────────
{
    echo "WeaselPanel EXE marker"
    echo "========================="
    echo "version:        ${VER}"
    echo "built_at:       $(date '+%Y-%m-%d %H:%M:%S %Z')"
    echo "executable:     ${VERSIONED_PATH}"
    echo "filename:       ${VERSIONED_NAME}"
    echo "size_bytes:     $(stat -f '%z' "$VERSIONED_PATH")"
    echo "size_human:     $(ls -lh "$VERSIONED_PATH" | awk '{print $5}')"
    echo "md5:            $(md5 -q "$VERSIONED_PATH")"
    echo "sha256_prefix:  $(shasum -a 256 "$VERSIONED_PATH" | awk '{print $1}' | cut -c1-16)"
    echo ""
    echo "How to verify which build you're actually running:"
    echo "  1. Make sure you double-clicked ${VERSIONED_NAME}"
    echo "     (NOT a Start menu shortcut or pinned taskbar entry)"
    echo "  2. Title bar MUST show:"
    echo "     小狼毫控制面板 v${VER} · MM-DD HH:MM · #${HASH8}"
    echo "  3. After run, ${EXE_DIR}/LAST_RUN.txt must have a mtime from 'just now'"
    echo "     (not the build time). If not, the new exe didn't run."
} > "${EXE_DIR}/../BUILD_INFO.txt"

# 同步写一份到 dist 根目录方便单层查看
cp "${EXE_DIR}/../BUILD_INFO.txt" "${EXE_DIR}/BUILD_INFO.txt"

echo "   BUILD_INFO.txt: ${EXE_DIR}/BUILD_INFO.txt"
echo ""
echo "Windows 上必须打开（不要打开 Start 菜单里的旧快捷方式）："
echo "  ${VERSIONED_PATH}"
echo "  （dist 根目录有 BUILD_INFO.txt，开 LAST_RUN.txt 还能验是否真跑了新 exe）"

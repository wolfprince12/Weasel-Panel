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

# 旧的多架构产物不再维护，遇到就清掉，避免误拿
if [ -d "dist/win-arm64" ]; then
  echo "发现已废弃的 dist/win-arm64，删除（本项目只交付 x64）"
  rm -rf dist/win-arm64
fi

if [ "$RUN_TESTS" -eq 1 ]; then
  echo "═══ 运行 Core 测试 ═══"
  dotnet test tests/WeaselPanel.Core.Tests -c Release --nologo 2>&1 | tail -3
fi

echo "═══ 发布 ${RID} ═══"
dotnet publish src/WeaselPanel.App -c Release -r "$RID" -o "$OUT" --nologo 2>&1 | tail -2

EXE="${OUT}/WeaselPanel.exe"
if [ ! -f "$EXE" ]; then
  echo "构建失败：未产出 $EXE" >&2
  exit 1
fi

# ── 出包验收：语言包必须真的在 exe 里 ────────────────────────────────
# 缺省开启。缺 python3 时硬失败而不是静默跳过 —— 会静默跳过的关卡等于没有关卡。
if [ "$RUN_VERIFY" -eq 1 ]; then
  echo ""
  echo "═══ 验收语言包是否嵌入 exe ═══"
  PY=""
  for cand in python3 /Users/wolfprince/.workbuddy/binaries/python/versions/3.13.12/bin/python3; do
    if command -v "$cand" >/dev/null 2>&1; then PY="$cand"; break; fi
    [ -x "$cand" ] && { PY="$cand"; break; }
  done
  if [ -z "$PY" ]; then
    echo "找不到 python3，无法验收语言包。装好 python3 再跑，或用 --no-verify 显式跳过。" >&2
    exit 1
  fi
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
echo ""
echo "Windows 上直接打开：${OUT}/WeaselPanel.exe"

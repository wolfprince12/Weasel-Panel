#!/usr/bin/env bash
#
#  build.sh — 小狼毫控制面板一键构建（仅 x64）
#
#  用法：
#    ./build.sh              跑测试，然后产出 dist/win-x64/WeaselPanel.exe
#    ./build.sh --no-test    跳过测试（改文案/资源时的快速迭代）
#    ./build.sh --clean      清空 dist 后重建
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

for arg in "$@"; do
  case "$arg" in
    --no-test) RUN_TESTS=0 ;;
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

echo ""
echo "✅ 构建完成"
ls -lh "$EXE"
echo "   md5: $(md5 -q "$EXE")"
echo ""
echo "Windows 上直接打开：${OUT}/WeaselPanel.exe"

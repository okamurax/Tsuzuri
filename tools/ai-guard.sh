#!/usr/bin/env bash
# AI に渡さない場所（Tsuzuri/Markdown/AiGuard.cs。目印 .tsuzuri-no-ai）の確かめ（2026-11-09）。
# Claude のツールの呼び出し（Read・Write・Grep・Glob・Bash）を、目印のあるフォルダの下なら断り、ほかは通すかを見る。
# NG が1つでもあれば終了コード 1。AiGuard.cs を変えたら回す。
#
#   bash tools/ai-guard.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/ai-guard" "$work/Tsuzuri/Markdown"
cp "$root/tools/ai-guard/"*.cs "$root/tools/ai-guard/AiGuardCheck.csproj" "$work/tools/ai-guard/"
cp "$root/Tsuzuri/Markdown/AiGuard.cs" "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/ai-guard/AiGuardCheck.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/ai-guard/bin/Release/net8.0/AiGuardCheck.dll"

#!/usr/bin/env bash
# フローのテキスト（Tsuzuri/Markdown/FlowFile.cs。フローのサブページ）の確かめ（2026-11-17）。
# 読んで書き直しても同じになるか、カード・位置・つながり・線の名前・``` の中を正しく読むかを見る。
# FlowResults.cs の添付の渡し方・行き先の頼み方・印の間の取り出し、自動で送る段取り（FlowPlan.cs。分かれ道・確認・繰り返し）も見る。
# NG が1つでもあれば終了コード 1。FlowFile.cs・FlowResults.cs・FlowPlan.cs を変えたら回す。
#
#   bash tools/flow-file.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/flow-file" "$work/Tsuzuri/Markdown"
cp "$root/tools/flow-file/"*.cs "$root/tools/flow-file/FlowFileCheck.csproj" "$work/tools/flow-file/"
cp "$root/Tsuzuri/Markdown/FlowFile.cs" "$root/Tsuzuri/Markdown/FlowResults.cs" "$root/Tsuzuri/Markdown/FlowPlan.cs" "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/flow-file/FlowFileCheck.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/flow-file/bin/Release/net8.0/FlowFileCheck.dll"

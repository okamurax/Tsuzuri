#!/usr/bin/env bash
# 見出しの節を動かす・回す・消す（MarkdownView.Sections.cs）の確かめ（2026-11-07）。
# でたらめな文書にアウトラインの操作をかけ、見出しの並び・親・置き場所（リスト・引用の中か）・ほかのブロックが崩れないかを見る。
# 崩れが1つでもあれば終了コード 1。Sections.cs を変えたら tools/struct-fuzz/Port.cs の写しを合わせて回す。
#
#   bash tools/struct-fuzz.sh [文書の数（既定 30000）] [乱数の種（既定 1）]
#   bash tools/struct-fuzz.sh demo      # 名前を付けた例だけ
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/struct-fuzz" "$work/Tsuzuri/Markdown"
cp "$root/tools/struct-fuzz/"*.cs "$root/tools/struct-fuzz/StructFuzz.csproj" "$work/tools/struct-fuzz/"
cp "$root/Tsuzuri/Markdown/"*.cs "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/struct-fuzz/StructFuzz.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/struct-fuzz/bin/Release/net8.0/StructFuzz.dll" "${@:-30000}"

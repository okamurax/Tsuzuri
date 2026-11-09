#!/usr/bin/env bash
# ページの読み書き（PageDocument）と、サブページ作成・解除・孤立ページ・AI に知らせる行の範囲・表の書き戻しの確かめ（2026-11-09）。
# 崩れた Markdown（閉じていないフェンス・壊れた印・重なった Id・行き先の無いリンク・列のそろわない表・改行の混在など）を
# でたらめに作り、保存して開き直しても中身が変わらないか、作成して解除すると1文字も変わらずに戻るかなどを見る。
# 壊れ（BUG）が1つでもあれば終了コード 1。PageDocument.cs・MainWindow.Pages.cs の作成と解除・TableModel.cs を変えたら回す。
#
#   bash tools/page-fuzz.sh [文書の数（既定 3000）] [乱数の種（既定 1）]
#   bash tools/page-fuzz.sh demo      # 名前を付けた例だけ
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/page-fuzz" "$work/Tsuzuri/Markdown"
cp "$root/tools/page-fuzz/"*.cs "$root/tools/page-fuzz/PageFuzz.csproj" "$work/tools/page-fuzz/"
cp "$root/Tsuzuri/Markdown/"*.cs "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/page-fuzz/PageFuzz.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/page-fuzz/bin/Release/net8.0/PageFuzz.dll" "${@:-3000}"

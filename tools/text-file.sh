#!/usr/bin/env bash
# テキストファイル（.txt）の読み書きの確かめ（2026-11-21。SPEC 10章）。
# UTF-8・BOM 付き・Shift_JIS を読んで、直さずに保存するとバイト列が1つも変わらないか、ページの印・コードフェンスの形の行も本文のままか、
# Shift_JIS で書けない文字があると保存をやめるか、読めない文字コードは開かないか、.md の扱いが変わっていないかを見る。
# でたらめな中身でも、読んで保存して同じバイト列に戻るかを見る。PageDocument.cs のテキストファイルの所を変えたら回す。
#
#   bash tools/text-file.sh [でたらめの数（既定 2000）] [乱数の種（既定 1）]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（page-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/text-file" "$work/Tsuzuri/Markdown"
cp "$root/tools/text-file/"*.cs "$root/tools/text-file/TextFileCheck.csproj" "$work/tools/text-file/"
cp "$root/Tsuzuri/Markdown/"*.cs "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/text-file/TextFileCheck.csproj" -c Release -nologo -v q -clp:ErrorsOnly
"$dotnet" "$work/tools/text-file/bin/Release/net8.0/TextFileCheck.dll" "$@"

#!/usr/bin/env bash
# 検索の置換（Tsuzuri/Markdown/SearchReplace.cs）の確かめ（2026-11-22。SPEC 12章「置換」）。
# でたらめな本文・検索語・置き換える文字で、別に書いた素直な置換（行ごとの Regex.Replace）と1文字も違わないか、件数が同じか、
# 改行が1つも変わらないか、編集の範囲がサロゲートの対を割らないか、改行のある置き換える文字を断るか、
# 置き換えた本文を .md のページに入れて保存・読み直してもページの数と中身が変わらないかを見る。置換を変えたら回す。
#
#   bash tools/replace-fuzz.sh [でたらめの数（既定 20000）] [乱数の種（既定 1）]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（page-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/replace-fuzz" "$work/Tsuzuri/Markdown"
cp "$root/tools/replace-fuzz/"*.cs "$root/tools/replace-fuzz/ReplaceFuzz.csproj" "$work/tools/replace-fuzz/"
cp "$root/Tsuzuri/Markdown/"*.cs "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/replace-fuzz/ReplaceFuzz.csproj" -c Release -nologo -v q -clp:ErrorsOnly
"$dotnet" "$work/tools/replace-fuzz/bin/Release/net8.0/ReplaceFuzz.dll" "$@"

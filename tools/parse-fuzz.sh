#!/usr/bin/env bash
# 部分解析の確かめ（SPEC 8.6）。打った段落だけを解析し直した結果が、全体を解析し直した結果と同じかを、
# 名前を付けた例と、でたらめな書き換えの繰り返しで見る。食い違いが1つでもあれば終了コード 1。
# 解析（Tsuzuri/Markdown/）や、MarkdownView の部分解析（TryPatchModel・TryPatchLines など）を変えたら回す。
#
#   bash tools/parse-fuzz.sh [回数（既定 3000）] [確かめる .md ファイル]
#
# .NET SDK は ~/.dotnet か PATH の dotnet。3000 回で2〜3分かかる。変えたところが大きいときは 20000 回（15分ほど）回す。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（wp-check.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/parse-fuzz" "$work/Tsuzuri/Markdown"
cp "$root/tools/parse-fuzz/ParseFuzz.csproj" "$root/tools/parse-fuzz/Program.cs" "$work/tools/parse-fuzz/"
cp "$root/Tsuzuri/Markdown/"*.cs "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/parse-fuzz/ParseFuzz.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/parse-fuzz/bin/Release/net8.0/ParseFuzz.dll" "$@"

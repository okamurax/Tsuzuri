#!/usr/bin/env bash
# カレンダーの予定の保存先（Tsuzuri/Markdown/ScheduleFile.cs。SPEC 22章）の確かめ（2026-11-20）。
# 読んで書き直しても同じになるか、何日かにまたがる予定・メモ・メモの中の「## 」・崩れた行を正しく読むか、打った日付を読めるかを見る。
# NG が1つでもあれば終了コード 1。ScheduleFile.cs を変えたら回す。
#
#   bash tools/schedule-file.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/schedule-file" "$work/Tsuzuri/Markdown"
cp "$root/tools/schedule-file/"*.cs "$root/tools/schedule-file/ScheduleFileCheck.csproj" "$work/tools/schedule-file/"
cp "$root/Tsuzuri/Markdown/ScheduleFile.cs" "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/schedule-file/ScheduleFileCheck.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/schedule-file/bin/Release/net8.0/ScheduleFileCheck.dll"

#!/usr/bin/env bash
# カレンダーの予定のファズテスト（SPEC 22章。2026-11-21）。でたらめな予定・崩れた .md・繰り返しの決まりを大量に作り、
#   ・読み書きで変わらないか（Serialize → Parse）、崩れた .md を読んでも落ちず、2回目からは同じになるか
#   ・繰り返しの回が、別に書いた素朴な数え方（日を1日ずつ見て決まりを確かめる）と同じか
#   ・週ごとに数えた回（カレンダーが週の行を組む数え方）が、まとめて数えた回と同じか
#   ・消した回・動かした回・決まりの書き直し（Prune）・RRULE の書き直しが崩れないか
#   ・直す操作（ScheduleBook。足す・つかむ・消す・直す・戻す・読み直す）を続けて流しても、書いて読み戻せ、戻すと前の中身に戻るか
# を見る。食い違いが1つでもあれば終了コード 1。ScheduleFile.cs・ScheduleRepeat.cs を変えたら回す。
#
#   bash tools/schedule-fuzz.sh [回数（既定 3000）] [種（既定 1）]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（parse-fuzz.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/schedule-fuzz" "$work/Tsuzuri/Markdown"
cp "$root/tools/schedule-fuzz/"*.cs "$root/tools/schedule-fuzz/ScheduleFuzz.csproj" "$work/tools/schedule-fuzz/"
cp "$root/Tsuzuri/Markdown/ScheduleFile.cs" "$root/Tsuzuri/Markdown/ScheduleRepeat.cs" "$work/Tsuzuri/Markdown/"
[ -f "$root/Tsuzuri/Markdown/ScheduleBook.cs" ] && cp "$root/Tsuzuri/Markdown/ScheduleBook.cs" "$work/Tsuzuri/Markdown/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/schedule-fuzz/ScheduleFuzz.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/schedule-fuzz/bin/Release/net8.0/ScheduleFuzz.dll" "$@"

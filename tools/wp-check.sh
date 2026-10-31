#!/usr/bin/env bash
# WordPress の変換の確かめ（SPEC 20章）。サイトの全記事を読み（GET だけ）、エディタと同じ変換で往復させて、中身が化けないかを見る。
# 食い違いが1件でもあれば終了コード 1。WordPress の変換（Tsuzuri/WordPress/）を変えたら必ず回す（コミットの前にフックが回す。tools/git-hooks/pre-commit）。
#
#   bash tools/wp-check.sh [wordpress.json]
#
# wordpress.json を渡さなければ、exe の隣のもの（Release → Debug の順）を使う。.NET SDK は ~/.dotnet か PATH の dotnet。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
settings="${1:-}"
if [ -z "$settings" ]; then
    for candidate in "$root/Tsuzuri/bin/Release/wordpress.json" "$root/Tsuzuri/bin/Debug/wordpress.json"; do
        if [ -f "$candidate" ]; then settings="$candidate"; break; fi
    done
fi
if [ -z "$settings" ]; then
    echo "wordpress.json が見つかりません（Tsuzuri/bin/Release か Debug に置くか、引数で渡す）。" >&2
    exit 2
fi

dotnet="$HOME/.dotnet/dotnet"
[ -x "$dotnet" ] || dotnet="dotnet"

# Dropbox の上でビルドすると obj/ を取り合うので、作業用のフォルダにコピーしてからビルドする（check-build.sh と同じ）
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tools/wp-check" "$work/Tsuzuri/WordPress"
cp "$root/tools/wp-check/WpCheck.csproj" "$root/tools/wp-check/Program.cs" "$work/tools/wp-check/"
cp "$root/Tsuzuri/WordPress/"*.cs "$work/Tsuzuri/WordPress/"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" build "$work/tools/wp-check/WpCheck.csproj" -c Release -nologo -v q -clp:ErrorsOnly >/dev/null
"$dotnet" "$work/tools/wp-check/bin/Release/net8.0/WpCheck.dll" "$settings"

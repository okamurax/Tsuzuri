#!/usr/bin/env bash
# AI連携ツール（AiLink）が Mac でコンパイルできるかを確かめる（tools/check-build.sh の AiLink 版）。動かすのは Windows で。
# XAML は Mac ではコンパイルできないので、XAML から作られる部品（x:Name の付いた部品と、イベントのつなぎ）の代わりを
# MainWindow.xaml から作って足す（XamlStub.cs）。XAML そのものの誤りは、Windows のビルドで確かめる。
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

# AiLink.csproj は古い形で、コンパイルするファイルを1つずつ並べている。並べ忘れると Mac では通って Windows で通らないので、先に確かめる
missing=0
while read -r file; do
    if ! grep -qF "Include=\"${file//\//\\}\"" "$root/AiLink/AiLink.csproj"; then
        echo "AiLink.csproj に並んでいないファイル: AiLink/$file" >&2
        missing=1
    fi
done < <(cd "$root/AiLink" && find . -name '*.cs' -not -path './obj/*' -not -path './bin/*' | sed 's#^\./##')
[ "$missing" = 0 ] || { echo "AiLink.csproj の <Compile Include> に足してください" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/main"

(cd "$root/AiLink" && find . -name '*.cs' -not -path './obj/*' -not -path './bin/*' -not -path './Properties/*') | while read -r file; do
    mkdir -p "$work/main/src/$(dirname "$file")"
    cp "$root/AiLink/$file" "$work/main/src/$file"
done

# Tsuzuri と同じファイルを使う分（AiLink.csproj の <Compile Include="..\Tsuzuri\…"><Link>…）も写す（2026-11-26）
grep -o 'Compile Include="\.\.\\Tsuzuri\\[^"]*"' "$root/AiLink/AiLink.csproj" | sed 's/^Compile Include="\.\.\\//; s/"$//; s#\\#/#g' | while read -r file; do
    [ -f "$root/$file" ] || { echo "AiLink.csproj がつなぐファイルが無い: $file" >&2; exit 1; }
    mkdir -p "$work/main/src/shared/$(dirname "$file")"
    cp "$root/$file" "$work/main/src/shared/$file"
done

python3 - "$root/AiLink/MainWindow.xaml" > "$work/main/src/XamlStub.cs" <<'PY'
import re, sys
xaml = open(sys.argv[1], encoding="utf-8").read()
types = {"TextBlock": "System.Windows.Controls.TextBlock", "Grid": "System.Windows.Controls.Grid",
         "ColumnDefinition": "System.Windows.Controls.ColumnDefinition", "Border": "System.Windows.Controls.Border",
         "Button": "System.Windows.Controls.Button", "TextBox": "System.Windows.Controls.TextBox",
         "ScrollViewer": "System.Windows.Controls.ScrollViewer", "StackPanel": "System.Windows.Controls.StackPanel",
         "DockPanel": "System.Windows.Controls.DockPanel", "GridSplitter": "System.Windows.Controls.GridSplitter",
         "RowDefinition": "System.Windows.Controls.RowDefinition"}
handlers = {"Click": "System.Windows.RoutedEventHandler",
            "DragCompleted": "System.Windows.Controls.Primitives.DragCompletedEventHandler",
            "MouseLeftButtonDown": "System.Windows.Input.MouseButtonEventHandler",
            "MouseDoubleClick": "System.Windows.Input.MouseButtonEventHandler",
            "Loaded": "System.Windows.RoutedEventHandler"}
print("#pragma warning disable 0649   // 代わりの部品は、XAML が入れるので割り当てない")
print("namespace AiLink {")
print("  public partial class MainWindow {")
for tag, name in re.findall(r'<([A-Za-z]+)\b[^>]*?\bx:Name="([^"]+)"', xaml, re.S):
    if tag not in types: sys.exit("XamlStub: 型が分からない部品 " + tag)
    print("    internal %s %s;" % (types[tag], name))
print("    public void InitializeComponent() {")
for event, method in sorted(set(re.findall(r'\b(\w+)="(\w+)"', xaml))):
    if event in handlers and not method[0].islower():
        print("      { %s h = %s; }" % (handlers[event], method))
print("    }")
print("  }")
print("  public partial class App { public void InitializeComponent() { } }")
print("}")
PY

cat > "$work/main/Main.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>7.3</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Microsoft.VisualBasic" />
    <Reference Include="System.Runtime.Serialization" />
    <Reference Include="System.Web.Extensions" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.IO.Compression" />
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4258.31" />
  </ItemGroup>
</Project>
XML

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$HOME/.dotnet/dotnet" build "$work/main/Main.csproj" -c Release -nologo \
    -p:EnableWindowsTargeting=true -clp:ErrorsOnly
echo "コンパイルできました（AiLink の C#。XAML そのものは Windows で確かめる）。"

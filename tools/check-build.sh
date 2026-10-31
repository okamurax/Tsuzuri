#!/usr/bin/env bash
# Mac でコンパイルが通るかを確かめる。コンパイルが通るかを見るだけで、できた DLL は使わない（動かすのは Windows で）。
#
# 1. 表エディタ（TableEditor）・図エディタ（DrawingEditor）をまるごと
# 2. 本体（Tsuzuri）の C# をまるごと。XAML は Mac ではコンパイルできないので、XAML から作られる部品
#    （x:Name の付いた部品と、イベントのつなぎ）の代わりを MainWindow.xaml から作って足す（XamlStub.cs）。
#    XAML そのもの（見た目・リソース）の誤りは、Windows のビルドで確かめる
#
# - Dropbox の上でビルドすると、obj/ を Windows の Visual Studio と取り合うので、作業用のフォルダにコピーしてからビルドする
# - Windows 向けの部品（WPF）を参照するので EnableWindowsTargeting を付ける
# - .NET SDK は ~/.dotnet に入れたもの（dotnet-install.sh。2026-10-17）
# - 本体の NuGet の版（AvalonEdit・Markdig・WebView2）は Tsuzuri.csproj と揃える
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/te" "$work/de" "$work/main"
cp "$root/TableEditor/"*.cs "$root/TableEditor/TableEditor.csproj" "$work/te/"
cp "$root/DrawingEditor/"*.cs "$root/DrawingEditor/DrawingEditor.csproj" "$work/de/"

(cd "$root/Tsuzuri" && find . -name '*.cs' -not -path './obj/*' -not -path './bin/*' -not -path './Properties/*') | while read -r file; do
    mkdir -p "$work/main/src/$(dirname "$file")"
    cp "$root/Tsuzuri/$file" "$work/main/src/$file"
done

# XAML から作られる部品の代わり。x:Name の付いた部品はフィールドに、イベントは型の合う関数か確かめる代入にする。
python3 - "$root/Tsuzuri/MainWindow.xaml" > "$work/main/src/XamlStub.cs" <<'PY'
import re, sys
xaml = open(sys.argv[1], encoding="utf-8").read()
types = {"TextBlock": "System.Windows.Controls.TextBlock", "Grid": "System.Windows.Controls.Grid",
         "ColumnDefinition": "System.Windows.Controls.ColumnDefinition", "Border": "System.Windows.Controls.Border",
         "Button": "System.Windows.Controls.Button", "TextBox": "System.Windows.Controls.TextBox",
         "ItemsControl": "System.Windows.Controls.ItemsControl", "TreeView": "System.Windows.Controls.TreeView",
         "ScrollViewer": "System.Windows.Controls.ScrollViewer", "StackPanel": "System.Windows.Controls.StackPanel",
         "DockPanel": "System.Windows.Controls.DockPanel", "GridSplitter": "System.Windows.Controls.GridSplitter",
         "RowDefinition": "System.Windows.Controls.RowDefinition"}
handlers = {"Click": "System.Windows.RoutedEventHandler",
            "DragCompleted": "System.Windows.Controls.Primitives.DragCompletedEventHandler",
            "MouseLeftButtonDown": "System.Windows.Input.MouseButtonEventHandler",
            "PreviewMouseLeftButtonDown": "System.Windows.Input.MouseButtonEventHandler",
            "SizeChanged": "System.Windows.SizeChangedEventHandler",
            "TextChanged": "System.Windows.Controls.TextChangedEventHandler",
            "Loaded": "System.Windows.RoutedEventHandler"}
print("#pragma warning disable 0649   // 代わりの部品は、XAML が入れるので割り当てない")
print("namespace Tsuzuri {")
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

cat > "$work/main/Main.csproj" <<'EOF'
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
    <PackageReference Include="AvalonEdit" Version="6.3.1.120" />
    <PackageReference Include="Markdig" Version="0.33.0" />
    <Reference Include="Microsoft.VisualBasic" />
    <Reference Include="System.Runtime.Serialization" />
    <Reference Include="System.Web.Extensions" />
    <Reference Include="System.Drawing" />
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4258.31" />
    <ProjectReference Include="../te/TableEditor.csproj" />
    <ProjectReference Include="../de/DrawingEditor.csproj" />
  </ItemGroup>
</Project>
EOF

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$HOME/.dotnet/dotnet" build "$work/main/Main.csproj" -c Release -nologo \
    -p:EnableWindowsTargeting=true -clp:ErrorsOnly
echo "コンパイルできました（表エディタ・図エディタと、本体の C#。XAML そのものは Windows で確かめる）。"

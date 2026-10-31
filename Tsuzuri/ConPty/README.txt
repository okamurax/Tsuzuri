AI の欄の端末で使う、新しい ConPTY（SPEC 19章。2026-10-30）

NuGet の Microsoft.Windows.Console.ConPTY 1.24.261001001（MIT。https://github.com/microsoft/terminal）から取り出したもの。
Windows Terminal が使っているのと同じ conpty.dll と OpenConsole.exe。

  x64\conpty.dll              エディタが x64 で動いているとき
  x64\x64\OpenConsole.exe     　x64 の Windows で
  x64\arm64\OpenConsole.exe   　ARM の Windows で（x64 のエディタが ARM の上で動くとき）
  arm64\conpty.dll            エディタが ARM64 で動いているとき
  arm64\arm64\OpenConsole.exe

conpty.dll は、自分のフォルダの下の「Windows の種類」のフォルダから OpenConsole.exe を探す（パッケージの配置と同じ）。
このフォルダを消すか、AI の欄のタブの右クリックで「Windows 標準の conhost を使う」にすると、前と同じ Windows 標準の conhost に戻る。

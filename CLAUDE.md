# Tsuzuri の運用ルール

- **機能を足したり変えたりしたら、README.md を必ず最新にする。** 主な機能の表、AI の欄・WordPress などの説明、設定ファイル、構成、使っているもの（ライブラリとライセンス）が今の動きと合っているかを見直す。細かい仕様は SPEC.md に書き、README には利用者と開発者が最初に知るべきことだけを書く
- このリポジトリは GitHub に公開する。パスワード・個人のパス・接続先（`wordpress.json`・`app.json`）・個人の名前やサイト名をコードと文書に書かない。例は `example.com`・`mysite`・`xxxx` を使う
- コンパイルの確認は Mac で `bash tools/check-build.sh`。XAML は Windows のビルドでしか確かめられない

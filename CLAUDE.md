# Tsuzuri の運用ルール

- **機能を足したり変えたりしたら、README.md を必ず最新にする。** 主な機能の表、AI の欄・WordPress などの説明、設定ファイル、構成、使っているもの（ライブラリとライセンス）が今の動きと合っているかを見直す。細かい仕様は SPEC.md に書き、README には利用者と開発者が最初に知るべきことだけを書く
- このリポジトリは GitHub に公開する。パスワード・個人のパス・接続先（`wordpress.json`・`app.json`）・個人の名前やサイト名をコードと文書に書かない。例は `example.com`・`mysite`・`xxxx` を使う
- コンパイルの確認は Mac で `bash tools/check-build.sh`。XAML は Windows のビルドでしか確かめられない
- **WordPress の変換（`Tsuzuri/WordPress/`）を変えたら、`bash tools/wp-check.sh` を回し、OK でなければコミットしない。** サイトの全記事で、送る HTML の見える文字が元と同じか・往復で変わらないかを見る。問題の出た記事と例だけで「確かめた」としない（それで何度も取りこぼした。2026-10-31）。コミットの前のフック（`tools/git-hooks/pre-commit`）も同じものを回す。消すか残すかを決めるのは、出力の「HTML のまま残る」一覧を見てから。効果があるか分からないものは、思い込みで消さずに利用者に尋ねる
- 解析（`Tsuzuri/Markdown/`）や MarkdownView の部分解析（`TryPatchModel`・`TryPatchLines` など）を変えたら、`tools/parse-fuzz/Program.cs` の写しを合わせ、`bash tools/parse-fuzz.sh` が OK になってからコミットする
- 変更を確かめるための試験は、頼まれていなくても足してよい。残す価値のあるものは `tools/` に置く。公開されるので、絶対パス・個人の名前やサイト名・パスワード・鍵・接続先を入れない

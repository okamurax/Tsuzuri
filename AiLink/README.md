# AI連携ツール（AiLink）

**開いている Excel を、AI（Codex CLI）に読ませ・書かせるための Windows 用のツール。**

左の列に、いま開いている Excel のブックとシートの一覧。右に、AI と話す会話のタブ（中は端末で、Codex CLI がそのまま動く）。
一覧でシートを選んで話しかけると、そのシートが AI にとっての「このシート」になります。
AI は、利用者の許可をもらってから Excel に書きます。保存はしません。

Windows 用のデスクトップアプリ（WPF / .NET Framework 4.8 / C# 7.3）です。Tsuzuri の AI の欄と Excel の読み書きを切り出した単独のソフトで、Tsuzuri のプロジェクトは参照しません。

> 細かい仕様は [SPEC.md](SPEC.md) にあります。画面の文字と文書は日本語だけです。

---

## 主な機能

| 機能 | 中身 |
|---|---|
| Excel で AI にいつも伝えること | 一覧の一番下の1行。押すとメモ帳が開き、Excel を扱うときに AI へいつも伝える前提（言葉の意味・書き方の好み・社内の決まりなど）を書ける。次に開く会話から AI に渡り、Excel を読む・書くときにだけ当てはめる。量の多い表は Excel に置き、ここには「○○.xlsx の単価表を見る（そのブックを Excel で開いておく）」とだけ書く |
| Excel の一覧（左の列） | 開いているブックと、その下にシート。選び方はエクスプローラーと同じ（押すとそれだけ・Ctrl で足す外す・Shift で範囲。ブックの行はそのブックのシート全部。何もない所を押すと全部外す）。保存していない変更があるブックは名前の右に「※」。窓に戻るたびと「更新」で読み直す |
| 会話のタブ（右） | タブごとに別の PowerShell が動き、その中で Codex を起動する。名前は `#1`・`#2`…。「＋」で足し、× で閉じる。閉じたときに開いていた会話は、次の起動で続きとして開き直す |
| 今の状態を AI に渡す | 話しかけるたびに、この会話の名前・ほかの会話・一覧で選んだシートの名前を、頼みに添えて渡す（シートの中身・Excel で前に出ているものは渡さない） |
| Excel を読む・書く | AI が `excel.ps1` で、開いているブックのどのシートも読める・書ける・COM の台本で操作できる（下の「AI が Excel を扱うしくみ」） |
| タブどうしの伝言 | AI が `talk.ps1` で、ほかのタブの AI に伝言を送る。相手の手が空いていれば、Enter を押さなくても渡る |
| ファイルを落とす | 会話の欄にファイルやフォルダを落とすと、そのパスが入力欄に入る |
| Codex の入れ方の案内 | Codex が見つからなければ、会話のタブの代わりに入れ方の案内を出す |

## 動作環境

- Windows 10 以降（x64 / ARM64）
- .NET Framework 4.8
- WebView2 ランタイム（会話のタブの端末で使う。Windows 11 には入っている）
- Excel（デスクトップ版）
- [Codex CLI](https://learn.chatgpt.com/docs/codex/cli)（OpenAI）。初めて動かすときに ChatGPT のアカウントでサインインする
- 任意: exe の隣の `git` フォルダに置いた MinGit（あれば、会話のタブの PATH に足す）

### Codex が入っていないとき

会話のタブを開かず、右に入れ方の案内を出します。

- 「入れる（x64）」「入れる（ARM64）」: 別の PowerShell の窓で、OpenAI の公式の入れる手順（`irm https://chatgpt.com/codex/install.ps1 | iex`）を動かす。この PC に合うと見た版のボタンを青くする。管理者の権限は要らない
- 「もう一度確かめる」「コマンドをコピー」「説明のページ」
- 入れる窓を閉じて戻ると、自動で探し直す（案内を出している間は 3 秒ごとにも探す）。見つかれば案内を消して会話のタブを開く
- 入れる場所を変えているときは、`ailink.json` の `agentExecutable` に codex.exe の場所を書く（下の「設定ファイル」）

探す順: `agentExecutable` → 環境変数 `CODEX_INSTALL_DIR` → `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` → PATH（今のプロセス・利用者・PC の設定を読み直す）の `codex.exe`・`codex.cmd` → `%APPDATA%\npm\codex.cmd`。この PC で動かない CPU 向けの exe（x64 の PC の ARM64 版）は見つからなかったことにします。

## 初めて使うとき

1. AiLink を起動すると、会話のタブで Codex が起動する（初めてならサインイン）
2. Codex が **「Hooks need review」** と尋ねてきたら、**2 を選んで信頼する**。
   AiLink は会話を始めるフォルダに Codex のフック（`.codex\hooks.json`）を置いていて、その中身が変わるたび（AiLink を更新したときなど）に Codex が尋ね直します
3. 左の Excel の一覧でシートを選び、右で話しかける

Codex は最初に話しかけるまでフックを動かしません。話しかけて（2 字以上を打って Enter）20 秒たってもフックが一度も来ないと、欄の下に「⚠ #1 は話しかけてもフックが動きません」と出ます。
フックが動かないと、**AI に渡さない場所の見張りと、今の状態の受け渡しが効きません**（伝言の自動の受け渡しも、画面の確かめだけで行います）。
そのタブで `/exit` と打ってから `codex` と打ち、「Hooks need review」で 2 を選んでから、もう一度話しかけてください。
タブの見出しのツールチップにも「フック: 動いている／まだ来ていない」が出ます。

## AI が Excel を扱うしくみ

AI は Excel に直接触りません。AI が台本 `excel.ps1` を動かすと、台本がパイプで AiLink に頼み、AiLink が Excel の COM で読み書きします。
Excel への頼みは AiLink が1つずつ順に流します。xlsx のファイルを直接読み書きしないよう、AI に指示しています。

| 呼び方 | すること |
|---|---|
| `excel.ps1 -Book … -Sheet … -Range A1:H200` | 読む。結果をタブ区切りのファイルに書き、そのパスを返す（式のセルは値のあとに式）。-Range が無ければ使っている範囲全体。多ければ先頭だけ |
| `excel.ps1 -Overview -Book … -Sheet …` | シートの概要。書式のあらまし・結合・列の幅・図形・グラフ・テーブル・名前・条件付き書式・入力規則・コメントなどと、セルの値と式、それにシートの見た目の画像 |
| `excel.ps1 -Write -Book … -Sheet … -Range B3` | 書く。AI が先に作った `excel-write-tab<n>.tsv`（タブ区切り）の中身を、B3 を左上として手で打ったのと同じに入れる |
| `excel.ps1 -Script [-Timeout 60]` | AI が先に作った `excel-script-tab<n>.ps1`（Excel の COM を使う PowerShell）を、AiLink が確かめてから別の PowerShell で動かす。書式・行の挿入・並べ替え・グラフなど |

- **変える前に、AI が利用者に許可を求めます。** どのブックのどのシートのどこに何をするか（上書きで消える中身も）を示し、「はい」などの返事をもらってから書く。示す前の頼みは許可にしない（AI への指示）
- 書くときは `-Book` と `-Sheet` を必ず付けさせる（許可のあとに一覧で選び直しても、許可をもらったシートに書くため）
- **保存はしません。** 保存とブックを閉じるのは利用者がします
- **Excel の元に戻す（Ctrl+Z）は効かず、その前の履歴も消えます。** AI の変更を捨てたいときは、そのブックを保存せずに閉じれば、最後に保存したときに戻ります（そのあと手でした変更も消える）。AiLink に戻す仕組みはありません（2026-11-25。利用者の判断）
- 概要を読むと、シートの画像を撮るのにクリップボードを使うので、利用者がコピーしていたものは消えます（利用者の判断）
- 扱わないブック: AI に渡さない場所のブック・パスが https:// などの URL のブック（OneDrive・SharePoint の多く）・隠れたブック（LAN の共有フォルダ・NAS `\\server\…` のブックは扱う）

### 台本の確かめ

台本は利用者の権限で、Codex の囲い（サンドボックス）の外で動くので、AiLink が動かす前に確かめます。

1. 保存・閉じる・Excel を終える・開く・マクロを動かすなどの語（`save` を含む語・`Close`・`Quit`・`.Open`・`.Run`・`DisplayAlerts`・`Start-Process` など）があれば動かさない（コメントや文字の中でも）
2. AI に渡さない場所のフォルダのフルパスを書いた台本は動かさない
3. PowerShell の構文解析で、許した形だけを通す（`Tsuzuri/Ai/excel-guard.ps1`。Tsuzuri と同じファイルをリンクして使う）。変数・制御文・`$excel` からたどる決まった Excel のメンバー・一部のコマンドと型だけ。ファイル・ほかのプログラム・ネット・`&`・`Invoke-Expression`・名前の組み立て・関数の定め・`exit` などは断る

台本は時間を過ぎると止めます（既定 60 秒、`-Timeout` で 540 秒まで）。AiLink が終わると、動いている台本も一緒に終わります。

### それでも防げないこと

- **セルに書く式**: 外のブックやフォルダを指す参照・`WEBSERVICE` などネットに触れる関数・ブックの VBA のユーザー定義関数を呼ぶ式は、Excel の働きとして通ります（`-Write` でも台本でも）。AI には書かないよう指示しているだけです
- 台本の中で、AI に渡さない場所のブックに触れないかは確かめきれません（excel.ps1 の読む・書く・概要は断ります）。AI には触らないよう指示しています
- Excel の確かめの窓が出る操作（シートの削除など）は、台本では止まったまま時間切れになります。AI にはしないよう指示しています

## AI に渡さない場所

フォルダに **`.ailink-no-ai`** という名前のファイル（中身は空でよい）を置くと、その下は AI に渡しません（Tsuzuri の `.tsuzuri-no-ai` は見ません）。

- その下のブックは、一覧では選べず、今の状態にも名前を書かない。excel.ps1 でも読み書きしない
- AI がそのフォルダの下を読み書きしようとしたら、道具を使う前のフック（PreToolUse）で断る（コマンドに書いた場所を上へたどって目印を探す）
- 台本にそのフォルダのフルパスがあれば動かさない

**フックが動いていないと、道具の前の見張りは効きません**（上の「初めて使うとき」）。AiLink が落ちているなどでフックが AiLink につながらないときも、見張りは何もせずに通します（受け入れた）。
コマンドの中に場所を書かずにたどる道（`cd` を重ねる・`*` で広く読む）も防げません。

## タブどうしの伝言

AI が次を動かすと、相手のタブに「【#2 からの伝言】…」が渡ります。

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<%LOCALAPPDATA%\AiLink\ai>\talk.ps1" -To "#1" -Text "伝言"
```

- 相手の AI が待っていれば、AiLink が入力欄に置いて Enter を送る（Enter は要らない。2026-11-25。利用者の判断）。作業中なら、その作業が終わったときに次の頼みとして渡す
- **利用者が話しかけないまま自動で渡すのは、タブごとに 4 回まで**（AI どうしが話し続けないように）。超えたら、相手のタブの見出しに「●伝言」、欄の下に「伝言を渡す」のボタンを出す。押すと渡し、回数も数え直す
- 相手の AI が動いていないときも、受け箱に残してボタンで渡す
- 伝言は 1 行にまとめ、1000 字まで。**`$` は全角の `＄` に置き換えます**（Codex の入力欄で `$` がスキルの候補を開き、Enter で中身が別の語に置き換わったため。2026-11-26。利用者の判断）。受け取る AI には「＄ は $ のこと」と指示しています
- 受け箱は保存しません（AiLink を閉じると、渡していない伝言は消える）

## 設定ファイル

exe と同じフォルダの `ailink.json`。持ち運べるように `%AppData%` は使いません。自動で作ります。

| 鍵 | 中身 |
|---|---|
| `sidebarWidth` | 左の列（Excel の一覧）の幅 |
| `windowLeft`・`windowTop`・`windowWidth`・`windowHeight`・`windowMaximized` | 窓の位置と大きさ（最大化する前のもの） |
| `softwareRendering` | `true` にすると GPU を使わずに描く（仮想マシンで窓がちらつくとき。手で書く） |
| `agentExecutable` | codex.exe（か codex.cmd）の場所。空なら自動で探す。入れ場所が違う PC で手で書く（パスの `\` は `\\`。例 `"C:\\Tools\\codex.exe"`） |
| `aiTabs` | 開いていた会話のタブ（会話の番号 `session`・フォルダ `folder`・名前 `name`・題 `topic`） |

読めない `ailink.json` は、起動のときに尋ねて `ailink.json.bad` に退かし、初めの状態で起動します（「いいえ」なら起動しない）。
思わぬエラーは、隣の `crash.log` に書きます（1MB を超えたら作り直す）。

**`ailink.json` は人に渡したりリポジトリに入れたりしないでください**（`.gitignore` で除いてあります）。

## 使うフォルダ

| 場所 | 中身 |
|---|---|
| `%LOCALAPPDATA%\AiLink\ai` | AI が動かす台本（`excel.ps1`・`state.ps1`・`talk.ps1`・`hook.ps1`。exe の隣の `Ai\` から写す）と、AiLink が書く結果（読んだ表・概要と画像・シートの名前の一覧）。古いものは片付ける |
| `%LOCALAPPDATA%\AiLink\work` | 会話を始めるフォルダ。AI への指示 `AGENTS.md`、Codex のフック `.codex\hooks.json` とルール `.codex\rules\ailink.rules`、AI が作る作業のファイル（`excel-write-tab1.tsv` など） |
| `%LOCALAPPDATA%\AiLink\WebView2` | 端末の表示（WebView2）のデータ |
| `%LOCALAPPDATA%\AiLink\excel-notes.txt` | Excel の一覧の一番下の「Excel で AI にいつも伝えること」で書く前提（下） |

会話の記録そのものは Codex が `%USERPROFILE%\.codex\sessions`（`CODEX_HOME` があればその下）に残します。AiLink は利用者の `~/.codex` には書きません。

## 会社の PC で使うとき（PowerShell の決まり）

AiLink の台本は、どれも `powershell -ExecutionPolicy Bypass -File …` で動かします。ふつうの実行ポリシー（`Restricted`・`RemoteSigned` など）なら、これで動きます。
ただし次のときは動きません。

- **グループポリシーで実行ポリシーを決めている**（`MachinePolicy`・`UserPolicy`）: `-ExecutionPolicy Bypass` より強く、台本が止められる
- **AppLocker・WDAC などで PowerShell が制限言語モード（ConstrainedLanguage）になる**: 台本が使う .NET の部品（パイプなど）を作れず、AiLink につながらない

確かめ方（PowerShell で）:

```powershell
Get-ExecutionPolicy -List                     # MachinePolicy・UserPolicy が Undefined でなければ、グループポリシーで決めている
$ExecutionContext.SessionState.LanguageMode   # FullLanguage でなければ、制限言語モード
```

どちらかに当たれば、情報システムの担当に相談してください。

## ビルド

1. Visual Studio で `AiLink\AiLink.sln` を開く（Tsuzuri とは別のソリューション。2026-11-26。利用者の判断。Tsuzuri と更新の時期がずれるため）
2. NuGet パッケージを復元する（PackageReference 方式）
3. ビルドする（`AiLink\bin\Release\AiLink.exe` ができる。台本 `Ai\`・端末の `Terminal\`・`ConPty\` も隣に写る）

台本の書き方の確かめ `Tsuzuri\Ai\excel-guard.ps1` は、写しを作らずリンクして使います（出力では `Ai\excel-guard.ps1`）。
`ConPty\` の `conpty.dll`・`OpenConsole.exe` は `Tsuzuri\ConPty\` と同じもの（NuGet の Microsoft.Windows.Console.ConPTY 1.24.261001001）です。

Mac では、XAML を除いた C# のコンパイルだけを `bash tools/check-build-ailink.sh` で確かめられます（.NET SDK が要ります）。`AiLink.csproj` は古い形で、コンパイルするファイルを1つずつ並べているので、並べ忘れもここで見つけます。XAML は Windows のビルドでしか確かめられません。

## 試験

どれも Windows（Windows PowerShell 5.1）で、リポジトリの一番上から回します。

| 試験 | 確かめること |
|---|---|
| `powershell -NoProfile -ExecutionPolicy Bypass -File tools\excel-client-test.ps1` | `excel.ps1`（AiLink と Tsuzuri）を偽のパイプを相手に動かし、呼び方の誤りでは何も送らずファイルを残すか、作業のファイルを一度だけ送って消すか、壊れた答えを失敗にするか。`excel.ps1` を変えたら回す（3 分ほど） |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tools\excel-guard-test.ps1` | 台本の書き方の確かめ（`excel-guard.ps1`）が、ふつうの台本を通し、ファイル・ほかのプログラム・ネット・保存や閉じる・マクロに触る台本を断るか。`excel-guard.ps1` を変えたら回す |

AI に渡す文（`MainWindow.AiInstructions.cs` の指示と今の状態）を変えたら、別のエージェントに、指示と今の状態・コードの動きとの食い違いが無いかを見てもらってからコミットします（リポジトリの `CLAUDE.md`）。

## 構成

```
AiLink/
├ App.xaml(.cs)                起動の入口（2つ目の起動を止める・設定を読む・漏れた例外の記録）
├ AppSettings.cs               ailink.json
├ MainWindow.xaml(.cs)         窓（左に Excel の一覧、右に会話の欄、下に知らせ）
├ MainWindow.Ai.cs             会話のタブ・Codex の起動・パイプ・片付け
├ MainWindow.AiHooks.cs        Codex のフック（hooks.json・道具の前の確かめ）
├ MainWindow.AiInstructions.cs AI への指示（AGENTS.md）と今の状態
├ MainWindow.AiTalk.cs         タブどうしの伝言
├ MainWindow.AiSetup.cs        Codex が入っていないときの案内
├ MainWindow.Excel.cs          Excel の一覧・読む・書く・概要・台本
├ Agents/                      AI のつなぎ口（IAgent・CodexAgent）と、AI に渡さない場所（AiGuard）
├ Office/                      Excel の COM（AiLink の中で COM を使うのはここだけ）
├ Views/                       端末（WebView2 と ConPTY）・Excel の一覧・アイコン・プロセスの親子
├ Ai/                          AI が動かす台本（excel.ps1・state.ps1・talk.ps1・hook.ps1）
├ Terminal/                    端末の画面（xterm.js）
└ ConPty/                      端末で使う ConPTY（conpty.dll・OpenConsole.exe）
```

## 使っているもの

| 名前 | ライセンス | 用途 |
|---|---|---|
| [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)（Microsoft.Web.WebView2 1.0.4258.31） | BSD-3-Clause（SDK） | 会話のタブの端末の表示 |
| [xterm.js](https://github.com/xtermjs/xterm.js)（と addon-fit・addon-unicode11・addon-webgl） | MIT | 会話のタブの端末（`AiLink/Terminal/LICENSE-xterm.txt`） |
| [ConPTY](https://github.com/microsoft/terminal)（conpty.dll・OpenConsole.exe） | MIT | 会話のタブの端末（`AiLink/ConPty/LICENSE.txt`） |

Codex CLI・Excel・MinGit は同梱しません（利用者の PC に入っているものを使う）。

## ライセンス

リポジトリの [MIT License](../LICENSE)。同梱しているもの（上の表）は、それぞれのライセンスに従います。

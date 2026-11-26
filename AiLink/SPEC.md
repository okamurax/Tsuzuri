# AI連携ツール（AiLink）仕様書

最終更新: 2026-11-26（切り出しの手順書 PORTING.md をこの文書と README.md に移して消した）

利用者と開発者が最初に知るべきことは [README.md](README.md) に書く。ここには細かい仕様と、決めたことを書く。

## 1. 何をするものか

開いている Excel を、AI（Codex CLI）に読ませ・書かせるための Windows 用のツール。
Tsuzuri の AI の欄（会話のタブ・今の状態の渡し方）と Excel の読み書きを切り出した単独のソフト（2026-11-25）。Tsuzuri は変えない。

- 画面: 上端の段（名前と「終了」）、左の列に開いている Excel の一覧、仕切り、右に会話の欄、下に知らせの行
- AI は Codex CLI に固定する。ただし AI ごとの違いはつなぎ口（`Agents/IAgent.cs`）の向こうに置き、ほかの AI に替えられるようにする

### やらないこと（決めたこと）

- 共通ターミナルと SSH は持たない（2026-11-25。利用者の判断。Excel の操作に絞る）
- ランチャーとファイラーは持たない（2026-11-25。利用者の判断。いつも起動している Excel だけを相手にする）
- プロジェクトフォルダは持たない（2026-11-25。利用者の判断。扱うのは開いている Excel で、ファイルが要るときは端末へ落としてもらう。落としたファイルのパスは入力欄に置く）
- Excel の変更を戻す仕組みは持たない（2026-11-25。利用者の判断。AI は保存しないので、利用者は保存せずに閉じれば元に戻せる）
- 保存しない・ブックを閉じない・ブックやファイルを開かない・マクロを動かさない（保存と閉じるのは利用者がする）
- 利用者の `~/.codex` には書かない（フック・ルールは会話を始めるフォルダの `.codex` に置く）

## 2. 技術構成

| 項目 | 決定 |
|---|---|
| ランタイム | .NET Framework 4.8 |
| 言語 | C# 7.3 |
| UI | WPF。プロジェクトは古い形の csproj で、コンパイルするファイルを1つずつ並べる |
| ソリューション | `AiLink/AiLink.sln`（Tsuzuri とは別。2026-11-26。利用者の判断。Tsuzuri と更新の時期がずれるので、同じ Visual Studio では不便。前は `Tsuzuri.sln` に入れていた） |
| Tsuzuri との関わり | Tsuzuri のプロジェクトは参照しない。Excel を扱う部分（`Tsuzuri/Office/ExcelBridge.cs`・`ExcelBridge.Overview.cs`・`ExcelInstructions.cs`、`Tsuzuri/Views/ExcelListView.cs`・`FileIcon.cs`、`Tsuzuri/Ai/excel.ps1`・`excel-guard.ps1`）はファイルをリンクして同じものを使う（写しを作らない。2026-11-26。前は写しで、直すたびに両方へ写していた）。名前空間は `Tsuzuri.Office`・`Tsuzuri.Views` のまま |
| 外部ライブラリ | WebView2（NuGet の Microsoft.Web.WebView2 1.0.4258.31。版は Tsuzuri とそろえる）、xterm.js、ConPTY（conpty.dll・OpenConsole.exe） |
| 32 ビット | 優先しない（`Prefer32Bit` false。32 ビットだと ConPTY が裏で使う conhost.exe が見つからない） |
| 名前 | 画面に出す名前は「AI連携ツール」（`App.DisplayName`） |

## 3. ファイルの構成

| ファイル | 受け持ち |
|---|---|
| `App.xaml(.cs)` | 起動の入口。2つ目の起動を止める・設定を読む・漏れた例外の記録。色とスタイル |
| `AppSettings.cs` | `ailink.json` |
| `MainWindow.xaml(.cs)` | 窓。位置と大きさ・仕切り・知らせの行 |
| `MainWindow.Ai.cs` | 会話のタブ・Codex の起動のコマンド・会話を始めるフォルダの用意・パイプ・片付け |
| `MainWindow.AiHooks.cs` | Codex のフック（`hooks.json` の中身・フックへの答え・道具の前の確かめ） |
| `MainWindow.AiInstructions.cs` | AI への指示（`AGENTS.md`）と今の状態 |
| `MainWindow.AiTalk.cs` | タブどうしの伝言 |
| `MainWindow.AiSetup.cs` | Codex が入っていないときの案内 |
| `MainWindow.Excel.cs` | Excel の一覧と選び方・今の状態の Excel の段・読む・書く・概要・台本 |
| `Agents/IAgent.cs`・`CodexAgent.cs` | AI のつなぎ口と Codex の分（探し方・入れ方・画面の印・会話の番号） |
| `Agents/AiGuard.cs` | AI に渡さない場所（目印 `.ailink-no-ai`） |
| `Office/ExcelBridge.cs`・`ExcelBridge.Overview.cs` | Excel の COM（AiLink の中で COM を使うのはここだけ）。`Tsuzuri/Office/` へのリンク |
| `Office/ExcelInstructions.cs` | AI への指示の Excel の段（Tsuzuri と同じ型の文から作る。`⟦T:…⟧` は Tsuzuri だけ、`⟦A:…⟧` は AiLink だけ）。`Tsuzuri/Office/` へのリンク |
| `Views/TerminalView.cs`・`PseudoConsole.cs` | 端末（WebView2 の xterm.js と ConPTY）。アプリのジョブ |
| `Views/ExcelListView.cs` | 左の列の Excel の一覧。`Tsuzuri/Views/` へのリンク |
| `MainWindow.Outlook.cs` | Outlook のつなぎ（AiLink に置く所だけ） |
| `Office/OutlookBridge.cs`・`OutlookLink.cs`・`OutlookInstructions.cs`、`Views/OutlookListView.cs` | Outlook の COM・選んだフォルダと頼みに答える所・指示の Outlook の段・一覧。`Tsuzuri/` へのリンク |
| `Views/ProcessTree.cs` | プロセスの親子（タブの PowerShell の下で AI が動いているか） |
| `Views/FileIcon.cs`・`ShellIcons.cs`・`ScrollThumbLength.cs` | アイコン・スクロールバー（`FileIcon.cs` は `Tsuzuri/Views/` へのリンク） |
| `GitLocator.cs` | exe の隣の git（MinGit）の場所 |
| `Ai/excel.ps1`・`outlook.ps1`・`state.ps1`・`talk.ps1`・`hook.ps1` | AI（とフック）が動かす台本（`excel.ps1`・`outlook.ps1` は `Tsuzuri/Ai/` へのリンク。`AILINK_PIPE` があれば AiLink として動く）。ASCII だけで書く（PowerShell 5.1 は BOM の無い台本を ANSI の文字コードで読む） |
| `Ai/excel-guard.ps1` | `Tsuzuri/Ai/excel-guard.ps1` へのリンク（台本の書き方の確かめ） |
| `Terminal/` | 端末の画面（terminal.html・xterm.js と addon） |
| `ConPty/` | 新しい ConPTY（`x64\`・`arm64\` の conpty.dll と、その下の Windows の種類ごとの OpenConsole.exe） |

## 4. 起動と終了

- 2つ目の起動は止める（Mutex `Local\AiLink.SingleInstance`。「すでに起動しています」と出して終わる）
- `ailink.json` の中身を読めなければ、「はい」で `ailink.json.bad-<日時>` に退かして初めの状態で起動し、「いいえ」で起動しない（2026-11-25 のレビュー。手で直して `\` を書き損じたときに、起動できなくならないように。前に退かしたファイルは消さない）。退かせなければ起動しない。開けない（IOException・権限）ときは、正しい中身を退かさないよう、尋ねずに起動しない（2026-11-26）
- `softwareRendering` が真なら、窓を作る前に GPU を使わない描き方にする
- 漏れた例外: 画面のスレッドのものは `crash.log`（`ailink.json` の隣。1MB を超えたら作り直す）に書き、知らせて続ける。30 秒の間に 4 回目が起きたら落とす。裏のスレッド・待たれなかったタスクのものは記録だけ
- 窓が出たら（Loaded）: 会話のタブを開き（6章）、Excel の一覧を読む
- 窓に戻ったら（Activated）: Excel の一覧を読み直し、Codex の案内を出していれば探し直す
- 閉じるとき: 窓の位置と大きさ・左の列の幅を書き、`StopAi`（パイプを閉じ、各タブの端末を片付け、この起動が書いたシートの名前の一覧のファイルを消す）。閉じている最中は、会話が終わっても開いていたタブの覚えを消さない

## 5. 設定ファイル（`ailink.json`）

exe と同じフォルダ。持ち運べるように `%AppData%` は使わない（Tsuzuri の `app.json` と同じ考え）。DataContract の JSON。
書くときは一時ファイル（`ailink.json.tmp`）に書いてから置き換える。書けなくても作業は止めない。

| 鍵 | 中身 | 読み込みで直すこと |
|---|---|---|
| `sidebarWidth` | 左の列の幅 | 180〜3000 の外（NaN も）なら 300 |
| `windowLeft`・`windowTop`・`windowWidth`・`windowHeight` | 窓の位置と大きさ（最大化する前） | 幅 640〜20000・高さ 400〜20000 の外か位置が NaN・無限なら、幅と高さを 0（未保存。既定の場所に出す）。上端が画面に 100px 以上かかっていなければ位置は使わない |
| `windowMaximized` | 最大化していたか | |
| `softwareRendering` | GPU を使わずに描く（手で書く） | |
| `agentExecutable` | AI の実行ファイルの場所（任意。手で書く）。環境変数を展開し、前後の `"` を外す | |
| `aiTabs` | 開いていた会話のタブ: `session`（会話の番号）・`folder`・`name`（`#1` など）・`topic` | `session` が Guid の形でないものは捨てる（起動のコマンドに書くため） |

## 6. 会話のタブ

- タブごとに別の PowerShell が動き、その中で Codex を起動する（7章）。裏のタブも動き続ける
- タブの番号（内部）は `t` ＋ Guid（`AILINK_TAB`。頭の `t` は、`-c` で渡すとき数として読まれないように）
- タブの名前は `#1`・`#2`…（使っていないいちばん小さい番号）。伝言の宛先になる。`#` を付けるのは、話の中の数と取り違えないため
- 見出しは「名前  題」。受け箱に伝言があれば「  ●伝言」を足す。ツールチップに名前・題・フックが動いているか・端末のタイトル
- 窓が出たとき: Codex が見つからなければ案内を出す（19章）。見つかれば、`aiTabs` のうち Codex がその会話を残しているもの（`SessionExists`）を開き直す。1つも無ければ新しいタブを1つ開く
- 戻すタブの名前は、`#` で始まり、まだ使っていなければそのまま使う
- 最後のタブを閉じたら、新しいタブを1つ開く（欄を空にしない）
- タブを閉じると、中の PowerShell も終わる。自分で閉じた会話は、次の起動で戻さない
- `aiTabs` に書くのは、会話の番号が分かったタブだけ（フックで番号が変わったとき・タブを閉じたとき・会話の終わりのフックで書き直す）。会話の終わり（SessionEnd）のフックが来たタブは、番号を消す（次の起動で戻さない）
- タブを選ぶと、端末にキーボードを渡す。いったん見えない文字の欄に移してから渡す（端末にじかに移すと、日本語の変換が窓の左上に出ることがあった。Tsuzuri で見つけたこと）
- 欄の下の行に、選んでいるタブに最後に今の状態を渡したときの一言（11章）か、フックの警告を出す
- **フックの見張り**: 話しかけて（前の Enter から 2 字以上を打って Enter。「Hooks need review」の「2」と Enter は数えない）20 秒たってもフックが一度も来ないタブには、欄の下に「⚠ #n は話しかけてもフックが動きません。…このタブで /exit と打ってから codex と打ち、「Hooks need review」で 2 を選んでから、もう一度話しかけてください」と出す（5 秒ごとに見る。1 タブ 1 回）。起動からは数えない（2026-11-26。Codex は最初に話しかけるまでフックを動かさないので、起動から数えると、動いているのに出た）

### 端末

- `Views/TerminalView`: WebView2 の中の xterm.js（`Terminal/terminal.html`）に描き、中のプログラムとは ConPTY（`Views/PseudoConsole`）でつなぐ
- 仮の宿の名前は `ailink.local`（exe の隣の `Terminal` フォルダを `https://ailink.local/` で見せる）。ほかのページへは移らない。リンクは http・https だけ、いつものブラウザで開く
- WebView2 のデータは `%LOCALAPPDATA%\AiLink\WebView2`
- ConPTY は、exe の隣の `ConPty\<このプロセスの種類>\conpty.dll` があり、OpenConsole.exe も見つかれば同梱の新しい版を使う。無ければ Windows 標準の conhost
- 中のプログラムが終わったら、Enter で PowerShell を起動し直す（Codex は最初の1回だけ起動するので、ただの PowerShell になる。`codex` と打てば同じ Codex が動く）
- ファイル・フォルダを落とすと、そのパスを打ったのと同じに入力欄に置く（Enter は付けない。空白を含むパスは `"` で囲む）
- AiLink が起動したプロセス（タブの PowerShell・台本）は、アプリのジョブ（`KILL_ON_JOB_CLOSE`）に入れ、AiLink が終わると一緒に終わる

## 7. Codex の起動

`AiCommandLine` が、タブの端末で動かすコマンドを作る。

```
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoExit -EncodedCommand <下の文を UTF-16 で Base64>
```

```powershell
$env:AILINK_TAB='t…';$env:AILINK_PIPE='AiLink-<プロセス番号>';$env:AILINK_WORK='<%LOCALAPPDATA%>\AiLink\work';$env:AILINK_NAME='tab1'
;$env:PATH='<codex のフォルダ>;'+$env:PATH
;function codex { & '<codex.exe>' -c 'shell_environment_policy.set.AILINK_TAB=t…' -c 'shell_environment_policy.set.AILINK_PIPE=AiLink-…'
                  -c 'shell_environment_policy.set.AILINK_WORK=…' -c 'shell_environment_policy.set.AILINK_NAME=tab1' -c 'project_doc_max_bytes=262144' @args }
;codex                       # 前の会話を戻すときは codex resume <会話の番号>
```

| 環境変数 | 中身 |
|---|---|
| `AILINK_TAB` | タブの番号（内部）。台本・フックがどのタブからかを伝える |
| `AILINK_PIPE` | パイプの名前 `AiLink-<AiLink のプロセス番号>` |
| `AILINK_WORK` | 会話を始めるフォルダ（AI が作業のファイルを作る所） |
| `AILINK_NAME` | タブの名前をファイルの名前に使う形（`#1` → `tab1`。数字だけを使う。`ailink.json` から戻した名前に `\` や `..` が入っていても、作業のフォルダの外を指さないように）。作業のファイルをタブごとに分ける（2026-11-25 のレビュー。タブが2つ以上あると、同じ名前のファイルを取り合い、別の会話の中身を書いてしまうため） |

- **`-c shell_environment_policy.set.…`**: Codex はタブの PowerShell の環境変数を、AI が動かすコマンドに引き継がなかった（2026-11-25 に Windows で試して分かった。`state.ps1` が「AILINK_PIPE is not set」になった）。Codex の設定で、AI のコマンドにも同じ 4 つを渡す。値は Codex が TOML として読めなければ文字のまま使う
- **`-c project_doc_max_bytes=262144`**（2026-11-26）: Codex は AGENTS.md を初めの設定では 32KiB までしか読まず、超えた分を黙って捨てる（OpenAI の説明）。AiLink の指示だけで約 39KB あり、終わりの段（伝言・ファイル・Excel で AI にいつも伝えること）が渡っていなかったので、256KiB に上げる
- PowerShell の関数 `codex` を置くので、Codex が終わったあとに `codex` と打っても同じ設定で動く。PATH に Codex のフォルダを足す（AiLink を起動したあとに入れた Codex でも動くように）
- Codex を起動するのは、タブを開いた最初の1回だけ
- exe の隣に `git\cmd`（MinGit）があれば、AiLink のプロセスの PATH の頭に足す（タブの PowerShell に引き継がれる）
- 会話を始めるフォルダを用意できなければ（8章）、知らせを出して PowerShell だけを起動する

## 8. 会話を始めるフォルダ（`%LOCALAPPDATA%\AiLink\work`）

扱うのは開いている Excel なので、利用者のフォルダ全体を作業の場所として渡さない（Codex は作業のフォルダの中を自由に読み、そこに書ける）。
タブを開くたびに `PrepareAiFolder` が次を用意する。中身が同じなら書かない。書くときは一時ファイル（`….<プロセス番号>.saving`）から置き換える（ほかのタブが起動する瞬間に、途中のファイルを読ませない）。

| ファイル | 中身 |
|---|---|
| `%LOCALAPPDATA%\AiLink\ai\*.ps1` | exe の隣の `Ai\` から `excel.ps1`・`state.ps1`・`talk.ps1`・`hook.ps1` を写す |
| `work\AGENTS.md` | AI への指示（11章）。Codex は始めたフォルダの `AGENTS.md` を自分で読む |
| `work\.codex\hooks.json` | フック（9章）。中身が変わると、Codex が利用者に信頼するかを尋ね直す（信頼するまで動かない） |
| `work\.codex\rules\ailink.rules` | Codex のルール（下） |

**ルール**（2026-11-25。`excel.ps1` のたびに「囲いの外で動かしてよいか」と聞かれた）: 指示に書いた形で AiLink の台本を動かすときだけ、確かめずに動かす。

```
prefix_rule(
    pattern = [["powershell", "powershell.exe"], "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ["<ai>\\state.ps1", "<ai>\\excel.ps1", "<ai>\\talk.ps1"]],
    decision = "allow",
    justification = "AiLink のパイプに頼むだけの台本（Excel に触るのは AiLink）",
)
```

許したコマンドは囲い（サンドボックス）の外で動く。台本はパイプで AiLink に頼むだけで、Excel に触るのは AiLink。
ただし `excel.ps1 -Script` は AI が書いた PowerShell を利用者の権限で動かすので、AiLink が動かす前に確かめる（16章）。
台本の後ろにほかのコマンドをつなげられないよう、道具の前のフックで断る（9章の `ChainsCommands`）。

**`.codex` の片付け**: `.codex` の下には AiLink が置いた `hooks.json` と `rules\ailink.rules` だけを残し、ほかの `*.toml`・`*.rules`・`*.json` は消す
（2026-11-25 のレビュー。AI が「何でも許す」ルールや囲いの設定を置いて、次の起動から確かめなしに動けないように。Codex は作業のフォルダの `.codex` を AI から書けない場所にしているはずだが、念のため。ほかの拡張子のものは残す）。

## 9. フック

`work\.codex\hooks.json`。どれも `hook.ps1` で AiLink のパイプへ中継する（2026-11-25。試しのフックで Windows での動きを確かめてから作った）。

```
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<ai>\hook.ps1" <イベント>
```

exe はパスを書かず `powershell` にする（Codex はフックのコマンドを PowerShell で動かすので、`"…\powershell.exe" -NoProfile` は文法の誤りになった）。

| イベント | matcher | 待ち（秒） | AiLink がすること | 答え |
|---|---|---|---|---|
| SessionStart | | 30 | 会話の番号をタブに覚える。作業中の印を外し、待っている印にする | 今の状態（additionalContext） |
| UserPromptSubmit | | 30 | 作業中の印を付ける。頼みに「【#」が無ければ（利用者が打った頼み）、自動で渡した伝言の回数を 0 に戻す | 今の状態（additionalContext） |
| PreToolUse | `*` | 15 | AI に渡さない場所と、台本へのつなぎを確かめる（画面のスレッドは使わない）。作業中の印を立て直す | 断るときだけ `permissionDecision: deny` |
| Stop | | 30 | 受け箱に伝言があり、自動で渡した回数が 4 未満なら、先頭を渡す。無ければ待っている印にする | 伝言を渡すときだけ `{"decision":"block","reason":"<伝言>"}`（Codex は reason を次の頼みとして続ける） |
| SessionEnd | | 3 | 会話の番号がタブのものなら消す（閉じている最中は消さない）。動いていない印にする | なし |

確かめたこと（2026-11-25。Windows）: フックは囲いの外で利用者と同じユーザーで動き、タブの PowerShell の環境変数（`AILINK_TAB`・`AILINK_PIPE`）が見える。
標準入力の JSON に `session_id`・`turn_id`・`cwd`・`prompt`・`tool_name`（シェルは `"Bash"`）・`tool_input.command`・`last_assistant_message`・`source` がある。
SessionStart・UserPromptSubmit の `hookSpecificOutput.additionalContext` は AI に届く（先頭が「[」の文を返すと JSON として読まれて失敗した。今の状態の頭の「[AI連携ツールの今の状態 …]」は additionalContext の中なので通る）。

- どのフックが来ても、そのタブはフックが動いている（`HooksSeen`）とみなす（会話の始まりのフックは、フックを信頼する前に過ぎていることがある。2026-11-25 に Windows で、フックを信頼し直したタブが「AI が動いていない」になった）。SessionEnd 以外なら、会話を始めていて、`session_id` が Guid の形で今と違えば番号とフォルダ（`cwd`）を覚え直す
- 画面のスレッドで答える（PreToolUse 以外）。10 秒で間に合わなければ何も返さない。UserPromptSubmit が間に合わなかったときも、作業中の印だけは付ける（付けないと、作業中のタブに伝言を打ってしまう）
- **AiLink に届かないとき**（`hook.ps1` がつながらない・`AILINK_TAB` が無い・答えが無い）は、何も出さずに exit 0 で終わる。このとき道具の前の確かめも通してしまう（受け入れた）。AiLink が答える途中で例外になったときの PreToolUse は断る（「確かめられなかったので断った」）
- AiLink の外で動かした codex（`AILINK_TAB` が無い）では、中継は何もせずに終わる

### 道具の前の確かめ（PreToolUse）

1. `tool_input` の文字の項目を集める（`tool_input` が文字なら `command` とみなす）
2. **台本へのつなぎ**（`ChainsCommands`。2026-11-25 のレビュー）: `command` に AI のフォルダ（`%LOCALAPPDATA%\AiLink\ai`）のパスがあれば、ルールで確かめなしに動く AiLink の台本の後ろに、別のコマンドを紛れ込ませていないかを見る。次のどれかなら断る
   - 全角の引用符（‘ ’ ‚ ‛ “ ” „）がどこかにある（PowerShell は ' や " と同じに扱うので、引用符の数え違いですり抜けられる）
   - 引用符の外に、英数字・空白・タブ・`- : \ / . _ # , =`・ASCII でない文字のほかがある（`; | & < > ( ) { } @ $` 改行など）
   - 二重引用符の中に `$` か `` ` `` がある（式・読み替えになる）。単引用符の中は文字のまま読まれるので何でもよい
   - 引用符が閉じていない
3. **AI に渡さない場所**（`AiGuard.Check`。17章）

断るときは `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"<わけ>"}}` を返し、知らせの行にも出す。

## 10. パイプ

- 名前は `AiLink-<AiLink のプロセス番号>`（窓は1つだけなので、プロセス番号で分ける）。最初のタブを開くときに待ち始める
- 本人だけがつなげる（今のユーザーに許し、ネットワーク（NetworkSid）は断る）
- 待つのは別のスレッド。つながったら、そのつながりは別のスレッドに任せて、すぐ次の口を開く（ある頼みに応えている間も、ほかのタブの頼みを落とさない）
- 頼みも答えも UTF-8 の1行（区切りはタブ）。答えの中の改行は空白にする

| 頼み | 形 | 答え |
|---|---|---|
| フック | `hook→<イベント>→<タブの番号>→<Base64 の JSON>` | `ok→<Base64 の標準出力>`（出すものが無ければ `ok→`） |
| 今の状態 | `state→<タブの番号>` | `ok→<Base64 の今の状態>` か `error→わけ` |
| 伝言 | `talk→<送るタブの番号>→<Base64 の宛先>→<Base64 の伝言>` | `ok→一言` か `error→わけ` |
| 読む | `excel-read→<B64 ブック>→<B64 シート>→<B64 範囲>` | `ok→<ファイルのパス>（説明）` か `error→わけ` |
| 概要 | `excel-overview→<B64 ブック>→<B64 シート>` | `ok→<ファイルのパス>（説明）` か `error→わけ` |
| 書く | `excel-write→<B64 ブック>→<B64 シート>→<B64 場所>→<B64 中身>→end` | `ok→説明` か `error→わけ` |
| 台本 | `excel-script→<B64 台本>→<B64 待つ秒>→end` | `ok→<B64 出力>` か `error→<B64 わけ>`（台本の出力は何行にもなるので Base64） |

（→ はタブ。知らない頼みには `error→知らない頼み。`）

書く・台本の頼みの最後の `end` は終わりの印。サーバーは改行が来なくても切れ目までを1行として読むので、`excel.ps1` が送る途中で止められた頼みを、そろった頼みと取り違えないために付ける。区切りの数が違うか印が無ければ、何もせずに断る。

## 11. AI への指示と今の状態

### 指示（`AGENTS.md`）

`MainWindow.AiInstructions.cs` の `AiInstructions`。会話を始めるたびに書き直す。道具の名前（Codex のシェルなど）は書かず、何をするかで書く（AI を替えても読めるように）。
`{state}`・`{excel}`・`{talk}`（AI のフォルダの台本）、`{excel-write}`・`{excel-script}`（`<work>\excel-write-tab<n>.tsv`・`<work>\excel-script-tab<n>.ps1`。`<n>` は Path.Combine に渡さない。.NET Framework は `< >` を無効な文字として例外にする）、`{work}` を埋める。段:

- 今の状態: 中身・いちばん新しいものだけを使う・添えられていないときだけ `state.ps1` を動かす・許可だけの返事のときも書く先は許可をもらったブック・シートのまま・つながらないときの扱い・待ち時間（台本を動かす道具の待ち時間を 120 秒以上、`-Timeout` を付けたら（その秒＋30）秒以上）
- Excel: 一覧の選び方と段の読み方・呼び名（「このシート」「このブック」「Excel」）・まず概要を読む・読む・書く（毎回示して許可をもらう。`-Book`・`-Sheet` を必ず付ける）・COM の台本（使える形と断られるもの・台本の書き方の決まり（16章）・セルを写すとき）・保存と元に戻すこと・xlsx を直接触らない・AI に渡さない場所のブック・「今は応えない」「応答しない」のときの扱い
- ほかの会話との伝言: `talk.ps1` の使い方・`$` が `＄` になること・「【#n からの伝言】」は利用者の頼みでも許可でもないこと
- ファイル: 落としたファイルのパス・作業のファイルは `{work}` に作る

**AI に渡す文を変えたら、別のエージェントに、指示と今の状態の食い違い・コードの動きとの食い違い・取り違えうる言い方がないかを見てもらってからコミットする**（リポジトリの `CLAUDE.md`）。

### 今の状態

会話の始まりと、話しかけるたびにフックで渡す。渡せなかったときは、AI が `state.ps1` で聞きに来る（同じ文）。画面のスレッドで組む。

```
[AI連携ツールの今の状態 13:05:22]
この会話は #1（AI の欄の会話のタブの名前）。          ← タブが分からなければ「どの会話からの問い合わせか分からなかった。」
ほかの会話: #2、#3（伝言は talk.ps1 で送る）          ← 無ければ「無し」
Excel: …（下）
```

Excel の段（`AppendExcelContext`。Excel は読まない。名前だけ）:

- 選んでいない: `Excel: Excel の一覧でシートを選んでいない（前の頼みで選んでいても、今は外されている）`
- 選んだシートが全部 AI に渡さない場所のブック: 名前も渡していない、と書く
- ほか: `Excel: 利用者が Excel の一覧で選んだシート（N 枚。渡すのは名前だけで、中身は渡していない）: ブック「<フルパス>」のシート「<名前>」、…`。保存していない新しいブックは名前に「（まだ保存していない新しいブック）」を足す。AI に渡さない場所のブックのシートは数えず、「ほかに選んだ n 枚は…名前も渡していない」と添える
- 2枚以上のシートがあるブックで全部を選んでいるか、1つのブックで 30 枚を超えて選んでいれば、名前を並べず `ブック「…」の全シート（N 枚。シートの名前はここに並べず、ファイル「…」に書いた）` とする（2026-11-25。利用者の考え。シートがとても多いブックで、話しかけるたびに名前を全部並べないため）。ファイルは `<ai>\sheets-<プロセス番号>-<中身の SHA1 の頭 12 桁>.txt` で、ブックごとに `# ブック: <ブック>（全シート N 枚）` か `（選んだシート N 枚）` の行と、その下にシートの名前を1行に1つ。中身ごとに別の名前にし、上書きしない（前の頼みの今の状態が指すファイルを、あとの頼みで書き換えないため）。書けなければ名前を並べる

欄の下の行には「今の状態を渡した  ・Excel 見積.xlsx › 明細  13:05:22」（2枚以上なら「・Excel N シート」、AI に渡さないブックなら「・Excel（AI に渡さないブック）」）を出す。

## 12. タブどうしの伝言

Codex には会話どうしのメッセージが無いので、AiLink が仲立ちする（2026-11-25。利用者の判断）。Enter は要らない。

### 受け付け（`talk.ps1` → `PlaceAiTalk`）

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<ai>\talk.ps1" -To "#1" -Text "<伝言>"
powershell -NoProfile -ExecutionPolicy Bypass -File "<ai>\talk.ps1" -To "#1" -TextFile "<ファイル>"
```

- 送ったタブが分からなければ断る（AiLink のタブの中で動かしていない）。宛先は `#` が無ければ足す（「1」も `#1`）。無い宛先には、今あるほかの会話の名前を添えて断る。自分には送れない
- 伝言を1行にする（改行・タブ・U+2028・U+2029 は空白、ほかの制御の文字は落とす）。前後の空白を除いて空なら断る。**1000 字を超えたら断る**（長いと Codex が貼り付けとして畳み、利用者が中身を見ずに送ってしまうため）
- 本文の「【#」は「［#」にする（差出人の見出しの形を書いて、ほかの会話になりすませないように）
- **`$` は全角の `＄` にする**（2026-11-26。利用者の判断。Codex の入力欄は `$` のあとに字を打つとスキルの候補を開き、あとで送る Enter がその候補を選んで、伝言の中身が別の語に置き換わった。`$a` が `$openai-templates:…` になった）。受け取る AI には指示で「＄ は $ のこと」と伝える
- 入力欄が空の印と同じ文字（`Ask Codex to do anything`）は `(…)` にする（画面の確かめを釣らないように）
- 「【#2 からの伝言】<本文>」として宛先の受け箱に入れ、すぐ渡せるなら渡す
- 答え: 相手の AI が起動していて端末が動き、（会話を始めている か フックがまだ来ていない）で、自動で渡した回数が上限より少なければ「…に預けた（手が空いていれば、すぐに渡る。作業中なら終わったときに渡る）」。ほかは「…の受け箱に預けた（… 利用者が …のタブで「伝言を渡す」を押すと渡る）」

### 渡す

- **相手の AI が作業中**（確かめを出している間も）: 入力欄には打たない（文字が確かめの答えのキーとして効くため。2026-11-25 のレビュー）。作業の終わりのフック（Stop）で、伝言を次の頼みとして返す（9章）。このときは話しかけたフックが来ないので、今の状態は添わらない（指示で `state.ps1` を動かさせる）
- **相手の AI が待っている**: 見回り（2 秒ごと。受け箱が空になったら止まる）が、次が全部そろったときに渡す
  - 受け箱に伝言があり、画面の確かめの最中でない
  - 作業中でない（作業中の印が付いて 3 分たったものは、作業の終わりのフックが来なかったとみなし、画面の確かめに任せる）
  - 前に打った伝言の受け付けを待っていない。打ってから 5 秒たった
  - 端末が動いていて、このタブで Codex を起動した
  - 自動で渡した回数が上限（4）より少ない
  - 作業を終えて（会話を始めて）3 秒たった（起動の案内などの画面に Enter が効かないように）。利用者が最後にキーを打って 3 秒たった
  - フックが来ているなら、会話が終わっていない
  - **タブの PowerShell の子か孫（2 段まで）に `codex.exe` か `node.exe`（npm で入れたとき）が動いている**（AI が落ちて会話の終わりのフックが来なかったとき、打った伝言が PowerShell のコマンドとして動いてしまうため。2026-11-25 のレビュー）。動いていなければ、動いていない印にする
- そろったら、端末の画面の下 20 行を読み（`terminal.html` の `tsuzuriScreen(20)`）、次のときだけ打つ（キーの流れから書きかけを推し量るのは、フォーカスの知らせなどが混ざって当てにならなかった。2026-11-25）
  - 作業中の表示 `esc to interrupt` がどこにも無い
  - 入力欄の行（`›` で始まる、いちばん下の行。画面の下から 6 行以内）に、空の印 `Ask Codex to do anything` がある（画面の下のどこかに印があるだけでは良しとしない。会話の表示に残った文字や、確かめの画面の上に残った文字に釣られないため）
- 打つ: 受け箱の先頭を入力欄に置き、回数を 1 足す。フックが来ているタブなら、作業中と「受け付けを待つ」の印を付ける（話しかけたフックが来るまでに重ねて打たない）
- **Enter は 400 ミリ秒置いて別に送る**（一度に流し込むと、Codex が貼り付けとみなして Enter を改行にすることがある）。送る直前に画面を読み直し、入力欄の行に伝言の本文の頭 12 字（見出しの「【#n からの伝言】」を除く。見出しは同じ差出人の前の伝言と同じなので見分けにならない）か `[Pasted Content`（長いと Codex が畳む）があるときだけ送る。無ければ送らず「入力欄に伝言を置きましたが、送りませんでした」と知らせる（その間に確かめの画面が出ていれば、Enter が答えとして効くため）
- 打った伝言が 15 秒たっても頼みとして受け付けられなければ、待つ印と作業中の印を外す（入力欄に残っていれば、画面の確かめで次を打たない）
- **フックがまだ来ていないタブ**（フックを信頼する前に始まった会話など）でも、画面の確かめで渡す（2026-11-25。利用者の判断。ボタンを押さずに届くように）。作業中かは分からないが、確かめの画面・書きかけには打たない

### 上限とボタン

- 利用者が話しかけないまま自動で渡すのは、**タブごとに 4 回まで**（AI どうしがやりとりし続けないための歯止め）。数え直すのは、利用者が打った頼みの UserPromptSubmit（頼みに「【#」が無い）・フックが来ていないタブで利用者が Enter を押したとき・「伝言を渡す」を押したとき
- 渡せない伝言（上限を超えた・AI が動いていない）は受け箱に残し、見出しに「●伝言」、そのタブを選んでいれば欄の下に「伝言を渡す: <先頭 60 字>…（ほかに n 件）」を出す
- 「伝言を渡す」: タブの下で AI が動いていなければ渡さない（「タブで codex と打って起動すると、渡します」）。回数を 0 にし、作業中なら手が空いたら渡す。そうでなければ画面の確かめをせずに入力欄に置いて Enter（書きかけがあれば、その後ろに付く。ツールチップに書いた）
- 利用者がタブに打った: 最後にキーを打った時刻を覚える（ESC で始まる制御の並び（フォーカスの知らせ・矢印・マウス）は数えない）。Esc・Ctrl+C は作業を止めることがあり、そのときは作業の終わりのフックが来ないので、作業中の印も外す（次に道具を使えば PreToolUse で立て直す）
- **受け箱は保存しない**（AiLink を閉じると、渡していない伝言は消える）

## 13. Excel の一覧

`Views/ExcelListView.cs` と `MainWindow.Excel.cs`。

- ブックを並べ、その下に字下げしてシート。保存していない新しいブックは「（未保存）」、保存していない変更（`Workbook.Saved` が偽）があれば名前の右に「 ※」（2026-11-25。利用者の判断。AI の分も、手で直した分も。NOW・RAND などの式があると、開いただけでも付く）
- 隠れたブック（PERSONAL.XLSB など）は出さない。AI に渡さない場所のブック（OneDrive・SharePoint などの `://` の場所も）は選べない
- 読み直す: 窓が出たとき・窓に戻ったとき・「更新」・AI が書いた・台本を動かしたあと。裏で読み（5 秒まで）、AI に渡さない場所かもそこで決める（つながらない共有フォルダで画面を止めないように）
- 無くなったシート（閉じた・名前を変えた・保存していないブックの Excel を開き直した）は、選びから外して知らせる
- **選び方はエクスプローラーと同じ**（2026-11-26。利用者の判断）: 押すとそれだけ（ほかは外れる）・Ctrl で足す（全部選んでいれば外す）・Shift で起点からの範囲だけ・Ctrl＋Shift で起点からの範囲を足す。起点は Shift なしで最後に押した行（一覧に無ければ押した行）。ブックの行はそのブックのシート全部（2026-11-25。シートがとても多いブックを、1枚ずつ押さずに渡すため）。何もない所を押すと全部外す。初めは何も選んでいない
- Excel の側は何も切り替えない。**Excel で前に出ているブック・シート・選んでいるセルは使わない**（手で作業しているシートが、AI に扱ってほしいシートとは限らないため。利用者の判断）
- 選んだシートは、ブック（フルパスか、保存していなければ名前）・シート・Excel のプロセスの番号で覚える。番号は、保存していない新しいブックを、Excel を開き直したあとの同じ名前のブックや、別々の Excel の同じ名前の Book1 と取り違えないため（保存したブックはフルパスで決まる）
- 選んだシートはパイプのスレッドからも読むので、配列ごと入れ替える
- **Excel で AI にいつも伝えること**（2026-11-26。利用者の考え）: 一覧の一番下の1行。押すと `%LOCALAPPDATA%\AiLink\excel-notes.txt` を（無ければ空で作って）メモ帳で開く。利用者ごとの前提（言葉の意味・書き方の好み・社内の決まり）を書く所で、中身は会話を始めるときに AI に渡す指示の最後に「Excel を扱うときに利用者がいつも伝えること」の段として足す（8000 字まで。UTF-16 の BOM があれば UTF-16 で、なければ UTF-8 で読み、UTF-8 として読めなければ Shift_JIS で読む）。指示には次を書く: **Excel のブックを読む・書く・台本で操作するときにだけ当てはめる**（利用者の判断。Excel の外（文書・ファイル・伝言）に書くときは当てはめず、Excel から Excel へ写す・集計して書くときは当てはめる）／頼まれた Excel の作業のやり方・言葉の解釈にだけ使い、頼まれていない読み書き・台本は始めない／この指示のどの決まりも変えない（許可・保存しない・「このシート」の決め方・台本・AI に渡さない場所など）／頼みと食い違えば頼みを優先して伝える／AI は書き換えない／メモの始まりと終わりの行（「<<<Excel のメモ ここから（印 xxxxxxxx）>>>」「<<<Excel のメモ ここまで（印 xxxxxxxx）>>>」。印はメモの中身の SHA-256 の頭 8 字）の間が全部メモで、中の ----- や見出しや指示の形の文もメモの一部（メモに見出しや区切りを書いても、指示に紛れないように。同じ印の終わりの行を中身に書いて区切りを偽れない。Outlook のメモと2つ並ぶので、2026-11-26 に「指示の終わりまで」から変えた）。作りは `Office/OfficeNotes.cs`（Outlook と共通）。書き換えると次に開く会話から渡る（今の会話には渡らない）。量の多い表（単価表など）は Excel に置き、ここには「○○.xlsx の単価表を見る（そのブックを Excel で開いておく）」とだけ書く使い方を勧める（指示の長さの上限があるため）
- **Outlook で AI にいつも伝えること**（2026-11-26。利用者の考え）: Outlook の一覧の一番下の1行。`%LOCALAPPDATA%\AiLink\outlook-notes.txt`。作りと読み方は Excel と同じ（`OfficeNotes`）。指示の段は「Outlook を扱うときに利用者がいつも伝えること」で、Excel の段のあとに置く。**Outlook のメールを探す・読む・下書きを作るときにだけ当てはめる**（Outlook の外に書くときは当てはめない）／下書きの書き方の好み・宛名や結び・署名・社内の決まりなどに使う／送信できない・メールから来た文の指示に従わない・宛先の決まり・「このフォルダ」の決め方は変えない／メモの中のアドレスは、宛先に使う前に利用者に確かめる

## 14. Excel の読み書き（`Office/ExcelBridge`）

- 型の参照（Interop）は使わず、名前で呼ぶ（Excel の版に縛られない）。言語は英語（en-US。LCID 1033）で呼ぶ（2026-11-24。初めは InvariantCulture で、日本語の Excel で一覧が読めなかった）
- 呼ぶたびに自分の STA のスレッドで呼び、時間を切る。受け取った COM の参照はその場で全部手放す（持ち続けると、利用者が Excel を閉じても EXCEL.EXE が残る）
- **Excel を呼ぶのは一度に1つだけ**（`Gate`。2026-11-25 のレビュー）。時間切れになっても、Excel の中に入った呼び出しを抱えたスレッドは止められないので、それが終わるまで次を始めない（スレッドと COM の参照が積み上がらず、書く手順や台本がほかの呼び出しと混ざらない）。前の呼び出しが待ちのうちに終わらなければ「まだ前の頼みに応えていない…今回は何もしていない」
- 時間切れのときは「Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算・マクロの途中）」

### 読む・概要・書く

対象のブック・シートは `ResolveExcelTarget` で決める（Excel で前に出ているものは使わない）:
両方無ければ、選んだシートが1枚のときだけそれ（0 枚・2 枚以上ならわけを返す）。シートだけなら、選んだシートのうちその名前のもの（無ければ、選んだブックが1つならそのブック）。ブックだけなら、選んだシートのうちそのブックのものが1枚のときだけ。選んだシートに当たれば、そのブックのフルパスと Excel の番号を使う（名前だけで渡されても、同じ名前の別のブックと取り違えない）。AI に渡さない場所のブックの選びは数えない。

| 頼み | 待つ | 中身 |
|---|---|---|
| 読む | 10 秒 | 範囲（空なら使っている範囲全体）と使っている範囲が重なる所。セル 20000・列 200 まで、1 セル 300 字まで（改行は ↵、タブは →）。`<ai>\excel-read-<12 桁>.tsv` に、`# ブック`・`# シート`・`# 範囲` の行、1行目が列の名前・各行の頭が行の番号の表、式のセルは値のあとに「（式 =…）」、多ければ最後に「先頭の n 行・左の n 列だけ」 |
| 概要 | 45 秒 | `<ai>\excel-overview-<12 桁>.txt` と、見た目の画像 `.png`。大きさ（行・列・A4 で何枚分か・見込み）・見た目（素か整えたか）・書式のあらまし・結合・列の幅・行の高さ・隠した行と列・図形・グラフ・テーブル・ピボットテーブル・名前の定義・条件付き書式・入力規則・コメント・ハイパーリンク・見出しの色・保護と、セルの値と式。書式は範囲でまとめて聞き、そろっていなければ半分に分ける（15 秒・1500 範囲まで）。画像は `Range.CopyPicture` でクリップボードを通す（利用者がコピーしていたものは消える。戻さない。利用者の判断）。300 行・60 列・3000 ポイントまで、長い辺 2400 ピクセル。1000 行を超えるシートは上の 60 行だけ |
| 書く | 10 秒 | `-Book`・`-Sheet` が無ければ断る（許可をもらったあとに利用者が選び直しても、許可をもらったのと別のシートに書かないように）。AI に渡さない場所のブック・場所の無い頼みも断る。中身はタブ区切りで、最後の空行は落とす。書く範囲は「行の数 × いちばん長い行の列の数」で、列の足りない行は空のセルで埋める（埋めた数を答えに書く）。セルには手で打ったのと同じに入れる（`Formula2`。無い古い Excel だけ `Formula`）。保存していない新しいブックは、選んだときと Excel の番号が違えば書かない |

- 書くのが時間切れになったら、まだ書き始めていなければ書かない。書き始めていたら、Excel が応えたときに書き終わるか途中で失敗し、そのとき知らせの行に出す（AI には「応答しない」と返している）
- 書く途中で失敗したら「途中まで書けているかもしれない」と返す
- 書けたら「書いた: 見積.xlsx › 明細!B3:D5（n 行 × n 列）。保存はしていない（保存は利用者がする）。Excel の Ctrl+Z では戻らない」と返し、知らせの行に出し、一覧を読み直す（「※」）

## 15. `excel.ps1`

AI が動かす台本。パイプで AiLink に頼むだけ。

- **呼び方の誤りは、何も送らずに断る**（ファイルも残す）: `-Write`・`-Script`・`-Overview` を2つ以上・知らないパラメーターや名前の無い値（`[CmdletBinding(PositionalBinding = $false)]`。打ち間違えた `-Wirte` で読むことにならないように）・`-Script` に `-Book`/`-Sheet`/`-Range`・`-Script` 以外に `-Timeout`・1〜540 の外の `-Timeout`・`-Overview` に `-Range`・`-Book`/`-Sheet`/`-Range` の無い `-Write`
- Tsuzuri と同じファイル（`Tsuzuri/Ai/excel.ps1`。2026-11-26）。`AILINK_PIPE` があれば AiLink として動き（呼び名は「AiLink」）、無くて `TSUZURI_AI_PIPE` があれば Tsuzuri として動く（呼び名は「the editor」、作業のファイルは台本の隣）。AiLink は会話のタブを開くときに `TSUZURI_AI_*` を消し、Tsuzuri は `AILINK_*` を消す（片方から片方を起動しても取り違えない）
- どちらも無ければ「TSUZURI_AI_PIPE is not set and AILINK_PIPE is not set…」。書く・台本で `AILINK_NAME` が無ければ断る
- **作業のファイル**: `AILINK_WORK`（無ければ台本の隣）の `excel-write-<AILINK_NAME>.tsv`・`excel-script-<AILINK_NAME>.ps1`。`-File` は無い（2026-11-26 に外した。許された場所の外のファイルを送る抜け道だった）
- **ファイルを取る**: つなぐ前に、作業のファイルを自分だけの名前（`…-sending-<12 桁>.tsv`）に移してから読む（移せた呼び出しだけが送る。古い中身を二度送らない）。UTF-8 として厳しく読み、読めなければ元の名前に戻して断る。つなげなかった・送る前に失敗したときも、元の名前に戻す（その間に新しいファイルができていれば戻さない）。送り始めたら、答えによらず消す。消せずに残った `excel-*-sending-*` は、AiLink が次に起動したとき半日より古ければ消す
- つなぐのを待つのは 15 秒
- **答えを待つ時間**（台本を動かす道具の待ち時間より先にあきらめ、下の文を AI に返すため）: 台本は `-Timeout`＋25 秒、概要は 105 秒、ほかは 90 秒。この PowerShell のプロセスが始まった時刻から数える（遅い PC では起動に数秒かかる）。指示では、道具の待ち時間を 120 秒以上（台本は `-Timeout`＋30 秒以上）にさせている
- 送り始めてからの失敗・時間切れ・読めない答えは、書く・台本なら「it may or may not have changed Excel: read it to check before trying again」を添える
- 読む・概要の `ok` に、ファイルのパスが無ければ失敗にする。台本の答えは Base64 をほどいて出す
- 失敗は終わりの番号 1

## 16. 台本（`excel.ps1 -Script`）

AI が Excel の COM を使う PowerShell の台本を書き、AiLink が確かめてから、別の PowerShell のプロセスで動かす（2026-11-25。利用者の判断）。
別のプロセスにするのは、COM の参照を持ち続けないため（プロセスが終われば全部手放される）と、止まっても AiLink を巻き込まないため。

`ServeExcelScript` の順:

1. 終わりの印・空の台本を確かめる。待つ秒は既定 60、540 まで（AI のコマンドの道具の上限 600 秒より、待つ分・止める分だけ短く）
2. **禁止の語**（大文字・小文字を問わず、語の一部・コメント・文字の中でも）: `save`（Saved・AutoSave・Unsaved も）・`Close`・`Quit`・`ChangeFileAccess`・`Stop-Process`・`taskkill`・`SendKeys`・`Kill`・`DisplayAlerts`・`.Open…`・`.Run`・`RunAutoMacros`・`ExecuteExcel4Macro`・引数を渡した `Workbooks.Add(`・`FollowHyperlink`・`Invoke-Item`・`Start-Process`・名前を文字や変数にしたメソッドの呼び出し（`.'Open'(…)`・`.$m(…)`）。保存・閉じる（利用者の保存していない作業まで消える）・Excel を終える・読み込み直す・プロセスを止める・キーを送る（Ctrl+S で保存になる）・確かめを消す・開く（AI に渡さない場所のブックを開いて読む道にもなる）・マクロを動かすのをさせない（2026-11-25。利用者の判断）
3. **AI に渡さない場所**: 台本の文字を Bash のコマンドと同じに確かめる（台本は AI の道具のフックを通らないため。作業の場所は AI のフォルダ）
4. Excel への呼び出しを1つずつ流す中で（`RunExclusive`。10 秒待って前が終わらなければ「まだ前の頼みに応えていない」）、包み `<ai>\excel-script-run-<Guid>.ps1` を BOM 付きの UTF-8 で書き（PowerShell 5.1 は BOM の無い台本を ANSI で読む）、次で動かす

```
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<包み>"
```

包みの中身:

```powershell
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
<excel-guard.ps1 の中身>                         # 置いたファイルを読み込まず、写して入れる
$__text = …FromBase64String('<台本>')…            # 台本の文字も埋める（C# で確かめたあとに差し替えられないように）
$__problem = Test-ExcelScript $__text
if ($__problem) { [Console]::Error.WriteLine($__problem); exit 86 }
try { $excel = [Runtime.InteropServices.Marshal]::GetActiveObject('Excel.Application') } catch { …'Excel is not running'; exit 87 }
Remove-Item -Path Function:\Test-ExcelScript
. ([ScriptBlock]::Create($__text))
```

- 窓は出さず、標準出力とエラーをまとめて返す（20000 字まで）。プロセスはアプリのジョブに入れる（AiLink が閉じたり落ちたりしたあとも、Excel をつかんだまま動き続けないように）
- 時間を過ぎたら止め「途中まで操作されているかもしれない」。終わったあと出力を待つのは 5 秒まで（台本がほかのプログラムを起こして出力をつないだままだと終わりが来ないため）
- 終わりの番号: 87 は「Excel が動いていない。何もしていない」、86 は「台本を動かさなかった（使えない書き方: …）」と使える形の案内、ほかの 0 でない番号は「エラーで止まった。止まるまでの操作は Excel に残っている」
- 包みは終わったら消す。動かしたあと、知らせの行に出し、一覧を読み直す

### 台本の書き方の確かめ（`excel-guard.ps1` の `Test-ExcelScript`）

2026-11-26。利用者の判断。台本は利用者の権限で囲いの外で動くので、語で見るだけでは足りない（`'Sa'+'ve'`・`[IO.File]::Delete`・`& cmd` などが通る）。
PowerShell の構文解析で、許した形だけを通す（断る一覧ではなく許す一覧。断る一覧では Excel の危ないメンバーを数えきれなかった）。

- 通す: 変数・代入・制御文（if・foreach・for・while・do・switch・try・break・continue・return・throw）・演算子・文字と数・配列・ハッシュ表・`{ }`、名前をそのまま書いた許す一覧のメンバー（セル・書式・シート・テーブル・グラフ・範囲から作るピボットテーブル・名前・ページ設定・ワークシート関数と、文字・数・日付・配列・リストのもの）、一部のコマンド（Write-Output・`{ }` を渡した ForEach-Object と Where-Object・Select-Object・Sort-Object・Measure-Object・Group-Object・Format-Table・Out-String・Out-Null・Get-Date など）、一部の型（`object[,]`・`[Type]::Missing` など）
- 断る: ほかのコマンド・`&` と `.`・関数の定め・`exit`（終わりの番号で何が起きたかを見分けるため）・ファイルへのリダイレクト・`$env:` などのドライブの変数・`$ExecutionContext` と `$Host`・名前を文字や変数で組み立てた呼び出し・`-as`/`-is` に文字で書いた型・一覧に無いメンバー・自分で付けた名前のプロパティ（`$x.名前`。表はハッシュ表にして `$h['名前']`）・`$PSDefaultParameterValues`・`switch -File`・共通のパラメーター・変数に入れた Workbooks・Sheets などのコレクション・Item に配列や式（型を付けた変数・そのまま書いた名前や番号だけ）・ファイルを渡す Workbooks.Add とテンプレートを渡す Sheets.Add・範囲でないピボットテーブルとテーブルの元・Paste と PasteSpecial（利用者のクリップボードを読む）・Excel 全体の設定（ScreenUpdating・Calculation など。途中で止まると利用者の Excel がそのままになる）

細かい一覧は `Tsuzuri/Ai/excel-guard.ps1` と Tsuzuri の SPEC.md にある。変えたら `tools/excel-guard-test.ps1` を回す。

### 台本の書き方の決まり（AI への指示）

2026-11-26。利用者の希望。Excel の COM と PowerShell 5.1 でよく止まる所を調べ、指示の「台本の書き方の決まり」にまとめた。あらまし:

- 値は 1×1 でも型を付けた `object[,]` で `.Value2` に入れる（文字をそのまま入れると、PowerShell がその型を覚え、あとの配列の代入が「Object[,] を String にキャストできません」で止まった。Windows で確かめた）。日付は `ToOADate()` の数にし、表示形式で日付にする
- 文字のまま残す値（先頭の 0・長い番号・日付に見える文字・`=` で始まる文字）は、先に NumberFormat を `'@'` にしてから入れる
- 読むときは、1つのセルは配列でない・空は `$null`・エラーは int・`.Text` は値に使わない
- セルは `Cells.Item(行, 列)`、省く引数は `[Type]::Missing`、結合セルは左上で読み書きする
- 式は英語で `.Formula2`（無い Excel では `.Formula`）。Local の付くものは使わない
- 確かめの窓が出る操作（シートの削除・値の入った範囲の結合など）はしない
- 保護・フィルター・入力規則・重なり・シートの名前・幅と高さの上限は先に確かめる
- Sort の Header・Find の LookIn・LookAt・MatchCase・シートの Copy の Before か After は必ず渡す
- Excel が忙しいとき（0x800AC472・0x80010001・0x8001010A）は利用者に作業を終えてもらう
- 範囲でまとめて読み書きする。セルを写すのは `Copy(写し先)`（クリップボードを使わない）か Value2 で。列の幅は列ごとに

## 16-2. Outlook（`Office/OutlookBridge`・`OutlookLink`・`outlook.ps1`）

Tsuzuri と同じファイル（`Tsuzuri/Office/Outlook*.cs`・`Tsuzuri/Views/OutlookListView.cs`・`Tsuzuri/Ai/outlook.ps1`。2026-11-26。利用者の考え）。動きと COM の扱いは Tsuzuri の SPEC 19章「開いている Outlook（classic）のメールを探す・読む・下書きに保存する」。AiLink に置くのは `MainWindow.Outlook.cs` だけ。

- できること: フォルダの一覧・探す・1通を読む・下書きに保存する（新しいメールと返信）。送信・削除・移動などの道は作らない（利用者の判断）
- 一覧は左の列で、Excel の一覧とタブで切り替える（`Views/OfficeTabs.cs`）。選んだフォルダのパスを、今の状態の「Outlook」の段に書く。フォルダの頭の矢印でメールを件名で並べて選べ、選んだメールは「Outlook のメール」の段に書く（Tsuzuri の SPEC 19章）
- 下書きのファイルは会話のフォルダの `outlook-draft-tab<n>.txt`（指示の `{outlook-draft}`）。`outlook.ps1` も Codex のルールで確かめなしに動かす（台本はパイプで頼むだけ）
- AI への指示の Outlook の段は `OutlookInstructions.ForAiLink()`（Excel と同じ型の文の仕組み）

## 17. AI に渡さない場所（`Agents/AiGuard.cs`）

目印のファイル `.ailink-no-ai`（中身は空でよい）を置いたフォルダの下は、AI に渡さない。Tsuzuri の `.tsuzuri-no-ai` は見ない（2026-11-25。利用者の判断）。

- 今の状態に、その下のブックのシートの名前を書かない。一覧で選べない。excel.ps1 で読み書きしない。OneDrive・SharePoint などの `://` の場所のブックも、目印を確かめられないので同じに扱う
- 道具の前のフックで、道具の入力の `file_path`・`path`・`notebook_path` は上へたどって目印を探す。`command`・`pattern`・`glob` に書いた場所らしい部分（`C:\…`・`C:/…`・`\\server\…`・Git Bash の `/c/…`。32 個まで）も上へたどる。見つけた目印のフォルダは覚え、その場所の文字（`\` と `/`・重ね・大文字小文字・`/c/` の書き方を問わず、前後が名前の続きでない所）がどこかにあれば断る。上のフォルダからの検索（Grep・Glob）で、下に覚えた目印のフォルダがあれば断る
- 目印の有無は 30 秒覚える（置いた・消したのが効くまで最長 30 秒）。消えた目印のフォルダは覚えから外す
- 台本も、文字に目印のフォルダのパスがあれば動かさない（16章）

防げないこと: コマンドに場所を書かずにたどる道（`cd` を重ねる・`*` で広く読む）。フックが動いていない・AiLink に届かないときの道具の前の確かめ（9章）。台本の中で、AI に渡さない場所のブックに COM で触ること（指示で守らせる。触る前に excel.ps1 で読ませれば断られる）。

## 18. Codex（`Agents/CodexAgent.cs`）

- 実行ファイルを探す順: `agentExecutable` → `CODEX_INSTALL_DIR`（利用者の設定、無ければプロセス）の `codex.exe` → `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` → PATH（プロセス・利用者・PC の順に読み直す。AiLink を起動したあとに入れても見つかるように）の `codex.exe`・`codex.cmd` → `%APPDATA%\npm\codex.cmd`
- この PC で動かない exe は飛ばす（2026-11-25 のレビュー。x64 の PC で「入れる（ARM64）」を押すと ARM64 の codex.exe が置かれ、見つかったことにして案内が消えたあと、起動できずに入れ直す手段が無くなった）。exe の頭（PE）の CPU の種類を見て、ARM64 の exe は ARM64 の PC でだけ動くとみなす。exe でないもの・読めないものは動くとみなす
- AI が動いているかを見るプロセスの名前: `codex.exe`・`node.exe`（npm で入れたとき）
- 画面の印（2026-11-25 に Windows の Codex v0.172 で確かめた）: 入力欄の行の頭 `›`（U+203A）、空の入力欄 `Ask Codex to do anything`、作業中 `esc to interrupt`
- 会話の番号は Guid の形（2026-11-25 にフックで確かめた）。会話は `%CODEX_HOME%`（無ければ `%USERPROFILE%\.codex`）`\sessions\年\月\日\rollout-…-<番号>.jsonl` に残る。話しかける前に閉じた会話は残らないことがあるので、無ければ戻さない（`sessions` が無い・調べられなければ戻す）

## 19. Codex が入っていないときの案内（`MainWindow.AiSetup.cs`）

2026-11-25。利用者の考え（会社で配るので、入っていない人に入れ方を示す）。

- 起動したとき見つからなければ、会話のタブを開かず、会話の欄に案内（何をするものか・手順・ボタン）を出す
- 「入れる（x64）」「入れる（ARM64）」: 利用者が押したときだけ、別の PowerShell の窓（`-NoLogo -NoExit -ExecutionPolicy Bypass`）で公式の `install.ps1` を取ってきて動かす。終わっても窓は閉じない（結果を読めるように。問いに答えられるように普通の窓）。手順は `%TEMP%\ailink-install-<12 桁>.ps1` に書いて `&` で動かし（手順の中の `exit` で窓ごと閉じないように）、終わったら消す
  - 公式の手順は `[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture` で CPU を調べるが、Windows の PowerShell 5.1 ではこれが無く、Set-StrictMode の下で止まる（Windows で試して分かった）。この1か所を選んだ版（`'X64'`・`'Arm64'`）に差し替える。公式の手順がこの書き方をやめていれば、そのまま動かす
  - 入れ終わったあとの「Start Codex now?」は聞かないよう差し替える（答えると Codex がその窓の中で、AiLink の外で起動してしまう）。ほかの問いは残す
  - この PC に合うと見た版（`IsWow64Process2` で本当の CPU を調べる）のボタンを青くする。見当が外れていても選び直せる
- 「もう一度確かめる」「コマンドをコピー」（`irm https://chatgpt.com/codex/install.ps1 | iex`）「説明のページ」
- 探し直すのは、窓に戻ったとき（Activated）と、案内を出している間 3 秒ごと。入れる窓が開いている間は探し直さない（入れる手順は途中で codex.exe を置いてから確かめるので、確かめる前に見つけてしまう）。見つかれば案内を消して会話のタブを開く

## 20. 試験と確かめ

| 試験 | どこで | 中身 |
|---|---|---|
| `bash tools/check-build-ailink.sh` | Mac | `AiLink.csproj` に並べ忘れた `.cs` が無いか。XAML の部品の代わり（XamlStub.cs）を作って C# をコンパイルする。XAML そのものは Windows のビルドで確かめる |
| `tools\excel-client-test.ps1` | Windows | `excel.ps1`（両方のアプリで同じファイル。AiLink として・Tsuzuri として）を偽のパイプ（別のプロセス）を相手に動かす。呼び方の誤りで何も送らずファイルを残すか・作業のファイルを一度だけ送って消すか・壊れた答えを失敗にするか |
| `tools\excel-guard-test.ps1` | Windows | `excel-guard.ps1` が、ふつうの台本を通し、ファイル・ほかのプログラム・ネット・リフレクション・マクロ・保存や閉じる・開くに触る台本を断るか |

`AiGuard`（AI に渡さない場所）と `ChainsCommands`（台本へのつなぎ）の AiLink 用の試験は、まだ無い。

## 21. 知られた限り

- フックを信頼していない・AiLink に届かないとき、道具の前の確かめ（AI に渡さない場所・台本へのつなぎ）は効かない（9章）
- セルに書く式（外のブック・フォルダへの参照・WEBSERVICE・ブックの VBA のユーザー定義関数を呼ぶ式）は、Excel の働きとして通る。指示で書かせないだけ
- Excel の元に戻す（Ctrl+Z）は効かず、履歴も消える
- 概要を読むと、利用者のクリップボードが消える
- 伝言を入力欄に打つのは、Codex の画面の文字（`›`・`Ask Codex to do anything`・`esc to interrupt`）に頼っている。Codex の版で変われば、自動では渡らなくなる（ボタンと作業の終わりのフックでは渡る）
- 受け箱は保存しない
- PowerShell の実行ポリシーをグループポリシーで決めている PC・制限言語モードの PC では、台本もフックも動かない（README）

# AiLink への切り出しの手順書

AI連携ツール（AiLink）は、Tsuzuri の AI の欄（会話のタブ・タブどうしの話・今の状態の渡し方・共通ターミナル）と
ランチャー（ランチャー・ファイラー・Excel の一覧と読み書き）だけを切り出した単独のソフト。Tsuzuri は変えない。
AI は Codex CLI に固定するが、つなぎ口（`IAgent`）で抽象化して、ほかの AI に替えられるようにする。

この文書は切り出しの作業用（2026-11-25）。作り終えたら README・SPEC に移して消す。

## 決めたこと（利用者）

- 置き場所はこのリポジトリの `AiLink/`。ソリューションは Tsuzuri と同じ `Tsuzuri.sln`（利用者の判断。動かすほうはスタートアップ プロジェクトで選ぶ。AiLink は Tsuzuri のプロジェクトを参照しない）。
- 画面: 左の列に開いている Excel の一覧、仕切り、右に AI の欄。ランチャーとファイラーは持たない（2026-11-25 に外した。利用者の判断。いつも起動している Excel だけを相手にする）。
- プロジェクトフォルダは持たない（2026-11-25。利用者の判断。扱うのは開いている Excel で、ファイルが要るときは端末へ落としてもらう。落としたファイルのパスは入力欄に置く）。
- AI（Codex）が見つからなければ、会話のタブを開かず、AI の欄に入れ方の案内を出す（MainWindow.AiSetup.cs。「入れる」で公式の手順を別の PowerShell の窓で動かす。窓に戻るたびに探し直す）。
- AI に渡さない場所の目印は `.ailink-no-ai` だけ（Tsuzuri の `.tsuzuri-no-ai` は見ない）。
- Codex に起動のフラグでフックを渡せなければ、`~/.codex` にフックを書き込んでよい（AiLink の外で動かした codex では、中継が何もせずに終わる）。
- タブどうしの伝言は、受け取ったタブの入力欄に置くだけ（Enter は利用者が押す）。
- 設定ファイルは exe の隣の `ailink.json`。置き場所は `%LOCALAPPDATA%\AiLink\ai`。パイプは `AiLink-<プロセス番号>`。
  環境変数は `AILINK_TAB`・`AILINK_PIPE`・`AILINK_SSH_HOST`。WebView2 のデータは `%LOCALAPPDATA%\AiLink\WebView2`、ホスト名は `ailink.local`。

## 写すもの（Tsuzuri → AiLink。名前空間を AiLink.* にする）

| Tsuzuri | AiLink | 直す所 |
|---|---|---|
| Views/PseudoConsole.cs・TerminalView.cs | Views/ | ホスト名・WebView2 のフォルダ |
| Views/ShellActions.cs・ShellIcons.cs・FileIcon.cs・ScrollThumbLength.cs | Views/ | なし |
| Views/LauncherView.cs | Views/ | AppSettings の Tiles の部分と LauncherTile を写す |
| Views/FilerView.cs | Views/ | Theme の5色を Palette に。添付フォルダの処理を外す。コールバックを差し替える |
| Views/FilerGit.cs | Views/ | DocumentHistory.GitPath → GitLocator |
| Views/ExcelListView.cs | Views/ | using を消すだけ |
| Office/ExcelBridge*.cs | Office/ | なし |
| Markdown/AiGuard.cs | Agents/AiGuard.cs | 道具の名前ではなく ToolCategory。目印 `.ailink-no-ai` |
| Ai/excel.ps1・terminal.ps1・ssh-*.ps1 | Ai/ | 環境変数の名前・文 |
| Terminal/・ConPty/ | 同じ | そのまま（Content） |
| MainWindow.AiShell.cs | MainWindow.AiShell.cs | 始めるフォルダ・WindowKey を外す |
| MainWindow.Excel.cs | MainWindow.Excel.cs | AiGuard.Check(ToolCategory.Shell…)・`_launcher` の確かめ |
| App.xaml の色・スタイル | App.xaml | 色: PageBackground ChromeBackground SidebarBackground BorderBrushSoft TextPrimary TextMuted AccentBrush AccentText AccentFill AccentFillHover AccentFillPressed HoverFill PressedFill SelectionFill SidebarText FrameBorder InputBorder ThumbFill ThumbFillHover。スタイル: ToolButton ScrollThumb ScrollPage ScrollThumbLength 暗黙の ScrollBar GridSplitter RowSplitter ToolTip TextBlock |

## MainWindow.Ai*.cs の仕分け

- そのまま: タブの見出し・選ぶ・閉じる（Ai.cs 561-771）、StartAiListener、CopySshFolder、WriteIfChanged、WrittenPath、AiPipeSecurity、Quote、Fenced、StopAi、DeleteStaleAiFiles、WriteAiContext、AiTopic の見出しの部分、FindAiTab。
- 変える: AiTab（Pin を消し、IdleSince・Inbox を足す）、AddAiTab（起動のコマンドは IAgent）、AiStartFolder（文書を外す）、OnAiSession・StoreAiTabs・RestoreAiTabs（SessionExists は IAgent）、AiCommandLine（大半を IAgent へ）、ServeAiRequest（`hook\t<event>\t<tab>\t<b64 json>` の1形式＋shell-*・excel-*・talk-*）、HandleAiPrompt（保存・図を外す）、HandleAiChanged（ファイラーと Excel の一覧を読み直すだけ）、ShowAiContextSummary、RefreshAiFileLinks（UserFiles は IAgent）、AiContext（下）、AiInstructions（下）。
- 捨てる: 文書・ページ・カーソルの固定（AiPin の大半）・図（RenderAiDrawings ほか）・カレンダー・WordPress・SaveForAi・ReloadChangedDocuments・AppendDocumentFacts など・AiHttp.cs（HTTP のフック。中継の exe にそろえる）。

今の状態（AiContext）に残す段: 頭の行（[AiLink の今の状態 …]）・「この会話は #n」（足す）・どの会話か分からなかった・Excel・共通ターミナル・タブどうしの伝言（足す）。

指示（AiInstructions）に残すもの: 呼び名のうち会話を始めたフォルダ・前に書いた状態の扱い（段の名前を絞る）・タブの #番号と伝言（talk.ps1 に書き換える）・Excel・共通ターミナル・SSH・日本語で短く。道具の名前は雛形の `{agent}` `{write-file}` `{read-file}` `{view-image}` `{shell}` `{shell-timeout}` をアダプタが埋める。

## AI のつなぎ口（Agents/）

フックは AI によらず中継の exe（`AiLink.HookRelay.exe`）1つで受ける: 標準入力を全部読み、`AILINK_TAB` が無ければ exit 0、
`AILINK_PIPE` に `hook\t<イベント>\t<tab>\t<b64 json>` を送り、答え `exit\t<code>\t<b64 stdout>\t<b64 stderr>` を出して終わる。

```
enum AgentEventKind { SessionStarted, SessionEnded, PromptSubmitted, BeforeTool, AfterTool, TurnStopped, Unknown }
enum ToolCategory { Read, Write, Search, Shell, ViewImage, Other }
class AgentEvent { Kind, TabId, SessionId, Cwd, Prompt, Source, ToolName, Category, GuardInputs, WrittenPaths }
class HookReply { Deny, Reason, AdditionalContext, Feedback }
class HookOutput { ExitCode, Stdout, Stderr }
class AgentRuntimePaths { BaseFolder, WindowKey, PipeName, RelayExe, InstructionsText }
class AgentLaunchContext { TabId, TabName, WorkingFolder, ResumeSessionId, StartNow }
class AgentVocabulary { AgentName, ReadFile, WriteNewFile, ViewImage, Shell, ShellTimeout, Search }
interface IAgent {
  Id, DisplayName, MissingHint, Vocabulary, SupportsNativeTalk, ExtraPathFolders
  FindExecutable(), PrepareRuntime(paths), BuildShellCommand(ctx, paths, exe), IsValidSessionId(id), SessionExists(id),
  ParseHook(eventName, json, tabId), FormatHookReply(ev, reply), UserFiles(folder), GenerateTopic(prompt, folder, timeoutMs)
}
```

Codex で最初に現物で確かめること:
1. フックを起動のフラグ（`-c` やプロファイル）で渡せるか。だめなら `~/.codex/hooks.json`（か config.toml）に中継を一度だけ入れる。
2. PreToolUse・PostToolUse が shell だけか、apply_patch・MCP でも起きるか（起きなければ、Stop でファイラーと Excel の一覧を読み直す）。
3. フックが受け取る JSON の項目の名前と、答えの形（exit 2 と stderr か、JSON か）。
4. 指示の渡し方（SessionStart の additionalContext か、`-c developer_instructions=…`。model_instructions_file は元の指示を置き換えるので使わない）。
5. session id の形と、会話を残すファイルの場所（`$CODEX_HOME\sessions\**\rollout-*-<id>.jsonl`？）。会話の終わり（SessionEnd が無ければ、PowerShell の関数で codex が終わったあとに中継を `--ended` で呼ぶ）。

## 作業の順番

1. 骨組み: AiLink.csproj（古い形・net48・C# 7.3・WebView2）、写すファイル、AppSettings、App.xaml、空の MainWindow（3列）、`tools/check-build-ailink.sh`。
2. 端末とランチャー: タブは PowerShell だけで起動。ランチャー・ファイラー・プロジェクトフォルダの段・共通ターミナル・パイプ・Excel の一覧と excel.ps1。（2026-11-25 に作った。Windows での確かめはまだ。AI に返す文（Excel・共通ターミナルの答え）は Claude を AI に置き換えただけなので、第3段の指示と一緒にレビューを通す）
3. Codex: 中継の exe、ServeAiRequest の hook、IAgent と CodexAgent、指示の雛形と AiContext（別のエージェントのレビューを通す）、会話を戻す・題・下端のファイル、talk.ps1 とタブどうしの伝言、README と SPEC。

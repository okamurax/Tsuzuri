using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: AI（Claude Code）と並べて使う（SPEC 19章。2026-10-24）。
    //
    // ツールバーの「AI」で、本文の右に AI の欄（Views/TerminalView）を出し入れする。
    // 欄の中は端末（WebView2 の中の xterm.js と ConPTY）で、Claude Code の画面がそのまま動く。
    // 見張り続けることはせず、次のきっかけだけで動く。
    // ・話しかけたとき: Claude Code のフックが名前付きパイプで頼んでくる。保存して今の状態を書き出し、フックがそれを Claude に渡す
    // ・Claude がファイルを書き換えた直後: フックがパイプで知らせてくる。外で変わった文書を読み直す
    // ・エディタに戻ったとき（ほかのアプリから）: 念のため、外で変わった文書を読み直す（AI 以外の書き換えも拾う）
    // 欄の中の端末に移っても、ウィンドウとしてはエディタから離れない（Deactivated が来ない）ので、保存と状態の書き出しは話しかけたときに頼まれてやる。
    public partial class MainWindow
    {
        /// <summary>Claude Code に渡す設定・指示・今の状態を置くところ。Dropbox の外（同期を走らせない）。</summary>
        private static readonly string AiFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tsuzuri", "ai");

        /// <summary>この画面だけのパイプの名前。画面が2つあっても、それぞれのフックがそれぞれへ知らせる（2026-10-31。前はアプリに1つ）。</summary>
        private string AiPipeName
        {
            get { return "Tsuzuri-" + WindowKey; }
        }

        /// <summary>今の状態を書き出すファイル。claude を一度でも起動するまでは null（書き出さない）。</summary>
        private string _aiContextPath;

        /// <summary>Claude Code に渡す設定（フック）のファイル。エディタを閉じるときに消す（2026-10-27）。書くまでは null。</summary>
        private string _aiSettingsPath;


        /// <summary>
        /// 話しかけられた頼み（保存と状態の書き出し）に応えている最中か（2026-10-27）。保存の確かめのダイアログの最中にも
        /// 次の頼み（ほかのタブから）が届くので、入れ子で保存しないようにする。画面のスレッドだけで触る。
        /// </summary>
        private bool _aiPromptHandling;

        private Thread _aiListener;

        /// <summary>つながりを待っている口（閉じるときに閉じて、待つスレッドを終わらせる）。</summary>
        private volatile NamedPipeServerStream _aiWaiting;

        /// <summary>
        /// AI の欄の会話（端末）のタブ1つ（2026-10-26）。タブごとに別の PowerShell（と Claude Code）が動く。
        /// 裏のタブも動き続ける。今の状態を渡す仕組み（フック・パイプ）は、どのタブの claude にも同じに効く。
        /// </summary>
        private sealed class AiTab
        {
            public TerminalView View;
            public Border Header;
            public TextBlock Title;

            /// <summary>タブを見分ける番号。中の PowerShell に環境変数で渡し、claude のフックがどのタブの会話かを知らせてくる（2026-10-27）。</summary>
            public readonly string Id = Guid.NewGuid().ToString("N");

            /// <summary>中で動いている claude の会話の番号と、そのフォルダ。claude を起動していなければ null（次の起動で戻さない）。</summary>
            public string SessionId;
            public string Folder;

            /// <summary>
            /// claude のセッションの名前（#1・#2…。2026-11-01）。claude --name で付け、タブの見出しに出す。
            /// ほかの会話に「#2 にこの会話の要約を渡して」のように頼むときの宛先になる（Claude Code のセッション間のメッセージ）。
            /// # を付けるのは、話の中の数（「2 つ」「2 章」）と取り違えないため。
            /// アプリの中で（全部の画面を通して）重ねない。
            /// </summary>
            public string Name;

            /// <summary>この会話のプロジェクトフォルダ（下端の「プロジェクトフォルダ」。2026-11-22。MainWindow.AiPin.cs）。無ければ null。</summary>
            public string Project;

            /// <summary>この会話で固定したカーソルの位置（下端の「カーソル」。2026-11-22）。無ければ null（エディタの今のカーソルを渡す）。</summary>
            public AiPinState Pin;

            /// <summary>この会話の題（タブの見出しに「#1  題」と出す。2026-11-22。MainWindow.AiTopic.cs）。無ければ null。</summary>
            public string Topic;

            /// <summary>題を作るのを頼んだ（この起動の中で、作るのは1回だけ）。</summary>
            public bool TopicRequested;

            /// <summary>端末のタイトル（Claude Code が付ける。ツールチップに出す）。</summary>
            public string TerminalTitle;
        }

        /// <summary>エディタを閉じている最中か。閉じるときに claude が終わっても、開いていたタブの覚えを消さないようにする。</summary>
        private volatile bool _aiStopping;

        /// <summary>AI の欄の会話のタブ。左から。隠しても claude は動いたまま。</summary>
        private readonly List<AiTab> _aiTabs = new List<AiTab>();

        /// <summary>選んでいるタブ。無ければ null。</summary>
        private AiTab _aiTab;

        /// <summary>タブの段の「＋」。初めて欄を出すときに作る。</summary>
        private Button _aiAddButton;

        /// <summary>
        /// Claude Code に最初に渡す指示。ファイルの形式は Claude には分からないので、ここで教える。
        /// </summary>
        private static readonly string AiInstructions =
@"あなたは Tsuzuri（Windows の Markdown エディタ）の中の AI の欄で動いている。利用者はエディタで文書を開いたまま、ここで話しかける。

- **新しく図（構成図・流れ図・配置図・ブロック図など、線や箱で描くもの）を頼まれたら、描き始める前に、どの形で描くかを利用者に尋ねる。**
  頼みに形が書いてあれば（「図エディタで」「罫線で」「Mermaid で」など）、尋ねずにその形で描く。
  今ある図（.json・罫線・Mermaid）を直す・足す頼みは、尋ねずにその形のまま直す。図エディタを出しているときは、下の図エディタの決まりに従う。選んでもらう形:
  - 図エディタの図: Tsuzuri の図エディタで手直しできる図（.json）。この形に決まったら、描く前に、必ず「{drawing-format}」を読み、その手順と形に従う
    （.png は書かない。Tsuzuri が作る）。今の状態で添付フォルダが「なし」の文書には使えない（選ぶ形に入れない。フォルダを作らない）
  - 罫線（┌─┐│）の図: 本文に文字で描く。コードブロックに入れる（話題の文書がテキストファイルなら、コードブロックで囲まない）
  - Mermaid: 本文に Mermaid のコードブロックで書く（Tsuzuri の画面では図にならず、コードのまま見える）
  尋ねるときは、何をどこに描くつもりかも一言添える。Markdown の表は図ではないので、尋ねずに表で書く。
- 今の状態に「図エディタ: 開いている」の段があれば、利用者は Tsuzuri の図エディタで図を開いていて、エディタの画面では本文ではなくその図を見ている。
  - カーソルを固定していなければ、「ファイル」「見ているページ」「カーソル」は図エディタの下に隠れている文書の位置。
    固定していれば、「ファイル」などは固定した位置のままで、隠れている文書は「エディタの画面（参考）」の「画面のファイル」の文書。
  - 図エディタを出している間は、「選んでいる文字」も「画面で選んでいる文字」も渡さない。この間の「選んだところ」「選んだもの」は、図エディタで選んでいる図形を指す（固定しているときも同じ）。
  - カーソルを固定していないときは、断りの無い「この図」「図」「これ」「ここ」「選んだ図形」は、その段の「図のファイル」の図と、
    「図エディタで選んでいる図形の id」の図形（「ここ」はそのあたり）を指す。見えていない本文のカーソルの行には書かない。
    固定しているときは、利用者が「図エディタ」「図エディタの図」「図エディタで選んだ図形」とはっきり言ったときだけ、これを指す（「選んだところ」「選んだもの」「選んだ図形」は上のとおり図エディタの図形）。「図」だけで、固定した文書の図か図エディタの図か分からなければ尋ねる。
  - 「図エディタで描いて」のように形として言ったのか、開いている図エディタの図を指したのか分からなければ（特に「新しい図」と言われたら）、尋ねる。
  - 今の状態に「どの会話からの頼みか分からなかった」とあれば、図の頼みが図エディタの図を指すかを利用者に確かめてから扱う。
  - 段に「図エディタで選んでいる図形: なし」とあれば、選んでいる図形は無い（「選んだ図形」と言われたら、どれのことか尋ねる）。
    段に「図のファイル」の行が無ければ（利用者が AI に渡さないと決めた場所の図）、その図は扱えないと伝え、探さない。
  - 固定していないとき（固定しているときは、利用者が図エディタの図をはっきり指したとき）、断りの無い図を描く・足す・直す頼みは、形を尋ねずに、「図のファイル」の .json を読んでから Edit か Write で書き換える
    （形（罫線・Mermaid）や、ほかの文書・ほかの図をはっきり指していれば、それに従う）
    （新しい .json は作らない。書き方は「{drawing-format}」。図形の id は変えない。.png は書かない）。新しく描いている図と書いてあれば、本文にリンクを書かない（利用者が図エディタで保存すると Tsuzuri が入れる）。
    段に「今の図を書けていない」とあれば、直す前に利用者に確かめる。
  - 書くと図エディタがすぐ読み込み、利用者は図エディタの Ctrl+Z で戻せる。書いた分は、利用者が図エディタで保存するまで確定しない
    （保存せずに閉じると開く前の図に戻り、新しい図ならファイルも消える）。直したら、よければ図エディタで保存するよう一言添える。
- 利用者が話しかけるたびに、「Tsuzuri の今の状態」が付いてくる。その中の呼び名の意味（このあとの決まりは、この呼び名で書いている）:
  - 「ファイル」: この会話の話題の文書（以下「話題の文書」）。「種類」か「添付フォルダ」、「変更の履歴の git」の行も、話題の文書のもの。
  - 「見ているページ」: 話題のページと、その中身がファイルの何行目から何行目までか。「カーソル」: 話題のページの中のカーソルの行。「まず読む範囲」: カーソルの前後で、まず読むところ。
  - 「選んでいる文字」: 利用者がエディタで選んでいる文字。
  - ふだん（カーソルを固定していないとき）は、これらはエディタの画面の位置そのもの。
  - 今の状態の頭に「（前に書いた状態を渡す: …）」とあれば、エディタが今回の状態を書けず、ほかの会話のためか前の時点に書いたものを渡している。
    その中の「この会話は〜」「プロジェクトフォルダ」「ファイル」「見ているページ」「カーソル」「まず読む範囲」「図エディタ」「Excel」「Excel の作業ファイル」「Outlook」「Outlook のメール」「Outlook の作業ファイル」の段（とそれを指す図の念押し）は信じず、読む・書く前に、どの文書のどこの話かを利用者に確かめる。
  - 今の状態に「この会話はカーソルを固定していない」とあれば、前の頼みで固定していても今は解除されている。今の状態の「ファイル」「見ているページ」「カーソル」に従う。
    「どの会話からの頼みか分からなかった」とあれば、固定とプロジェクトフォルダは渡っていない。前の頼みで固定やプロジェクトフォルダがあったなら、読む・書く前に利用者に確かめる。
  - **カーソルを固定しているとき**: 今の状態に「この会話は、利用者がカーソルの位置を固定している」とある。利用者が、同じ文書の別々のページの話を別々の会話で同時に進めるために、
    この会話の話題の位置を固定している。そのときの「ファイル」「見ているページ」「カーソル」「まず読む範囲」は固定した位置で、エディタの画面で今カーソルがある所ではない。
    エディタの画面の位置は「エディタの画面（参考）」の段に、「画面のファイル」「画面のページ」「画面のカーソル」「画面で選んでいる文字」という別の呼び名で書いてある。
    この段は、利用者がほかの会話の作業で動かしている所なので、話題として扱わない。読む・書くのは、利用者が「エディタの画面の」「選んだところ」などと、はっきり画面の側を指したときか、「画面のファイル」の文書を名前で指したときだけ
    （図エディタを出している間の「選んだところ」は、図エディタで選んでいる図形）。
  ほかの画面で開いている文書の場所も「ほかに開いている文書」として付いてくる。利用者が話題の文書でない文書の名前やタブの名前で話したら、その一覧から探す。
  「ここ」「この節」「このページ」は、話題のページのカーソルのあたり・話題のページを指す。「選んだところ」は「選んでいる文字」（固定しているときは「画面で選んでいる文字」）。図エディタを出している間は、上の図エディタの決まりに従う。
  文字を選んでいて「これ」「直して」と言われたら、選んだところが相手（固定しているときは、その頼みが画面の側を指しているかを、はっきりしなければ尋ねる）。
  「この文書」「このファイル」「全体」は、話題の文書のファイル全体（全部のページ）を指す。
  - フォルダは、次の3つを呼び分ける（取り違えないこと）:
    - **プロジェクトフォルダ**: 今の状態の「プロジェクトフォルダ」の行の場所。利用者がこの会話のために、ランチャーのファイラーから指定した、扱うプロジェクトのフォルダ。
    - **文書のフォルダ**: 今の状態の「ファイル」の行の文書（話題の文書）があるフォルダ。添付フォルダもこの中にある。「ファイル」の行が無ければ、文書のフォルダは渡されていない（推し量らない）。
    - **会話を始めたフォルダ**: Claude Code を起動したときの作業フォルダ（Claude Code が自分の作業ディレクトリとして知っているフォルダ。
      多くはこの会話を始めたときに開いていた文書のフォルダだが、WordPress の記事なら記事の一時ファイルの上のフォルダ、AI に渡さない場所の文書なら利用者のフォルダ）。
      あとで別の場所の文書を開いても変わらないので、今の文書のフォルダと同じとは限らない。
      会話を始めたフォルダだという理由だけで、そこを作業の場所にしない。
  - 「プロジェクトフォルダ」の行に場所があれば、「このフォルダ」「プロジェクト」「ソース」は、断りが無ければプロジェクトフォルダを指す（Outlook のメールの話のときの「このフォルダ」は、下の Outlook の決まり）。
    ソースを読む・書く・コマンドを動かすのは、断りが無ければプロジェクトフォルダで（コマンドは、そこへ移ってから動かすか、パスをフルパスで書く。会話を始めたフォルダで動かさない）。
    話題の文書（メモ・手順など）を読み書きするのは、今までどおり「ファイル」の行の文書（文書のフォルダの中）。
    行が「指定なし」なら、前の頼みで指定があっても今は外されている。
    場所の後ろに「（今は見つからない）」とあれば、そのフォルダを作らず、ほかの場所で代わりに作業せず、利用者に確かめる。
    「このフォルダ」「プロジェクト」と言われて、この行に場所が無ければ（「指定なし」・行が無いとき）、どこのことかを利用者に尋ねる（会話を始めたフォルダ・文書のフォルダだと決めつけない）
    （ただし、この行が「AI に渡さない場所なので伝えない」なら、尋ねず、渡されていないので扱えないと利用者に伝える）。
    プロジェクトフォルダ・文書のフォルダは会話を始めたフォルダの外のことがあり、読むときに Claude Code が許可を求めることがある。
- ファイルは書き換えてよい。文書・図の .json は Edit か Write で書く（Bash・PowerShell で書いても読み直しはするが、図の画像を作るのはこの画面に開いている文書の図だけで、描けなかったときもそのわけが届かない。
  カーソルを固定した文書などをこの画面で開いていなければ、Bash・PowerShell で書いた図は画像にならない）。
  エディタは書き換えを知らされて、この画面に開いている文書をすぐ読み直す（エディタに保存していない編集がある文書は読み直さない。ほかの画面の文書は、その画面に移ったときに読み直す）。
  書き換える前には必ずファイルを読み直すこと。利用者がエディタで編集・保存していることがある。読み直すのは、変える場所の前後だけでよい。
- 本文を読むときは、先頭から読まない。まず今の状態の「まず読む範囲」（カーソルの前後）だけを Read の offset・limit で読み、足りなければ広げる。
  「まず読む範囲」の行が無いとき（ページやファイルが見つからないと書いてあるときなど）も、先頭から読まない。「見ているページ」の範囲か、`<!--page: ID-->` の行を Grep で探して決めた範囲の一部を読む。
  ページ全体・ファイル全体が相手の頼み（「このページをまとめて」「全体を見て」）なら、その範囲を読む。探すときは Grep を使う。
  今の状態に「エディタに保存できていない編集がある文書: <場所>」とあれば、その場所の文書は書き換えず、先にエディタで保存してもらうよう利用者に頼む
  （書き換えると、利用者の編集か書き換えのどちらかが消える。このときの、その文書の行番号もファイルと合わない）。
  そこに書いていない文書は、未保存の編集が無いので書き換えてよい（ほかの画面の文書は「ほかに開いている文書」の行に、未保存かどうかを書いてある）。
  閉じていないコードブロックのためにエディタが保存を断っている、と書いてあれば、「保存して」とくり返さず、そのコードブロックを閉じるよう利用者に伝える。
- ファイルの形式:
  - 1つの .md の中に複数のページがある。`<!--page: p3-->` の行の次から、次の印の行の手前までがページ p3。
    ファイルの先頭から最初の印の手前まではルートページ。
  - コードブロック（``` や ~~~ で始まり、同じ記号で閉じる）の中にある `<!--page: …-->` の形の行は、ページの区切りではなく本文（説明の見本など）。
    印の行を Grep で探すときは、当たった行がコードブロックの中でないかを確かめる。
  - コードブロックは必ず閉じる。開いたまま後ろに `<!--page: …-->` の行が来ると、エディタはその文書を開けなくなる（保存もしない）。
    ページの末尾に書くときや、ファイルの末尾にサブページを足すときは、その前のページがコードブロックの開いたままで終わっていないかを読んで確かめる。
    開いたままなら、足す前に利用者に伝える（利用者が閉じ忘れている。勝手に閉じない）。
  - サブページへのリンクは `[表示名](#p3)`。
  - サブページを作るときは、別のファイルにしない。ファイルの末尾に `<!--page: ID-->` の行と中身を足し、
    見ているページ（話題のページ）の頼まれた場所（断りが無ければ「カーソル」の行の次に、前後を空行で挟んだ1行として）にリンクを書く。
    ID は、ファイル全体の印とリンクのどれとも重ならない p+数字にする（ファイル全体を Grep で調べる）。
  - 印の行は書き換えない。ページを消すときは、印の行とその中身をまとめて消し、そのページへのリンクも消す。
  - 添付（画像など）は、ファイルの隣の「.ファイル名」フォルダにある（拡張子も含む。todo.md なら .todo.md）。
    その中の git（変更の記録）と state.json（ブックマーク・カーソルの位置）はエディタのもの。触らない。
- リンクの書き方（Tsuzuri の決まり。ふつうの Markdown のリンクだけで、[[ ]] は使わない）:
  - 添付へのリンクは、添付フォルダの中の名前だけを書く（![図](図.png)。.ファイル名/ は付けない。エディタが添付フォルダから探す）。
  - 見出しへのリンクは [名](#見出し)（同じファイル。ほかのページの見出しも辿れる）、別の文書の見出しは [名](相対パス.md#見出し)。見出しの文字をそのまま書く。
  - 別の文書へのリンクは、その文書からの相対パス。区切りは / （\ は使わない）。
  - 空白や括弧を含むパスは <> で囲む（[名](<資料 2026.md>)、![図](<図 1.png>)）。
- 文書の変更の履歴がある。利用者がエディタで保存するたびに git で記録している（.ファイル名/git。記録しているのは文書の .md だけ）。
  どこをいつ進めたか・前回の保存から何が変わったかを知りたいときに読む
  （例: git -c safe.directory=* --git-dir=""<履歴>"" log --stat -5 、git -c safe.directory=* --git-dir=""<履歴>"" log -p -3）。
  ネットワーク上の場所（\\Mac\...）では、-c safe.directory=* を付けないと git に断られる。
  <履歴> は、話しかけるたびに付いてくる今の状態の「変更の履歴の git」の行にある（話題の文書のもの）。
  「ファイル」の行があってこの行が無ければ、まだ記録が無い。「ファイル」の行が無ければ（ファイルを開いていない・AI に渡さない文書など）、履歴は分からない。
  log -p は件数を絞って読む（-3 など。全部出すと長すぎて切れる）。前回進めたところを踏まえて助言するのに使う。
  この履歴にコミットしたり、書き換えたりしない（記録はエディタがする）。
- AI の欄には、ほかの会話のタブもある。タブの見出しは「#1  題」の形で、頭の #1・#2… がそれぞれの claude のセッションの名前（宛先）。
  後ろの題は、Tsuzuri がその会話の最初の頼みから付けて見出しに出しているだけで、セッションの名前ではない。
  利用者が「#2 に〜を渡して」「#1 のスレッドに頼んで」と言ったら、その名前（#2・#1）のセッションに SendMessage で送る（相手は ListAgents で探せる）。
  利用者が題で指したとき（「ログイン修正の会話に」など）は、どの #番号のことかを利用者に尋ねる（題はこちらからは見えない）。
- 読む・書く・直す・まとめるの相手は、断りが無ければ見ているページ（話題のページ。サブページ）だけ。ファイル全体ではない。
  - 見ているページの範囲は、今の状態の「見ているページ」に書いた行（中身はファイルの何行目から何行目まで）。
    カーソルを固定しているときは、固定したページの範囲（「エディタの画面（参考）」の「画面のページ」ではない）。
    「このページを読んで」「要約して」「直して」「ここに書いて」は、この範囲の話。読むときもこの範囲を読む（Read の offset・limit で）。
  - 今の状態の行番号は、話しかけたときのもの。自分で書き換えたあとはずれるので、`<!--page: ID-->` の行を Grep で探し直して範囲を決める（コードブロックの中の見本の行は区切りではない）。
  - ファイル全体・ほかのページ・全部のページを相手にするのは、利用者がそう言ったとき（「この文書」「ファイル全体を見て」「全部のページで」「p5 を」など）だけ。
    ただし、印・リンク・ID を調べるとき（ID が重ならないか、消すページへのリンクがどこにあるか）は、ファイル全体を Grep で見る。
  - 書くのも範囲の中。範囲の外は別のページで、とくにファイルの末尾に足すと最後のページの中身になる。
    ファイルの末尾に足すのは、新しいサブページを作るときだけ。書いたら、印の行を探し直し、ページの範囲の中に入ったかを読み直して確かめる。
  - 同じ行がほかのページにもあることがある。Edit が一つに決まらないときは、前後の行を足して一つに決める。
    replace_all は、利用者がファイル全体を頼んだときだけ使う。
" + Tsuzuri.Office.ExcelInstructions.ForTsuzuri() + Tsuzuri.Office.OutlookInstructions.ForTsuzuri() + @"- AI の欄の下に、利用者と一緒に使う「共通ターミナル」（PowerShell。利用者がそこから ssh で機器やサーバーにつないでいることもある）を出していることがある。
  利用者が「共通ターミナル」と言ったら、これを指す（自分の Bash や、上の段の会話のタブではない）。
  出していれば、話しかけるたびに今の状態に「共通ターミナル」として、画面の最後の 20 行と選んでいる文字が付いてくる。
  - 画面は話しかけたときのもの。足りないとき・そのあとの出力を見たいときは、覚えている全部（1 万行まで）をファイルに書き出させて読む:
      powershell -NoProfile -ExecutionPolicy Bypass -File ""{terminal}"" -Read
    書き出したファイルのパスが返る。長いので、Grep か Read の offset・limit で要るところだけ読む。
  - 出していなければ、自分で出せる（利用者に頼まなくてよい。キーボードは取らない。PowerShell が動き出すまで待って返る）:
      powershell -NoProfile -ExecutionPolicy Bypass -File ""{terminal}"" -Open
    出しただけでは何も実行しない。出したら、出したことを返事で一言伝える。
  - 共通ターミナルに文字を置ける。Enter は押さない（押すのは利用者）:
      powershell -NoProfile -ExecutionPolicy Bypass -File ""{terminal}"" -Type ""<1行>""
    複数行は、ファイルに書いて -TypeFile ""<ファイル>"" で渡す。中のプログラムが貼り付けの印を受け付けるなら（bash・vim など）まとめて置き、
    受け付けないなら（RTX・PowerShell など。改行がそのまま実行になる）1行目だけを置き、残りは利用者が Enter を押すたびに1行ずつ置く。
    置いたら、何を置いたか・Enter で何が起きるかを返事で一言伝える。入力欄に文字が残っていると断られるので、利用者に空にしてもらう。
  - 利用者が共通ターミナルでつないでいる機器・サーバーへの操作は、Bash で別につながず、共通ターミナルに置いて利用者に Enter を任せる。
  - **作業の中で、共通ターミナルでやるほうがよいと思ったら、提案する**（機器・サーバーに限らない）。たとえば:
    利用者が目で確かめてから実行したい変更（設定の書き換え・削除・再起動・ルーターなどの機器の設定）、
    自分の Bash では答えられない対話（パスワード・sudo・(Y/N) を尋ねる・対話式のツール）、長く動かして利用者も様子を見たいもの、
    利用者がこのあと続けて手で触りそうな接続。
    提案では、なぜ共通ターミナルでやるかを一言添える。利用者が応じたら、出していなければ -Open で出し、要ればつなぐコマンドから順に置く
    （つなぐ・実行するの Enter も利用者）。応じなければ、ふだんどおり自分の Bash でできる範囲でやるか、案として書く。
  - 画面の中のパスワードなどの値は *** に伏せて渡している。伏せた値を推し量ったり、利用者に尋ねたりしない。
- SSH でサーバーにつなぐときの接続先と鍵は「{ssh-dir}」にまとめて置いてある。
  利用者が置く元は「{ssh-source}」で、エディタが会話を始めるたびに、本人だけが読める権限にしてここへ写している
  （元は Mac の共有フォルダのことがあり、そこの鍵は Windows の ssh に断られるため）。使うのはここのもの。元のフォルダは使わず、どちらも書き換えない。
  ここに無ければ、利用者がまだ置いていないか、置いたあとに会話を始め直していない。
  - 接続先はそのフォルダの config.txt に1行1つ。| で区切った欄の形は auth の値で3通り:
    - auth=publickey（鍵）: 名称 | ホスト:ポート | auth=publickey | user=ユーザー名 | keyfile=キーファイル名（鍵はそのフォルダの中）
    - auth=password（パスワード）: 名称 | ホスト:ポート | auth=password | user=ユーザー名 | passwd=パスワード
    - auth=via（直接は入れず、別の接続先を通って入る。仮想マシンなど）: 名称 | ホスト:ポート | auth=via | via=通る接続先の名称 | user=ユーザー名
    利用者が名称で言ったら（「〇〇につないで」）、config.txt を読んでその行を探す。ポートが書いていなければ 22。
  - ssh は Windows のもの（""{ssh-exe}""）を使う（Git の ssh ではない）。共通に付ける: -p <ポート> -o UserKnownHostsFile=""<そのフォルダ>\known_hosts""
    -o StrictHostKeyChecking=accept-new <ユーザー名>@<ホスト> ""コマンド""
  - publickey の行: -i ""<そのフォルダ>\<キーファイル名>"" を足す。
  - password の行: パスワードを打つ画面には答えられないので、askpass に答えさせる。Bash で1行に、そのコマンドだけに
    TSUZURI_SSH_HOST=""<名称>"" SSH_ASKPASS=""{ssh-askpass}"" SSH_ASKPASS_REQUIRE=force を前に付け、
    -o PubkeyAuthentication=no -o PreferredAuthentications=password,keyboard-interactive -o NumberOfPasswordPrompts=1 を足す
    （askpass は、その名称の行の passwd を config.txt から読んで ssh に返す。エディタが置いている）。
    パスワードの値は、コマンド・環境変数・引数のどこにも書かない（書くと欄の画面と会話の記録に残る）。
    それでも入れなければ（Windows の ssh が古く、askpass が効かないなど）、何度も試さずに利用者に伝える。
  - via の行: via= の名称の行（publickey か password）のとおりにその接続先へつなぎ、そのコマンドとして中で ssh を動かす。
    中の ssh は、通る接続先に置いてある鍵で入る（-i・-J・パスワードは付けない）:
      <via の行への ssh> ""ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -p <ポート> <ユーザー名>@<ホスト> '<コマンド>'""
    via の行が password なら、TSUZURI_SSH_HOST は via の名称にする。引用符が重なって書きにくいコマンドは、台本を標準入力で渡す
    （… ""ssh … <ユーザー名>@<ホスト> bash -s"" < 台本のファイル）。中の ssh で断られたら（Permission denied など）、何度も試さずに利用者に伝える。
  - 鍵・パスワード・config.txt の中身を、文書・コミット・返事に書き写さない。鍵をほかの場所へ写さない。
  - それでも鍵の権限で断られたら（UNPROTECTED PRIVATE KEY FILE）、権限を変えたり鍵を写したりせず、利用者に伝える。
  - サーバーの中を変える操作（書き換え・削除・再起動・設定の変更）は、する前に何をするかを利用者に伝えて確かめる。
  - ヤマハの RTX（ルーター）や、そのほかのネットワーク機器は、ふつうのサーバーと同じには動かない（ssh の後ろにコマンドを付ける形を断る
    （exec request failed）・プロンプトが出る前に送った文字を捨てる・入力が終わると結果の途中で切る）。Bash では動いている ssh に1行ずつ打てないので、
    ssh を直接使わず、エディタが置いた台本を使う（sleep を挟んでまとめて流す形は、取りこぼすので使わない）:
      powershell -NoProfile -ExecutionPolicy Bypass -File ""{ssh-session}"" -Name ""<名称>"" -Command ""show status""
    複数のコマンドは、1行に1つ書いたファイルを -CommandFile ""<ファイル>"" で渡す（-Command に改行で並べてもよい）。
    台本は config.txt の行から接続先・鍵・パスワードを読み、プロンプトを待って1行ずつ送り、最後に exit で抜ける（auth=via の機器も、通る接続先を経て入る）。
    最初に console lines infinity を送る（ページごとに止めない）。結果のパスワード・事前共有鍵などの行は *** に伏せて出す。
    文字の符号は既定で Shift_JIS（RTX の既定）。化けるなら -Encoding utf-8 を試す。応えが遅い機器は -Timeout 秒 を延ばす。
    - 機器がパスワードや (Y/N) を尋ねると、台本は答えずに止まる（administrator に入る・設定を保存して抜けるなど）。そうなったら利用者に伝える。
    - **自分で送ってよいのは読むだけのもの（show で始まるもの）だけ。** 設定を変えるコマンド（ip・nat・ipsec・tunnel・pp・save・restart・administrator など）は、
      利用者に頼まれても確かめても、台本でも Bash でも送らない。共通ターミナルでやることを提案し（上の「提案する」）、置いて（-Type）、Enter を押すかは利用者に任せる。
      応じなければ案として返事か文書に書く
      （ルーターの設定を誤ると利用者の接続ごと切れ、現地に行かないと戻せないことがあるため）。置くときは、何をするコマンドかを先に返事に書く。
    - 伏せる前の結果を別のやり方で取ったときは、パスワード・事前共有鍵（pre-shared-key）・login password の行を、返事・文書には伏せて書く。
      結果をファイルに残したときは、要らなくなったら消す（残すなら、その場所と秘密の行が入っていることを利用者に伝える）。
- Tsuzuri にはカレンダーがある（本文の左下に、小さな角の丸い板が浮いていて、その中にアイコンだけのつまみ（ボタン）が2つ縦に並んでいる。字は無く、上がカレンダー、下がランチャー。上のつまみを押すとカレンダーを開け閉めする。カレンダーとランチャーは別々に開け閉めし、両方出すと、後から出したほうが手前に重なる（手前のほうを閉じると、奥のほうが見える）。今の状態の「カレンダー」に「ランチャーの奥に隠れていて、利用者には見えていない」とあるときに、カレンダーを見せたいなら、下のつまみ（ランチャー）を押してランチャーを閉じる。そのとき上のつまみ（カレンダー）を押すと、カレンダーは閉じてしまう。Alt+C でもよいが、AI の欄にキーボードがあるときの Alt+C は端末へ渡り、カレンダーは開け閉めしない。出し方を案内するときは、Alt+C より先に左下の上のつまみを言う。このつまみは、文書の見出し・アウトラインの見出しとは別のもの。図エディタを出している間は、カレンダーは出せない（図を保存するか終了してから））。予定を週の行で下へずっと並べ、予定ごとに日・名前を持つ。
  時間の枠は無く、時間は名前に書く（「10:00 打合せ」）。何日かにまたがる予定と、繰り返しの予定がある。
  予定の保存先は「{schedule-file}」（Markdown。利用者はこのファイルを開かず、カレンダーの画面で直す）。予定・日付・空いている日の話になったら、このファイルを読む。
  - 形: 予定1つが見出し1つ。`## 2026-11-21 10:00 打合せ`、何日かは `## 2026-11-21〜2026-11-25 出張`（終わりの日を含む）。見出しの下の行から次の `## ` の手前までがメモ（画面ではメモを書かない。長い中身は文書の見出しと結ぶのが利用者のやり方なので、メモは足さない）。
    アウトラインの見出しから登録した予定は、見出しのすぐ下に `<!-- link: 文書のファイル | ページ | 見出し -->` の行がある（文書のその見出しと結んでいる。中身はその節にある）。
    繰り返しの予定は、見出しのすぐ下に `<!-- repeat: RRULE -->` の行がある（Google カレンダーと同じ RRULE。見出しの日が1回目、見出しの日の幅が1回の長さ）。
    Tsuzuri が読める RRULE は FREQ=DAILY / WEEKLY;BYDAY=MO,TU… / MONTHLY;BYMONTHDAY=25（-1 は末日。-2 などほかの負の数は読めない）/ MONTHLY;BYDAY=2TU（-1FR は最終金曜）/ YEARLY と、INTERVAL=n・UNTIL=yyyyMMdd（その日を含む）だけ。
    COUNT などほかの項目を書くと、カレンダーは1回目しか出さない。
    通知のある予定は、見出しのすぐ下に `<!-- notify: 10:00 -->` の行がある（鳴るのは予定のある日のその時刻。繰り返しなら毎回、何日かなら始まりの日）。
    Tsuzuri が動いている間は、その時刻に Windows の通知を出す（カレンダーを閉じていても出す。Tsuzuri を閉じていた間の分は出さない）。
    利用者がカレンダーの画面の「Google へ送る」を押すと、今日以降（前に送った日がそれより前なら、その日以降）に掛かる予定が全部、Google カレンダーの「Tsuzuri」カレンダーへ送られ、そのカレンダーの同じ範囲の予定と入れ替わる。
    そのうち通知のある予定にだけ、Google からメールが届く（届くのは、上の Windows の通知と同じ日の同じ時刻）。
    Google へ送るのは、利用者がそのボタンを押したときだけ。Claude は送らない（google.json・google-token.dat・google-calendar.json を読まない。Google の API を呼ばない。ボタンも押さない）。
    送ってと頼まれたら、Claude からは送らないことと、「Google へ送る」を押せば送れることを伝える。保存先を書き換えても Google は変わらない。
    送ったあとで予定を直した・消した・足したときは、次に押すまで Google では前のまま。前に送ったときに通知があった予定は、
    消しても、通知を消しても、前の日の前の時刻にメールが届く。日・通知の時刻・繰り返しを変えた予定や、1回だけ消した・動かした回も、前の日の前の時刻に届く（新しい日時には届かない）。
    足した予定と通知を足した予定には、まだ届かない。
    予定を書き換えたことを伝えるとき、次のどちらかなら、「Google へ送る」を押せば Google にも移ることを一言添える（押すかは利用者が決める）:
    利用者がこの会話の中で Google にも移したいと言っていた／通知のある予定を消した、日・通知の時刻・繰り返しを変えた、1回だけ消した・動かした、または通知を消した（押すまで前のとおりにメールが届くため）。
    消した回は `<!-- skip: 2026-12-29 2027-01-05 -->`、
    その回だけ動かした回は `<!-- move: 2026-12-08>2026-12-09 -->`（元の日>動かした先）。これらの行は見出しのすぐ下（メモより前）に置く。
    繰り返しの予定の日を尋ねられたら、決まりから数えて答える（数えるのは聞かれた範囲だけ。無期限の決まりを全部数えない）。
    メモの中で `## ` で始まる行は、頭に `\` を付けて書く。先頭の印の行と `# 予定` は残す。ファイルが無ければ、まだ予定が無い。
  - カレンダーを開いていれば、話しかけるたびに今の状態に「カレンダー」として、カレンダーで出している週・選んでいる日・選んでいる予定が付いてくる（ランチャーの奥に隠れていても付き、そのときは隠れていると書いてある）。
    「この日」「この週」「選んだ日」「この予定」は、それを指す。今日の日付は今の状態の先頭の日時。
  - 予定を足す・直す・消すのは、頼まれたときだけ。書き換える前に必ず読み直す。並びは日付順でなくてよい（カレンダーが保存するときに並べ直す）。
    link の行は書き換えない（結んだ予定の名前も変えない。名前を変えたいと言われたら、結びが切れることを伝える）。書くと、開いているカレンダーは読み直す。
- 返事は日本語で、欄は狭いので短めに書く。";

        /// <summary>図の書き方（2026-11-01。SPEC 19章）。exe の隣に置き、図を頼まれたときだけ Claude に読ませる（毎回は渡さない）。</summary>
        private static readonly string AiDrawingFormat = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ai", "drawing-format.md");

        /// <summary>AI の欄から SSH でつなぐときの鍵・設定を置くフォルダ（2026-11-04。SPEC 19章）。exe の隣の ssh。</summary>
        private static readonly string AiSshFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssh");

        /// <summary>
        /// ssh フォルダを写す先（2026-11-04）。exe が Mac の共有フォルダ（\\Mac\...）にあると、そこのファイルには Windows の権限を付けられず
        /// （icacls は成功と言うが「All users have full control」のまま）、Windows の ssh が鍵を使ってくれない。
        /// 会話を始めるたびにここへ写し、フォルダとファイルを本人だけが触れる権限にする。known_hosts はここで育てる（元から写さない・消さない）。
        /// </summary>
        private static readonly string AiSshLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tsuzuri", "ssh");

        /// <summary>Windows の ssh（Git の ssh は .cmd の askpass を動かせず、鍵の権限の見方も違うので、こちらを使わせる）。</summary>
        private static readonly string AiSshExe = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");

        /// <summary>
        /// パスワードでつなぐ接続先のとき、ssh にパスワードを返す askpass（2026-11-04。SPEC 19章）。
        /// Claude のコマンドは画面で答えられないので、ssh が代わりにこれを動かす。中身は隣の ssh-askpass.ps1 で、
        /// 環境変数 TSUZURI_SSH_HOST の名称の行を config.txt（写した先）から探し、その passwd を返す（2026-11-08。前は Claude が
        /// パスワードの値を環境変数 TSUZURI_SSH_PASS に入れて渡していたので、値がコマンドとして欄の画面と会話の記録に残った）。
        /// </summary>
        private const string AiSshAskpass =
            "@powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0ssh-askpass.ps1\"\r\n";

        /// <summary>exe の隣の Ai から AI のフォルダへ写す台本（2026-11-08。SPEC 19章）。askpass と、ネットワーク機器への入り方（ssh-session.ps1）。</summary>
        private static readonly string[] AiScripts = { "ssh-askpass.ps1", "ssh-session.ps1", "terminal.ps1", "excel.ps1", "outlook.ps1" };

        /// <summary>ツールバーの「AI」。AI の欄を出し入れする。タブが1つも無ければ、claude を起動したタブを1つ開く。</summary>
        private void ToggleAi()
        {
            if (AiPane.Visibility == Visibility.Visible)
            {
                HideAiPane();
                return;
            }

            if (_aiTabs.Count == 0 && !_aiRestorePending)
            {
                if (!CanStartAi()) return;
                AddAiTab(string.Empty);   // claude を直接起動する（2026-10-31。前は PowerShell だけ）
            }
            ShowAiPane();
        }

        /// <summary>
        /// 新しい会話を始めてよいか。ファイル（WordPress の記事も）を開いていれば、そのまま始める。
        /// 何も開いていない画面なら、Windows のファイルを作る画面で .md を作って開き（AskNewDocument。ツールバーの「新規作成」と同じ作り方）、その文書のフォルダで始める（2026-11-01）。
        /// 作るのをやめたら始めない。何も開かずに claude を起動すると利用者のフォルダで動き、そこを信頼するか尋ねられる・作業の範囲が広すぎる・
        /// あとで開いた文書のフォルダの外になる、ため（Claude に作らせてから開く案は、AI の動きに頼るのでやめた）。
        /// 前の起動の会話を戻すときは、その会話を始めたフォルダで起動するので構わない。
        /// </summary>
        private bool CanStartAi()
        {
            if (HasDocument) return true;
            var path = AskNewDocument("AI と使う Markdown を作る");
            if (path == null) return false;
            OpenRoot(path);
            return HasDocument;
        }

        /// <summary>起動したあと、前の会話のタブをまだ戻していないか（窓が出て落ち着いてから戻す。MainWindow のコンストラクタ）。</summary>
        private bool _aiRestorePending;

        /// <summary>
        /// 会話のタブを足して選ぶ。"" なら新しい会話（claude をそのまま起動する。最初の1回だけ。終わったあとの端末は PowerShell）。
        /// null なら PowerShell だけ（今は使わない。2026-10-31 に「＋」のメニューをやめ、claude を直接起動するようにした）。
        /// </summary>
        private void AddAiTab(string start)
        {
            AddAiTab(start, null);
        }

        // ランチャー・ファイラーのスクリプト（.bat・.cmd・.ps1）をこの欄のタブで動かすのは、やめた（2026-10-30。2026-10-27 に入れたが、
        // 使ってみてほぼ意味がなかった）。前のとおり別の窓で動かす（MainWindow.Links.cs の Launch）。

        /// <param name="restore">null でなければ、前の起動で開いていた会話。そのフォルダで、その会話の続きとして claude を起動する。</param>
        private void AddAiTab(string start, AiTabState restore)
        {
            var tab = new AiTab();
            var first = true;

            if (restore != null)
            {
                tab.SessionId = restore.Session;
                tab.Folder = restore.Folder;
                tab.Project = string.IsNullOrWhiteSpace(restore.Project) ? null : restore.Project;
                tab.Topic = string.IsNullOrWhiteSpace(restore.Topic) ? null : restore.Topic;
                tab.Pin = restore.Pin != null && !string.IsNullOrEmpty(restore.Pin.File) && restore.Pin.Line > 0 ? restore.Pin : null;
                var name = AiTabName(restore.Name);
                if (!AiTabNameUsed(name)) tab.Name = name;
                start = "--resume " + restore.Session;   // 番号は Guid の形のものだけ（AppSettings）
            }
            if (tab.Name == null) tab.Name = NewAiTabName();

            // ファイルを開いていなければ、利用者のフォルダで起動する。
            // WordPress の記事なら、記事ごとの一時フォルダではなく、その上の決まったフォルダで起動する（2026-10-31）。
            // claude は初めてのフォルダで「信頼するか」を尋ねるので、記事を開くたびに尋ねられていた。記事のファイルはその下にあるので書き換えられる。
            tab.View = new TerminalView(
                () =>
                {
                    var command = AiCommandLine(first ? start : null, tab.Id, tab.Name);
                    first = false;
                    return command;
                },
                () => AiStartFolder(restore));
            tab.View.Visibility = Visibility.Collapsed;
            AiTerminals.Children.Add(tab.View);

            // 見出しはセッションの名前と題（2026-11-01 にセッションの名前にし、2026-11-22 に題を足した。端末のタイトルはツールチップに出す）。
            // 見出しは「#1 題」（2026-11-22。題が付けば後ろに出す。宛先の名前は #1 のまま。AiTopic.cs の AiTabHeaderText）
            tab.Title = new TextBlock { Text = AiTabHeaderText(tab), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            tab.Header = CreateAiTabHeader(tab);
            tab.View.TitleChanged += title => SetAiTabTitle(tab, title);
            tab.View.FocusRequested += delegate
            {
                // 押したタブが今のタブで、欄が出ていれば渡し直す（タブを選んだときと同じ手順。日本語の変換の位置がずれないように）。
                if (ReferenceEquals(_aiTab, tab) && AiPane.Visibility == Visibility.Visible) FocusAiTerminal(tab);
            };
            _aiTabs.Add(tab);
            RebuildAiTabStrip();
            SelectAiTab(tab);
        }

        /// <summary>
        /// 会話を始めるフォルダ。前の会話を戻すならそのフォルダ、ファイルを開いていればそのフォルダ、無ければ利用者のフォルダ。
        /// AI に渡さない場所（AiGuard）では始めず、利用者のフォルダで始める（2026-11-09。Claude Code は作業のフォルダを自由に読むため）。
        /// </summary>
        private string AiStartFolder(AiTabState restore)
        {
            var folder = restore != null && !string.IsNullOrEmpty(restore.Folder) && Directory.Exists(restore.Folder)
                ? restore.Folder
                : HasDocument
                    ? (WordPressLinkOf(Active) != null ? WordPressTempRoot : Path.GetDirectoryName(Active.FilePath))
                    : null;

            if (folder != null && AiGuard.IsGuarded(folder))
            {
                Dispatcher.BeginInvoke(new Action(() => Status("AI に渡さないフォルダ（" + AiGuard.MarkerName + "）なので、会話は利用者のフォルダで始めました。")));
                folder = null;
            }

            return folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>タブの見出し。押すと選ぶ。右の × で閉じる（その会話の PowerShell と claude も終わる）。</summary>
        private Border CreateAiTabHeader(AiTab tab)
        {
            // × と ＋ は、検索欄の ↑↓ と同じアイコンのフォントの記号にする（本文のフォントの × は小さく見えた）。
            var close = new Button
            {
                Content = "\uE711",
                ToolTip = "この会話を閉じる（中の PowerShell と Claude Code も終わる）",
                FontFamily = IconFont,
                FontSize = 9,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
            if (style != null) close.Style = style;
            close.Click += delegate { CloseAiTab(tab); };

            // 名前は「#1」のように短く、押せる所が狭かったので、タブに最小の幅を持たせ、× を右端に寄せて、残りを全部押せる所にする（2026-11-07）。
            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);
            tab.Title.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(tab.Title);

            // 高さは検索欄の段のボタン（26px）にそろえる。
            var header = new Border
            {
                Child = panel,
                Height = 26,
                MinWidth = 120,
                MaxWidth = 320,   // 題を出すので広げた（2026-11-22。前は 96〜220）
                Background = Brushes.Transparent,   // 文字の無い所も押せるように
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.MouseLeftButtonUp += (sender, e) =>
            {
                if (IsButtonClick(e.OriginalSource as DependencyObject)) return;   // × は閉じる
                SelectAiTab(tab);
            };
            return header;
        }

        /// <summary>選んでいるタブの端末だけを出し、ほかは隠す（隠しても動き続ける）。選んでいるタブは青。</summary>
        private void ShowAiTabs()
        {
            foreach (var other in _aiTabs)
            {
                var selected = ReferenceEquals(other, _aiTab);
                other.View.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                other.Header.Background = selected ? (Brush)FindResource("AccentFill") : Brushes.Transparent;
                other.Title.Foreground = (Brush)FindResource(selected ? "AccentText" : "TextMuted");
            }
        }

        /// <summary>アイコンのフォント（ファイラー・検索欄の ↑↓ と同じ）。</summary>
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>
        /// 端末のタイトルが変わった。タブのツールチップに、セッションの名前と並べて出す。
        /// </summary>
        private static void SetAiTabTitle(AiTab tab, string title)
        {
            // 見出しは「#1  題」のまま。端末のタイトル（Claude Code が会話の中身に合わせて付ける）はツールチップに出す（2026-11-01）。
            var text = (title ?? string.Empty).Trim();
            tab.TerminalTitle = text;
            tab.Header.ToolTip = AiTabToolTip(tab);
        }

        /// <summary>アプリの中の（全部の画面の）AI の欄のタブで、その名前を使っているか。</summary>
        private static bool AiTabNameUsed(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return OpenWindows.Any(window => window._aiTabs.Any(tab => tab.Name == name));
        }

        /// <summary>覚えていた会話の名前を今の形にする（前の「1」は「#1」に）。</summary>
        private static string AiTabName(string name)
        {
            return name != null && !name.StartsWith("#", StringComparison.Ordinal) ? "#" + name : name;
        }

        /// <summary>新しいタブの名前。使っていないいちばん小さい番号（#1・#2…）。閉じたタブの番号は、次のタブが使う。</summary>
        private static string NewAiTabName()
        {
            var number = 1;
            while (AiTabNameUsed("#" + number.ToString(CultureInfo.InvariantCulture))) number++;
            return "#" + number.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>タブの段を並べ直す（タブ、その右に「＋」）。</summary>
        private void RebuildAiTabStrip()
        {
            if (_aiAddButton == null)
            {
                _aiAddButton = new Button
                {
                    Content = "\uE710",
                    ToolTip = "新しい会話を足す（claude を起動する）",
                    FontFamily = IconFont,
                    FontSize = 11,
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
                if (style != null) _aiAddButton.Style = style;
                _aiAddButton.Click += delegate { if (CanStartAi()) AddAiTab(string.Empty); };   // メニューは出さず、claude を直接起動する（2026-10-31）
            }

            AiTabs.Children.Clear();
            foreach (var tab in _aiTabs) AiTabs.Children.Add(tab.Header);
            AiTabs.Children.Add(_aiAddButton);
            AiTabs.Children.Add(AiShellButton());   // 共通ターミナル（2026-11-08）
        }

        /// <summary>タブを選ぶ。そのタブの端末を出し、ほかは隠す（隠しても動き続ける）。</summary>
        private void SelectAiTab(AiTab tab)
        {
            _aiTab = tab;
            ShowAiTabs();
            RefreshAiFileLinks();
            RefreshAiPinRows();
            if (AiPane.Visibility == Visibility.Visible) FocusAiTerminal(tab);
        }

        /// <summary>
        /// 端末にキーボードを渡す前に、いったん受け皿（見えない文字の欄）に移すためのもの（2026-10-28）。
        /// </summary>
        private TextBox _aiFocusSink;

        /// <summary>
        /// タブの端末にキーボードを渡す（欄を出したとき・タブを選んだとき）。
        /// いったん受け皿の文字の欄に移してから渡す（2026-10-28）。端末（WebView2）にじかに移すと、日本語の変換が窓の左上に出ることがあった
        /// （タブを切り替えたとき・起動したときに戻したタブ）。本文などほかの文字の欄を一度経てから戻ると、正しい所に出たので、それをまねる。
        /// </summary>
        private void FocusAiTerminal(AiTab tab)
        {
            Keyboard.Focus(AiFocusSink());
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ReferenceEquals(_aiTab, tab) && AiPane.Visibility == Visibility.Visible) tab.View.FocusTerminal();
            }), DispatcherPriority.Background);
        }

        /// <summary>受け皿の文字の欄。初めて使うときに作る（共通ターミナルも使う。2026-11-08）。</summary>
        private TextBox AiFocusSink()
        {
            if (_aiFocusSink == null)
            {
                _aiFocusSink = new TextBox
                {
                    Width = 1,
                    Height = 1,
                    Opacity = 0,
                    IsTabStop = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                AiTerminals.Children.Insert(0, _aiFocusSink);   // 端末の下（重ねて置いた一番奥）

                // 移している一瞬に打たれた文字は、端末に渡せないので捨てる（受け皿に溜めない）。
                _aiFocusSink.LostKeyboardFocus += delegate { _aiFocusSink.Clear(); };
            }

            return _aiFocusSink;
        }

        /// <summary>タブを閉じる。中の PowerShell と claude も終わらせる。最後の1つを閉じたら欄を畳む。</summary>
        private void CloseAiTab(AiTab tab)
        {
            var index = _aiTabs.IndexOf(tab);
            if (index < 0) return;

            // 中のプログラムを終わらせ、WebView2 も片付ける（2026-10-27。前は終わらせるだけで、WebView2 が残っていた）。
            tab.View.Dispose();
            AiTerminals.Children.Remove(tab.View);
            _aiTabs.RemoveAt(index);
            try
            {
                var tabContext = AiTabContextPath(tab);
                if (tabContext != null) File.Delete(tabContext);
            }
            catch (Exception)
            {
            }
            RebuildAiTabStrip();
            StoreAiTabs();   // 自分で閉じた会話は、次の起動で戻さない

            if (_aiTabs.Count == 0)
            {
                _aiTab = null;
                ShowAiTabs();
                RefreshAiPinRows();
                HideAiPane();
                return;
            }

            if (ReferenceEquals(_aiTab, tab))
            {
                SelectAiTab(_aiTabs[Math.Min(index, _aiTabs.Count - 1)]);
            }
        }

        /// <summary>
        /// claude のフックの知らせ（2026-10-27）。会話が始まった（新しく・続きから・/clear で作り直した）ら、その番号をタブに覚える。
        /// 終わった（/exit など）なら、その会話はもう戻さない。始まりと終わりの知らせは前後しうるので、終わりは同じ番号のときだけ消す。
        /// </summary>
        private void OnAiSession(bool started, string tabId, string session, string folder)
        {
            if (_aiStopping) return;

            var tab = _aiTabs.FirstOrDefault(other => other.Id == tabId);
            if (tab == null || !Guid.TryParse(session, out _)) return;

            if (started)
            {
                tab.SessionId = session;
                if (!string.IsNullOrEmpty(folder)) tab.Folder = folder;
            }
            else if (tab.SessionId == session)
            {
                tab.SessionId = null;
            }
            else
            {
                return;
            }

            StoreAiTabs();
        }

        /// <summary>
        /// claude の会話のあるタブを、並びのまま app.json に書く（次の起動で戻す）。PowerShell だけのタブは書かない。
        /// 落ちても残るように、変わるたびに書く。閉じている最中（claude を終わらせている）は書かない。
        /// </summary>
        private void StoreAiTabs()
        {
            if (_aiStopping) return;

            var tabs = _aiTabs.Where(tab => tab.SessionId != null)
                              .Select(tab => new AiTabState { Session = tab.SessionId, Folder = tab.Folder, Name = tab.Name, Project = tab.Project, Pin = tab.Pin, Topic = tab.Topic })
                              .ToList();
            SavedAiTabs = tabs.Count > 0 ? tabs : null;   // 画面ごとに覚える（2026-10-31）
            _settings.Save();
        }

        /// <summary>
        /// 起動したとき、前に開いていた会話のタブを、それぞれの会話の続き（claude --resume 番号）として開き直す（2026-10-27）。
        /// 欄はまだ出さない（画面を戻すときに出す）。戻したタブがあれば true。
        /// </summary>
        private bool RestoreAiTabs()
        {
            var saved = SavedAiTabs;
            if (saved == null || saved.Count == 0) return false;

            // 話しかける前に閉じた会話は、claude がディスクに残していないので戻さない（2026-10-31）。
            // claude を直接起動するようにしてから、開いただけのタブも会話の番号を覚え、次の起動で「No conversation found」が出た。
            var restorable = saved.Where(state => ClaudeConversationExists(state.Session)).ToList();
            if (restorable.Count < saved.Count)
            {
                SavedAiTabs = restorable.Count > 0 ? restorable : null;
                _settings.Save();
            }

            if (restorable.Count == 0)
            {
                // 画面を戻すときに欄を出していたら、空のままにせず新しい会話を1つ開く。
                if (AiPane.Visibility == Visibility.Visible)
                {
                    if (HasDocument) AddAiTab(string.Empty);
                    else HideAiPane();   // 何も開いていなければ新しい会話は始めない（CanStartAi）
                }
                return false;
            }

            foreach (var state in restorable) AddAiTab(null, state);
            SelectAiTab(_aiTabs[0]);
            return true;
        }

        /// <summary>
        /// claude がその会話を残しているか（%USERPROFILE%\.claude\projects\フォルダの名前\番号.jsonl があるか）。
        /// claude は最初に話しかけるまで会話を書かない。調べられなければ true（今までどおり開き直してみる）。
        /// </summary>
        private static bool ClaudeConversationExists(string session)
        {
            try
            {
                var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

                var projects = Path.Combine(home, "projects");
                if (!Directory.Exists(projects)) return true;

                return Directory.EnumerateFiles(projects, session + ".jsonl", SearchOption.AllDirectories).Any();
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 欄の端末で起動するコマンド。中は普通の PowerShell（2026-10-24。まず端末として確実に動かし、動いてから下を付け直した）。
        /// タブを作ると、今の状態を渡すフック（AiSettings）と指示を付けた Claude Code をすぐ起動する（start）。claude が終わったあとも、
        /// その PowerShell で claude と打てば同じ設定で起動する（PowerShell の関数 claude で、本物の claude.exe を設定付きで呼び直す）。欄の外で起動した claude には付かない。
        /// 設定を書けなければ、関数は置かず、ただの PowerShell にする（claude も設定なしで動く）。
        /// </summary>
        /// <param name="start">PowerShell が起動したら続けて動かす claude の引数（AddAiTab）。null なら何も動かさない。</param>
        /// <param name="tabId">タブの番号（AiTab.Id）。環境変数 MDEDITOR_AI_TAB で claude のフックに渡す。</param>
        private string AiCommandLine(string start, string tabId, string name)
        {
            // Claude Code の入る場所（%USERPROFILE%\.local\bin）を PATH の先頭に足す。中の PowerShell はこのアプリの PATH を受け継ぐが、
            // 入れる前から動いていたアプリ（や、それを起動した Visual Studio）の PATH には入っていず、claude が見つからなかった（2026-10-24）。
            // exe の隣に置いた git（MinGit）も足す。Claude が変更の履歴を読むときは、自分で git を動かすため（2026-10-25）。
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
            foreach (var folder in new[] { DocumentHistory.BundledGitFolder, local })
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (Directory.Exists(folder) && path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", folder + ";" + path);
            }

            var powershell = "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo";

            var claude = FindClaude();
            if (claude == null)
            {
                if (start == null) return powershell;

                // claude を起動するはずが見つからなければ、黙って PowerShell だけにせず知らせる（2026-10-27）。
                Status("claude（Claude Code）が見つかりませんでした。PowerShell だけを起動します。");
                var missing = "Write-Host " + Quote("claude（Claude Code）が見つかりませんでした（" + local + " にも PATH にもありません）。"
                                                   + "入れてから、＋ で会話を開き直してください。") + " -ForegroundColor Yellow";
                return powershell + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(missing));
            }

            string settings, instructions;
            try
            {
                Directory.CreateDirectory(AiFolder);

                MaybeDeleteStaleAiFiles();

                var id = WindowKey;   // 画面ごとに分ける（プロセスの番号-画面の番号）
                _aiContextPath = Path.Combine(AiFolder, "context-" + id + ".md");
                settings = Path.Combine(AiFolder, "settings-" + id + ".json");
                instructions = Path.Combine(AiFolder, "instructions.md");
                _aiSettingsPath = settings;

                var askpass = Path.Combine(AiFolder, "ssh-askpass.cmd");
                WriteIfChanged(askpass, AiSshAskpass);
                foreach (var script in AiScripts)
                {
                    var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ai", script);
                    if (File.Exists(source)) WriteIfChanged(Path.Combine(AiFolder, script), File.ReadAllText(source, Encoding.UTF8));
                }
                CopySshFolder();
                WriteIfChanged(instructions, AiInstructions
                    .Replace("{drawing-format}", AiDrawingFormat)
                    .Replace("{ssh-dir}", AiSshLocal)
                    .Replace("{ssh-source}", AiSshFolder)
                    .Replace("{ssh-exe}", AiSshExe)
                    .Replace("{ssh-askpass}", askpass)
                    .Replace("{ssh-session}", Path.Combine(AiFolder, "ssh-session.ps1"))
                    .Replace("{terminal}", Path.Combine(AiFolder, "terminal.ps1"))
                    .Replace("{excel}", Path.Combine(AiFolder, "excel.ps1"))
                    .Replace("{outlook}", Path.Combine(AiFolder, "outlook.ps1"))
                    .Replace("{schedule-file}", ScheduleFilePath)
                    + ExcelNotesSection() + OutlookNotes.Section());   // 利用者が書いた「Excel で AI にいつも伝えること」「Outlook で AI にいつも伝えること」（Office/OfficeNotes.cs）
                File.WriteAllText(settings, AiSettings(_aiContextPath, AiPipeName, StartAiHttp()), new UTF8Encoding(false));
                WriteAiContext(null);
                Dispatcher.BeginInvoke(new Action(RefreshAiFileLinks));   // 今の状態・フックの設定を書いたので、下端のボタンに出す（2026-11-08）
            }
            catch (Exception ex)
            {
                Status("AI に渡す設定を書けませんでした: " + ex.Message);
                return powershell;
            }

            StartAiListener();

            // 引数は -EncodedCommand にまとめる（引用符や $ を気にしなくて済む）。
            // 起動したときに出していた claude の使い方の1行は外した（2026-10-30。タブが戻るので「続き」の案内は要らず、履歴は claude --resume）。
            // TSUZURI_AI_PIPE は、Claude が共通ターミナルに文字を置く・画面を読む台本（terminal.ps1）が使う（2026-11-08）。
            // TSUZURI_AI_NAME（tab1 など）: excel.ps1 が、この会話の作業ファイル（excel-write-tab1.tsv など）を選ぶのに使う（2026-11-25。MainWindow.Excel.cs の AiExcelFileSuffix）
            // AiLink の環境変数は消す（AiLink のタブから Tsuzuri を起動すると受け継ぎ、同じ excel.ps1 が AiLink のほうへ頼んでしまう。excel.ps1 は AILINK_PIPE を先に見る。2026-11-26）
            var command = "Remove-Item Env:AILINK_PIPE,Env:AILINK_NAME,Env:AILINK_WORK,Env:AILINK_TAB -ErrorAction SilentlyContinue;$env:MDEDITOR_AI_TAB=" + Quote(tabId) + ";$env:TSUZURI_AI_PIPE=" + Quote(AiPipeName) + ";"
                         + (string.IsNullOrEmpty(name) ? string.Empty : "$env:TSUZURI_AI_NAME=" + Quote(AiExcelFileSuffix(name)) + ";")
                         + "function claude { & " + Quote(claude) + " --settings " + Quote(settings)
                         + " --append-system-prompt-file " + Quote(instructions)
                         + (string.IsNullOrEmpty(name) ? string.Empty : " --name " + Quote(name)) + " @args }";
            if (start != null) command += ";claude " + start;
            return powershell + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        /// <summary>
        /// 入れた場所（%USERPROFILE%\.local\bin）か、PATH の通った場所の claude.exe。無ければ null。
        /// （2026-11-06。前は使っていない Views/ChatView にあった。ChatView ごと消してここへ移した）
        /// </summary>
        private static string FindClaude()
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var folder in path.Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim().Trim('"'), "claude.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // PATH に変な項目があっても飛ばす。
                }
            }

            return null;
        }

        private void ShowAiPane()
        {
            AiPane.Visibility = Visibility.Visible;
            AiSplitter.Visibility = Visibility.Visible;
            AiSplitterColumn.Width = new GridLength(4);
            AiColumn.Width = new GridLength(_settings.AiWidth);

            if (_aiTab != null) FocusAiTerminal(_aiTab);
        }

        private void HideAiPane()
        {
            if (AiPane.Visibility == Visibility.Visible) StoreAiWidth();

            AiPane.Visibility = Visibility.Collapsed;
            AiSplitter.Visibility = Visibility.Collapsed;
            AiSplitterColumn.Width = new GridLength(0);
            AiColumn.Width = new GridLength(0);
        }

        /// <summary>AI の欄の幅を覚える。仕切りを動かしたときと、欄を畳むとき。</summary>
        private void StoreAiWidth()
        {
            if (AiColumn.ActualWidth < 200) return;

            _settings.AiWidth = AiColumn.ActualWidth;
            _settings.Save();
        }

        private void AiSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            StoreAiWidth();
        }

        /// <summary>仕切りをダブルクリックしたら、エディタと AI の欄を同じ幅にする（2026-11-11）。</summary>
        private void AiSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            var half = Math.Floor((EditorColumn.ActualWidth + AiColumn.ActualWidth) / 2);
            if (half < 200) return;

            AiColumn.Width = new GridLength(half);
            UpdateLayout();
            StoreAiWidth();
        }

        /// <summary>
        /// エディタを閉じるとき。claude も終わらせる（残すと窓の無いまま動き続ける）。
        /// WebView2 も片付け、このエディタの設定・状態のファイル（settings-番号.json・context-番号.md）も消す（2026-10-27。前は溜まっていった）。
        /// </summary>
        private void StopAi()
        {
            _aiStopping = true;
            StopAiHttp();

            // つながりを待っている口を閉じて、待つスレッドを終わらせる。
            var waiting = _aiWaiting;
            if (waiting != null)
            {
                try
                {
                    waiting.Dispose();
                }
                catch (Exception)
                {
                }
            }

            foreach (var tab in _aiTabs) tab.View.Dispose();
            CloseAiShell();

            var tabContexts = _aiTabs.Select(AiTabContextPath).Where(path => path != null).ToList();
            foreach (var path in new[] { _aiSettingsPath, _aiContextPath, AiShellDumpPath }.Concat(tabContexts))
            {
                if (path == null) continue;

                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // 消せなくても閉じるのは止めない。次に起動したとき DeleteStaleAiFiles が片付ける。
                }
            }

            DeleteExcelSheets();
            _aiContextPath = null;
            _aiSettingsPath = null;
        }

        /// <summary>古い結果のファイルの片付けを、1時間に1回まで回す（起動して初めの会話のときだけだと、何日も開いたままでたまるため。2026-11-26 のレビュー）。</summary>
        private static void MaybeDeleteStaleAiFiles()
        {
            lock (StaleFilesLock)
            {
                if ((DateTime.UtcNow - _staleFilesCleanedAt).TotalHours < 1) return;
                _staleFilesCleanedAt = DateTime.UtcNow;
            }
            DeleteStaleAiFiles();
        }

        private static readonly object StaleFilesLock = new object();
        private static DateTime _staleFilesCleanedAt = DateTime.MinValue;

        /// <summary>
        /// 前に落ちたエディタ（閉じる処理を通らなかったもの）が残した settings-番号.json・context-番号.md を消す（2026-10-27）。
        /// 番号のプロセスがもう動いていないか、別のプログラムに番号が使い回されていれば消す。同じ名前のプロセスが動いていれば念のため残す
        /// （アプリは1つしか動かない（App）ので、ふつうは落ちたエディタの残りだけ）。できる範囲で。
        /// </summary>
        private static void DeleteStaleAiFiles()
        {
            try
            {
                Tsuzuri.Office.OutlookLink.DeleteOld(AiFolder);   // Outlook の結果と、消せずに残った下書きの作業ファイル（2026-11-26）
                var self = Process.GetCurrentProcess();

                // 頼みごとの名前で書いた Excel の結果・台本は、半日より古いものを消す（どの起動のものかは名前から分からないので、古さで見る。2026-11-25）
                foreach (var pattern in new[] { "excel-read-*.tsv", "excel-overview-*.txt", "excel-overview-*.png", "excel-script-*.ps1", "excel-write-*.tsv",
                                                "excel-write.tsv", "excel-script.ps1" })   // 終わりの2つは、会話ごとの名前にする前の名前（2026-11-25 まで）
                {
                    foreach (var old in Directory.GetFiles(AiFolder, pattern))
                    {
                        try
                        {
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(12)) File.Delete(old);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }

                foreach (var pattern in new[] { "settings-*.json", "context-*.md", "terminal-*.txt", "sheets-*.txt" })
                {
                    foreach (var path in Directory.GetFiles(AiFolder, pattern))
                    {
                        // 名前は settings-プロセスの番号-画面の番号（2026-10-31。前は画面の番号が無かった）。プロセスの番号で見る。
                        var name = Path.GetFileNameWithoutExtension(path);
                        var number = name.Substring(name.IndexOf('-') + 1);
                        if (number.IndexOf('-') >= 0) number = number.Substring(0, number.IndexOf('-'));
                        int id;
                        if (!int.TryParse(number, out id) || id == self.Id) continue;
                        if (IsEditorRunning(id, self.ProcessName)) continue;

                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 片付けられなくても claude の起動は止めない。
            }
        }

        /// <summary>その番号でこのエディタ（同じ名前のプロセス）が動いているか。番号が別のプログラムに使い回されていれば false。</summary>
        private static bool IsEditorRunning(int id, string name)
        {
            try
            {
                using (var process = Process.GetProcessById(id))
                {
                    return string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;   // 動いていない
            }
        }

        /// <summary>PowerShell の文字列（'...'）にする。中の ' は2つ重ねる。</summary>
        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        /// <summary>
        /// Claude Code のフック。どれも PowerShell で動かす（フックは Git Bash で動くこともあるので、
        /// 引用符や $ を含まない -EncodedCommand にして、どちらのシェルでも同じに通るようにする）。
        /// どれも、このエディタのパイプにつないで1行送る。
        /// ・書き換えたあと（PostToolUse）: 「changed→書いたファイル→フォルダ」（タブ区切り）。エディタが Claude の書いた図の画像を作り（RenderAiDrawings）、
        ///   外で変わった文書を読み直す。答えは「ok」か、その呼び出しで書いた図を作れなかった知らせ
        /// ・会話が始まった・終わったとき（SessionStart・SessionEnd）: SessionHook
        /// ・話しかけたとき（UserPromptSubmit）: 「prompt」。エディタが保存して今の状態を書き出し、「ok」を返す。
        ///   そのあとで今の状態のファイルを出力する。出力は Claude への文脈になる。
        ///   つながらなくても、状態のファイルが残っていれば前に書いた状態をそのまま出す（エディタを閉じるとファイルは消える（StopAi）ので、そのときは何も出さない）
        /// </summary>
        private static string AiSettings(string contextPath, string pipeName, string http)
        {
            var connect =
                "$c=New-Object System.IO.Pipes.NamedPipeClientStream('.','" + pipeName + "',[System.IO.Pipes.PipeDirection]::InOut);";

            // つなぐのを待つのは5秒（2026-10-27。前は1秒で、ほかのタブの頼みに応えている間の「changed」が落ちることがあった）。
            // エディタはつながりごとに次の口を開き直すので、普通はすぐつながる。エディタが閉じると中の claude も終わる（ジョブ）ので、
            // パイプが無くて5秒待つことはまず無い。
            // エディタは、Claude が書いた図の .png を作ってから答える（2026-11-01）。作れなかった図があれば、その知らせを
            // 標準エラーに出して 2 で終わる（Claude Code は、それをツールの結果のあとに Claude に見せる。Claude が .json を直せるように）。
            // 書いたファイル（tool_input.file_path）も送る（2026-11-02）。知らせるのは、その呼び出しで書いた図のことだけにするため
            // （前は開いている文書の図を全部見て、作れなかった図をどの会話にでも知らせていた）。中身は標準入力に来るので、全部読んでから送る。
            var notify =
                "$p='';$d='';try{$s=New-Object System.IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($s);" +
                "$j=[System.Text.Encoding]::UTF8.GetString($s.ToArray())|ConvertFrom-Json;" +
                "if($j.tool_input.file_path){$p=[string]$j.tool_input.file_path};if($j.cwd){$d=[string]$j.cwd}}catch{};" +
                connect +
                "try{$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);$w.WriteLine('changed'+[char]9+$p+[char]9+$d);$w.Flush();" +
                "$r=New-Object System.IO.StreamReader($c,[System.Text.Encoding]::UTF8);$m=$r.ReadLine()}catch{};$c.Dispose();" +
                "if($m -and $m -ne 'ok'){[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;[Console]::Error.Write($m);exit 2}";

            // 読み書きの前（PreToolUse。2026-11-09）。AI に渡さない場所（AiGuard）を断る。「guard→ツール→フォルダ→項目の名前→値…」を Base64 にしてタブ区切りで送る。
            // 答えが「ok」でなければ、それを理由として標準エラーに出して 2 で終わる（Claude Code はツールを動かさず、理由を Claude に見せる）。
            // エディタにつながらなければ止めない（エディタを閉じると中の claude も終わる）。
            // 送るのは場所の入りうる項目（AiGuard.TextKeys）だけ（2026-11-15。前は Write の中身まで全部 Base64 にして送っていた）。待つのは 10 秒まで（越えたら Claude Code が通す）。
            var guard =
                "$q='';try{$s=New-Object System.IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($s);" +
                "$j=[System.Text.Encoding]::UTF8.GetString($s.ToArray())|ConvertFrom-Json;" +
                "$e={param($t)[Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes([string]$t))};" +
                "$q='guard'+[char]9+(&$e $j.tool_name)+[char]9+(&$e $j.cwd);" +
                "$k=@(" + string.Join(",", AiGuard.TextKeys.Select(key => "'" + key + "'").ToArray()) + ");" +
                "if($j.tool_input){foreach($i in $j.tool_input.PSObject.Properties){if($k -contains $i.Name){$q+=[char]9+(&$e $i.Name)+[char]9+(&$e $i.Value)}}}}catch{};" +
                "if(-not $q){exit 0};" +
                connect +
                "try{$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);$w.WriteLine($q);$w.Flush();" +
                "$r=New-Object System.IO.StreamReader($c,[System.Text.Encoding]::UTF8);$m=$r.ReadLine()}catch{};$c.Dispose();" +
                "if($m -and $m -ne 'ok'){[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;[Console]::Error.Write($m);exit 2}";

            // 状態のファイルはタブごとにも書く（2026-11-22。「context-画面-タブ.md」。固定したカーソル・フォルダはタブごとに違うので、
            // 画面に1つのファイルだけだと、ほかのタブが同時に話しかけたときに、そのタブの状態を読んでしまう）。タブの分が無ければ画面の分を読む
            // どのファイルを読むかは、エディタが答えたあとで決める（タブの最初の頼みでは、答えるまでタブの分がまだ無い）。
            // エディタが「ok」（今回の状態を書けた）と答えなければ、前の状態に断り（AiStaleNote）を付けて出す
            var pick = "$f=" + Quote(contextPath) + ";if($env:MDEDITOR_AI_TAB){$g=" + Quote(contextPath.Substring(0, contextPath.Length - 3) + "-")
                       + "+[string]$env:MDEDITOR_AI_TAB+'.md';if(Test-Path -LiteralPath $g){$f=$g}};";
            var context = "$m='';" + connect +
                "try{$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);$w.WriteLine('prompt'+[char]9+[string]$env:MDEDITOR_AI_TAB);$w.Flush();" +
                "$r=New-Object System.IO.StreamReader($c);$m=$r.ReadLine()}catch{};$c.Dispose();" +
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" + pick +
                "if(Test-Path -LiteralPath $f){if($m -ne 'ok'){[Console]::Out.Write(" + Quote(AiStaleNote) + ")};[Console]::Out.Write([System.IO.File]::ReadAllText($f))}";

            // HTTP の口を開けていれば（2026-11-15。MainWindow.AiHttp.cs）、ツールのたび・話しかけるたびのフックは HTTP で受ける。
            // PowerShell の起動（1 回 0.3〜1 秒）が無くなる。開けなければ今までどおり PowerShell。会話の始まり・終わりは HTTP にしない（始まりは HTTP を使えない。どちらも1回だけ）。
            var pre = http != null
                ? "{\"type\":\"http\",\"url\":\"" + http + "pre\",\"timeout\":10}"
                : "{\"type\":\"command\",\"timeout\":10,\"command\":\"" + PowerShell(guard) + "\"}";
            var post = http != null
                ? "{\"type\":\"http\",\"url\":\"" + http + "post\",\"timeout\":30}"
                : "{\"type\":\"command\",\"command\":\"" + PowerShell(notify) + "\"}";
            var prompt = http != null
                ? "{\"type\":\"http\",\"url\":\"" + http + "prompt\",\"timeout\":300}"
                : "{\"type\":\"command\",\"timeout\":300,\"command\":\"" + PowerShell(context) + "\"}";

            return "{\"hooks\":{" +
                   "\"PreToolUse\":[{\"matcher\":\"Read|Edit|Write|MultiEdit|NotebookEdit|Grep|Glob|Bash|PowerShell\",\"hooks\":[" + pre + "]}]," +
                   "\"PostToolUse\":[{\"matcher\":\"Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell\",\"hooks\":[" + post + "]}]," +
                   "\"UserPromptSubmit\":[{\"hooks\":[" + prompt + "]}]," +
                   "\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"" + PowerShell(SessionHook(connect, "session")) + "\"}]}]," +
                   "\"SessionEnd\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"" + PowerShell(SessionHook(connect, "ended")) + "\"}]}]" +
                   "}}";
        }

        /// <summary>
        /// 会話が始まった・終わったときのフック（2026-10-27）。次の起動で会話のタブを戻すため、
        /// 「session（ended）→タブの番号→会話の番号→フォルダ」をタブ区切りの1行で送る。
        /// 中身（JSON）は標準入力に来る。フォルダの名前に日本語があっても化けないよう、バイトのまま UTF-8 で読む。
        /// タブの番号（MDEDITOR_AI_TAB）が無ければ（欄の外で起動した claude）、何も送らない。
        /// </summary>
        private static string SessionHook(string connect, string kind)
        {
            return "$t=$env:MDEDITOR_AI_TAB;if($t){try{" +
                   "$m=New-Object System.IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($m);" +
                   "$j=[System.Text.Encoding]::UTF8.GetString($m.ToArray())|ConvertFrom-Json;" +
                   connect +
                   "$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);" +
                   "$w.WriteLine('" + kind + "'+[char]9+$t+[char]9+$j.session_id+[char]9+$j.cwd);$w.Flush();$c.Dispose()}catch{}}";
        }

        private static string PowerShell(string script)
        {
            return "powershell -NoProfile -NonInteractive -EncodedCommand " +
                   Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        /// <summary>
        /// フックからの頼みを受ける。待つのは別のスレッドで、頼まれたことは画面のスレッドでやる。
        /// 「prompt」は、保存と状態の書き出しが済むまで待ってから「ok」を返す（フックはそのあとで状態を読む）。
        /// つながったら、そのつながりは別のスレッドに任せて、すぐ次の口を開く（2026-10-27）。前は口が1つだけで、
        /// あるタブの「prompt」に応えている間（保存の確かめのダイアログを出している間など）、ほかのタブの知らせがつながらずに落ちた。
        /// </summary>
        private void StartAiListener()
        {
            if (_aiListener != null) return;

            // 画面を閉じたら（StopAi）、待っている口を閉じてループを抜ける（2026-11-02）。前は止めず、閉じた画面ごとスレッドが残っていた。
            _aiListener = new Thread(delegate ()
            {
                while (!_aiStopping)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(AiPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, AiPipeSecurity());
                        _aiWaiting = server;
                        if (_aiStopping) throw new ObjectDisposedException(AiPipeName);
                        server.WaitForConnection();
                        _aiWaiting = null;
                    }
                    catch (Exception)
                    {
                        // つないですぐ切られた等。少し置いて待ち直す。閉じたのなら終わる。
                        _aiWaiting = null;
                        if (server != null) server.Dispose();
                        if (_aiStopping) break;
                        Thread.Sleep(100);
                        continue;
                    }

                    var connection = server;
                    try
                    {
                        var worker = new Thread(delegate () { ServeAiRequest(connection); });
                        worker.IsBackground = true;   // 終了を止めない
                        worker.Start();
                    }
                    catch (Exception)
                    {
                        connection.Dispose();   // スレッドを作れなかった。落ちずに待ち受けを続ける（2026-11-26 のレビュー）
                    }
                }
            });
            _aiListener.IsBackground = true;   // 終了を止めない
            _aiListener.Start();
        }

        /// <summary>
        /// PreToolUse のフックの「guard→ツール→フォルダ→項目の名前→値…」（どれも Base64）を読んで、AiGuard で確かめる。断るなら理由、よければ null。
        /// 断ったら、下端の状態の行でも知らせる。読めない頼みは止めない。
        /// </summary>
        private string CheckAiGuard(string request)
        {
            var parts = request.Split('\t').Skip(1).Select(part =>
            {
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(part));
                }
                catch (FormatException)
                {
                    return string.Empty;
                }
            }).ToList();
            if (parts.Count < 2) return null;

            var input = new List<KeyValuePair<string, string>>();
            for (var i = 2; i + 1 < parts.Count; i += 2) input.Add(new KeyValuePair<string, string>(parts[i], parts[i + 1]));

            return CheckAiGuard(parts[0], parts[1], input);
        }

        /// <summary>AiGuard で確かめる。断ったら、下端の状態の行でも知らせる。断るなら理由、よければ null。HTTP のフックとパイプのフックの両方がここを通る。</summary>
        private string CheckAiGuard(string tool, string cwd, IList<KeyValuePair<string, string>> input)
        {
            string answer;
            try
            {
                answer = AiGuard.Check(tool, cwd, input);
            }
            catch (Exception)
            {
                // 確かめられなければ断る（通さない。AiLink と同じ。2026-11-26。前は何も返さず、Claude Code が通していた）
                Dispatcher.BeginInvoke(new Action(() => Status("AI に渡さない場所かを確かめられなかったので、断りました（" + tool + "）。")));
                return "Tsuzuri が AI に渡さない場所かを確かめられなかったので、この操作は断った。少し待ってからやり直す。続くなら利用者に伝える。";
            }
            if (answer != null)
                Dispatcher.BeginInvoke(new Action(() => Status("Claude が AI に渡さない場所（" + AiGuard.MarkerName + "）を開こうとしたので、断りました（" + tool + "）。")));
            return answer;
        }

        /// <summary>
        /// 話しかけられたとき（UserPromptSubmit）。共通ターミナルの画面を読み、保存して今の状態を書き出す。済むまで待つ（パイプのスレッド・HTTP のスレッドから）。
        /// </summary>
        /// <param name="tabId">頼んできたタブの番号（パイプのフック）。分からなければ null。</param>
        /// <param name="session">頼んできた会話の番号（HTTP のフックの session_id）。分からなければ null。</param>
        /// <returns>書き出した今の状態。書けなければ・間に合わなければ null。</returns>
        /// <param name="prompt">話しかけた文（HTTP のフックの prompt。会話の題を作るのに使う）。分からなければ null。</param>
        private string PrepareAiPrompt(string tabId, string session, string prompt = null)
        {
            // 共通ターミナルの画面を先に読む（2026-11-08）。読むのはページの中の式で、画面のスレッドが空いていないと返らないので、ここで待つ。
            _aiShellScreen = ReadAiShellScreen(AiShellTailLines, 2000);

            // 未保存の編集があれば、保存してから渡す（2026-10-25。話しかけたら保存してよいと決めた）。
            // 外で書き換わっていて上書きするか尋ねている間はフックが待つ。止まったままにはしないよう、
            // 4分半で諦めて前の状態を渡させる（フックの持ち時間は5分）。
            return Dispatcher.Invoke(new Func<string>(() => HandleAiPrompt(FindAiTab(tabId, session), prompt)), TimeSpan.FromSeconds(270)) as string;
        }

        /// <summary>
        /// Claude が書き換えたあと（PostToolUse）。図の画像を先に作り（読み直した本文に新しい図が出るように。待つのは 20 秒まで）、外で変わった文書を読み直す。
        /// 作れなかった図があればその知らせ、無ければ null。
        /// </summary>
        /// <param name="file">書いたファイル（tool_input.file_path）。無ければ null。</param>
        /// <param name="folder">作業のフォルダ（cwd）。</param>
        private string HandleAiChanged(string file, string folder)
        {
            var written = WrittenPath(file, folder);
            string problem = null;
            try
            {
                problem = Dispatcher.Invoke(new Func<string>(() => RenderAiDrawings(written)), TimeSpan.FromSeconds(20)) as string;
            }
            catch (Exception)
            {
                // 作れなくても、読み直しは続ける
            }

            Dispatcher.BeginInvoke(new Action(ReloadChangedDocuments), DispatcherPriority.Background);
            return problem;
        }

        /// <summary>つながった1つの頼みに応える（そのつながり用のスレッドで）。</summary>
        private void ServeAiRequest(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                {
                    var reader = new StreamReader(server, new UTF8Encoding(false), false, 256, true);
                    var request = reader.ReadLine();
                    // つないですぐ切れた相手（何も送らなかった）には答えない（下の「書き換えたあと」の扱いに落として、図の描き直しや一覧の読み直しを走らせないように。2026-11-26 のレビュー）
                    if (request == null) return;

                    if (request == "prompt" || (request != null && request.StartsWith("prompt\t", StringComparison.Ordinal)))
                    {
                        // 「prompt→タブの番号」（2026-11-22。タブごとのフォルダ・固定したカーソルを渡すため）。前の形（番号なし）も受ける
                        // 書けたときだけ「ok」。書けなければ「stale」で、フックは前の状態に断りを付けて出す（2026-11-22）
                        string written = null;
                        try
                        {
                            written = PrepareAiPrompt(request.Length > 7 ? request.Substring(7) : null, null);
                        }
                        catch (Exception)
                        {
                        }

                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(written != null ? "ok" : "stale");
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (request != null && (request == "shell-dump" || request == "shell-open" || request.StartsWith("shell-type\t", StringComparison.Ordinal)))
                    {
                        // 共通ターミナル（2026-11-08。terminal.ps1）。全部を書き出す・文字を置く・出す（2026-11-21）。
                        string answer;
                        if (request == "shell-open")
                        {
                            answer = OpenAiShellForClaude();
                        }
                        else if (request == "shell-dump")
                        {
                            answer = DumpAiShell();
                        }
                        else
                        {
                            string text = null;
                            try
                            {
                                text = Encoding.UTF8.GetString(Convert.FromBase64String(request.Substring("shell-type\t".Length)));
                            }
                            catch (FormatException)
                            {
                            }

                            var screen = ReadAiShellScreen(1, 2000);
                            answer = text == null
                                ? "error\t置く文字を読めなかった。"
                                : Dispatcher.Invoke(new Func<string>(() => TypeIntoAiShell(text, screen != null && screen.Bracketed)));
                        }

                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(answer.Replace("\r", " ").Replace("\n", " "));
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (request != null && (request.StartsWith("excel-read\t", StringComparison.Ordinal) || request.StartsWith("excel-write\t", StringComparison.Ordinal)
                                                 || request.StartsWith("excel-script\t", StringComparison.Ordinal) || request.StartsWith("excel-overview\t", StringComparison.Ordinal)))
                    {
                        // Excel を範囲を指定して読む・書く・COM の台本を動かす（2026-11-24・25。excel.ps1。MainWindow.Excel.cs）。画面のスレッドは使わない
                        MaybeDeleteStaleAiFiles();
                        var answer = request.StartsWith("excel-read\t", StringComparison.Ordinal) ? ServeExcelRead(request)
                                     : request.StartsWith("excel-write\t", StringComparison.Ordinal) ? ServeExcelWrite(request)
                                     : request.StartsWith("excel-overview\t", StringComparison.Ordinal) ? ServeExcelOverview(request) : ServeExcelScript(request);
                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(answer.Replace("\r", " ").Replace("\n", " "));
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (Tsuzuri.Office.OutlookLink.Handles(request))
                    {
                        // Outlook を探す・読む・下書きに保存する（2026-11-26。outlook.ps1。MainWindow.Outlook.cs・Office/OutlookLink.cs）。画面のスレッドは使わない
                        var answer = ServeOutlook(request);
                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(answer.Replace("\r", " ").Replace("\n", " "));
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (request != null && request.StartsWith("guard\t", StringComparison.Ordinal))
                    {
                        // 読み書きの前（2026-11-09。AiGuard）。画面のスレッドは使わない
                        var answer = CheckAiGuard(request);
                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(answer ?? "ok");
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (request != null && (request.StartsWith("session\t", StringComparison.Ordinal)
                                                 || request.StartsWith("ended\t", StringComparison.Ordinal)))
                    {
                        var parts = request.Split('\t');
                        if (parts.Length >= 3)
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                OnAiSession(parts[0] == "session", parts[1], parts[2], parts.Length > 3 ? parts[3] : null);
                            }));
                    }
                    else
                    {
                        // 図の画像を先に作る（読み直した本文に、新しい図が出るように）。待つのは 20 秒まで。
                        // 「changed→書いたファイル→フォルダ」。
                        var parts = (request ?? string.Empty).Split('\t');
                        var problem = HandleAiChanged(parts.Length > 1 ? parts[1] : null, parts.Length > 2 ? parts[2] : null);

                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine(problem == null ? "ok" : problem.Replace("\r", " ").Replace("\n", " "));
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                }
            }
            catch (Exception)
            {
                // つないですぐ切られた・エディタを閉じている途中等。フックは前の状態を出す。
            }
        }

        /// <summary>見て回って作れなかった図の .json と、そのときの更新時刻。同じ中身のままなら、2度は試さない。</summary>
        private readonly Dictionary<string, DateTime> _aiDrawingFailures = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// exe の隣の ssh フォルダを AiSshLocal へ写し、本人だけが触れる権限にする（2026-11-04。SPEC 19章）。
        /// 元で消したファイルは写した先からも消す（known_hosts は除く）。失敗しても claude の起動は止めない。
        /// </summary>
        private void CopySshFolder()
        {
            try
            {
                if (!Directory.Exists(AiSshFolder)) return;

                Directory.CreateDirectory(AiSshLocal);
                var folderSecurity = new DirectorySecurity();
                folderSecurity.SetAccessRuleProtection(true, false);
                folderSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(AiSshLocal, folderSecurity);

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in Directory.GetFiles(AiSshFolder))
                {
                    var name = Path.GetFileName(source);
                    if (string.Equals(name, "known_hosts", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.StartsWith("._", StringComparison.Ordinal)) continue;

                    names.Add(name);
                    var target = Path.Combine(AiSshLocal, name);
                    var bytes = File.ReadAllBytes(source);
                    // config.txt は UTF-8 にして写す（2026-11-21）。メモ帳などで Shift_JIS（ANSI）で保存されていると、askpass・台本・Claude が
                    // UTF-8 として読み、日本語の名称が一致しなかった
                    if (string.Equals(name, "config.txt", StringComparison.OrdinalIgnoreCase)) bytes = ToUtf8(bytes);
                    File.WriteAllBytes(target, bytes);

                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetAccessRuleProtection(true, false);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(target, fileSecurity);
                }

                foreach (var target in Directory.GetFiles(AiSshLocal))
                {
                    var name = Path.GetFileName(target);
                    if (string.Equals(name, "known_hosts", StringComparison.OrdinalIgnoreCase) || names.Contains(name)) continue;
                    File.Delete(target);
                }
            }
            catch (Exception ex)
            {
                Status("SSH の鍵を写せませんでした: " + ex.Message);
            }
        }

        /// <summary>UTF-8 として読めればそのまま（BOM も残す）。読めなければ ANSI（日本語の Windows なら Shift_JIS）として読み、BOM 無しの UTF-8 にする。</summary>
        private static byte[] ToUtf8(byte[] bytes)
        {
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return bytes;
            }
            catch (DecoderFallbackException)
            {
                return new UTF8Encoding(false).GetBytes(Encoding.Default.GetString(bytes));
            }
        }

        /// <summary>
        /// 全部の画面・全部のエディタで共有するファイル（instructions.md・ssh-askpass.cmd）を書く。中身が同じなら書かない。書くときは一時ファイルから置き換える
        /// （2026-11-02。前は起動のたびに書き直し、ほかのタブの claude が起動する瞬間と重なると、空や途中の指示を読むおそれがあった）。
        /// </summary>
        private static void WriteIfChanged(string path, string text)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;
            }
            catch (Exception)
            {
                // 読めなければ書く
            }

            var temp = path + "." + Process.GetCurrentProcess().Id + ".saving";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception)
            {
                // ほかのエディタが同時に書いた（中身は同じ）。あればそれを使う
                TryDeleteDrawingTemp(temp);
                if (!File.Exists(path)) throw;
            }
        }

        /// <summary>フックが送った、書いたファイルのフルパス。無い・読めなければ null。</summary>
        private static string WrittenPath(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? path : Path.Combine(folder, path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Claude が書いた図の画像を作る（2026-11-01。SPEC 19章）。図（.json）を、図エディタを開かずに描いて .png を書く（DrawingEditorHost.RenderPng）。
        /// ・その呼び出しで書いたファイル（written）が .json なら、それを必ず描く。作れなければ、何がいけないかを返す（書いた会話の Claude に知らせる）。
        ///   名前が「図 」で始まるか "shapes" があれば、"kind" の無い JSON も図として扱い、そのわけを返す（前は黙って飛ばし、Claude は描けたと思っていた）
        /// ・そのほか（Bash で書いた）は、この画面で開いている文書の添付フォルダを見て回り、.png が無いか古い図を描く。
        ///   作れなかった図は知らせない（2026-11-02）。誰が書いたか分からず、ほかの会話の Claude や、同期で来たほかの版の図を「直して」と頼んでしまうため
        /// 作れなかった図の知らせ（返す相手がいるとき）。無ければ null。
        /// </summary>
        private string RenderAiDrawings(string written)
        {
            var problems = new List<string>();
            var rendered = new List<string>();
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

            if (written != null && string.Equals(Path.GetExtension(written), ".json", StringComparison.OrdinalIgnoreCase) && File.Exists(written))
            {
                string json = null;
                try
                {
                    json = File.ReadAllText(written, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    problems.Add(Path.GetFileName(written) + ": 読めませんでした: " + ex.Message);
                }

                if (json != null && LooksLikeDrawing(written, json))
                {
                    _aiDrawingFailures.Remove(written);
                    bool broken;
                    var problem = RenderAiDrawing(written, json, dpiScale, out broken);
                    if (problem == null) rendered.Add(Path.GetFileName(Path.ChangeExtension(written, ".png")));
                    else problems.Add(problem);
                }
            }

            foreach (var jsonPath in DrawingsToRender())
            {
                if (written != null && string.Equals(jsonPath, written, StringComparison.OrdinalIgnoreCase)) continue;

                DateTime at;
                string json;
                try
                {
                    at = File.GetLastWriteTimeUtc(jsonPath);
                    json = File.ReadAllText(jsonPath, Encoding.UTF8);
                }
                catch (Exception)
                {
                    continue;   // 書いている最中など。次の知らせで見る
                }

                if (json.IndexOf(DrawingMarker, StringComparison.Ordinal) < 0) continue;   // 図ではない JSON

                DateTime failedAt;
                if (_aiDrawingFailures.TryGetValue(jsonPath, out failedAt) && failedAt == at) continue;

                bool broken;
                if (RenderAiDrawing(jsonPath, json, dpiScale, out broken) == null)
                {
                    _aiDrawingFailures.Remove(jsonPath);
                    rendered.Add(Path.GetFileName(Path.ChangeExtension(jsonPath, ".png")));
                }
                else if (broken)
                {
                    _aiDrawingFailures[jsonPath] = at;   // 図の誤りは、直るまで試さない。書けなかっただけなら、次の知らせでまた試す
                }
            }

            if (rendered.Count > 0)
            {
                foreach (var view in _stack) view.RefreshImages();
                Status("AI が書いた図を描きました: " + string.Join("、", rendered));
            }

            if (problems.Count == 0) return null;

            Status("AI が書いた図を描けませんでした: " + string.Join(" / ", problems));
            return "Tsuzuri: 図の .png を作れませんでした。 " + string.Join(" / ", problems);
        }

        private const string DrawingMarker = "markdown-editor-drawing";

        /// <summary>Claude が書いた .json が、図のつもりのものか（"kind" を書き忘れていても、名前か中身で見分ける）。</summary>
        private static bool LooksLikeDrawing(string path, string json)
        {
            return json.IndexOf(DrawingMarker, StringComparison.Ordinal) >= 0
                   || Path.GetFileName(path).StartsWith("図 ", StringComparison.Ordinal)
                   || json.IndexOf("\"shapes\"", StringComparison.Ordinal) >= 0;
        }

        /// <summary>この画面で開いている文書の添付フォルダの図（.json）のうち、同じ名前の .png が無いか、.json より古いもの。</summary>
        private List<string> DrawingsToRender()
        {
            var found = new List<string>();
            var folders = _stack.Select(view => view.FilePath).Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(Attachments.FolderOf)
                .Where(folder => folder != null && Directory.Exists(folder)).ToList();

            foreach (var folder in folders)
            {
                try
                {
                    foreach (var jsonPath in Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        var pngPath = Path.ChangeExtension(jsonPath, ".png");
                        if (!File.Exists(pngPath) || File.GetLastWriteTimeUtc(pngPath) < File.GetLastWriteTimeUtc(jsonPath)) found.Add(jsonPath);
                    }
                }
                catch (Exception)
                {
                    // 見られないフォルダは飛ばす
                }
            }

            return found;
        }

        /// <summary>図を1つ描いて .png を書く。書けたら null、だめなら「名前: わけ」。図の誤りか、書けなかったのかを分けて言う。</summary>
        /// <param name="broken">図そのものの誤りで描けなかったか（書けなかっただけなら false）。</param>
        private static string RenderAiDrawing(string jsonPath, string json, double dpiScale, out bool broken)
        {
            var pngPath = Path.ChangeExtension(jsonPath, ".png");

            string error;
            var png = DrawingEditor.DrawingEditorHost.RenderPng(json, dpiScale, out error);
            broken = png == null;
            if (png == null) return Path.GetFileName(jsonPath) + ": " + error + " .json を直して書き直してください。";

            var temp = pngPath + ".saving";
            try
            {
                WriteDrawingTemp(temp, png);
                ReplaceDrawingFile(temp, pngPath, false);
                return null;
            }
            catch (Exception ex)
            {
                TryDeleteDrawingTemp(temp);
                return Path.GetFileName(pngPath) + " を書けませんでした（図の誤りではない。.json は直さなくてよい）: " + ex.Message;
            }
        }

        /// <summary>
        /// パイプに入れるのは、このエディタを動かしている利用者だけ（2026-10-27）。ネットワーク越しのつなぎは断る。
        /// 前は既定の権限で、ほかの利用者やネットワーク越しからも「prompt」を送って保存させることができた。
        /// </summary>
        private static PipeSecurity AiPipeSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        /// <summary>
        /// 話しかけられた頼み（フックの「prompt」）に応える。保存して、今の状態を書き出す。
        /// ダイアログを出している間（保存するか尋ねている・上書きするか尋ねている等）は保存しない（2026-10-27）。
        /// その最中にも頼みは届き、ここで保存すると、利用者が「保存しない」「キャンセル」を選ぼうとしている文書まで保存してしまった。
        /// そのときは状態だけ書き出し、保存しなかったことを状態に書き添える。
        /// </summary>
        private string HandleAiPrompt(AiTab tab, string prompt = null)
        {
            RequestAiTopic(tab, prompt);   // 題がまだ無ければ、話しかけた文から作る（2026-11-22。MainWindow.AiTopic.cs）

            string skipped = null;
            if (_aiPromptHandling) skipped = "前の頼みに応えている最中だった";
            else if (IsShowingDialog()) skipped = "ダイアログを出していた";

            if (skipped == null)
            {
                _aiPromptHandling = true;
                try
                {
                    SaveForAi();
                }
                finally
                {
                    _aiPromptHandling = false;
                }
            }
            else if (_stack.Any(view => view.IsDirty))
            {
                Status("AI に渡す前の保存をしませんでした（" + skipped + "ため）。");
            }

            // 外で書き換わった文書を、渡す前に読み直す（2026-11-22。Claude が書いた直後は、書き換えの知らせの読み直しより先にここへ来ることがあり、古い行番号を渡していた）
            if (skipped == null) ReloadChangedDocuments();

            // 図エディタを出していれば、今の図をファイルに書いてから渡す（2026-11-23。Claude はファイルを読むので。MainWindow.Drawing.cs）
            string drawingNote = null;
            if (skipped == null) drawingNote = FlushDrawingForAi();
            else if (_drawing != null) _drawing.FileIsCurrent = false;

            var note = skipped == null ? null : "エディタは" + skipped + "ため、今回は保存していない。";
            if (drawingNote != null) note = note == null ? drawingNote : note + "\n" + drawingNote;
            var context = WriteAiContext(note, tab);
            ShowAiContextSummary(tab);
            return context;
        }

        /// <summary>
        /// ダイアログを出している最中か。このアプリのダイアログはどれもこの窓が親なので、出ている間は Windows がこの窓を操作できなくする。それで見分ける。
        /// </summary>
        private bool IsShowingDialog()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero && !IsWindowEnabled(handle);
        }

        /// <summary>
        /// Claude に話しかけられたとき、未保存の編集があれば保存する（尋ねない。2026-10-25）。Claude が読むのはディスクの内容なので、
        /// 今の画面に揃えてから渡す。開いたあとに外（Claude 自身など）で書き換わっていたときだけ、上書きするか尋ねる（SaveDocument）。
        /// 自動保存はやめたが、Claude に話しかけるのは「今の内容を見て」という意思表示なので、保存してよいとした。
        /// </summary>
        private void SaveForAi()
        {
            if (!_stack.Any(view => view.IsDirty)) return;
            _savingForAi = true;
            try
            {
                SaveAll();
            }
            finally
            {
                _savingForAi = false;
            }
        }

        /// <summary>AI に話しかけられて保存している最中（閉じていないコードブロックで保存を断っても、尋ねる画面を出さない。2026-11-22）。</summary>
        private bool _savingForAi;

        /// <summary>
        /// エディタの今の状態を書き出す。話しかけられたときと、claude を起動するとき。
        /// claude をまだ起動していなければ何もしない。書けなくても編集の邪魔はしない。
        /// </summary>
        /// <param name="note">状態に書き添えること（保存しなかった理由など）。無ければ null。</param>
        /// <param name="tab">話しかけてきた会話のタブ（そのタブのフォルダ・固定したカーソルを足す）。分からなければ null。</param>
        /// <returns>書いた今の状態。書けなければ null。</returns>
        private string WriteAiContext(string note, AiTab tab = null)
        {
            if (_aiContextPath == null) return null;

            try
            {
                var context = AiContext(note, tab);
                File.WriteAllText(_aiContextPath, context, new UTF8Encoding(false));
                if (tab != null) File.WriteAllText(AiTabContextPath(tab), context, new UTF8Encoding(false));   // パイプのフックはタブの分を読む
                return context;
            }
            catch (Exception ex)
            {
                Status("AI に今の状態を渡せませんでした: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// エディタが今回の状態を書けず、前に書いた状態を渡すときに頭に付ける断り（2026-11-22。HTTP とパイプのフックで同じ文）。
        /// 前の状態は、ほかの会話のために書いたものや、前の時点のものかもしれない。指示にもこの文の扱いを書いている。
        /// </summary>
        private const string AiStaleNote =
            "（前に書いた状態を渡す: エディタが今回の状態を書けなかったので、前に書いたものを渡す。ほかの会話のため、または前の時点のものかもしれない。"
            + "中の「この会話は〜」「プロジェクトフォルダ」「ファイル」「見ているページ」「カーソル」「まず読む範囲」「図エディタ」「Excel」「Excel の作業ファイル」「Outlook」「Outlook のメール」「Outlook の作業ファイル」の段を信じず、読む・書く前に利用者に確かめる）\n";

        /// <summary>タブごとの今の状態のファイル（パイプのフックが読む。2026-11-22）。claude を起動する前は null。</summary>
        private string AiTabContextPath(AiTab tab)
        {
            if (_aiContextPath == null || tab == null) return null;
            return _aiContextPath.Substring(0, _aiContextPath.Length - 3) + "-" + tab.Id + ".md";
        }

        /// <summary>
        /// AI の欄の下端の細い行に、今渡した状態を1行で出す（2026-10-24。2026-10-26 に上端の段から下端へ移した）。話しかけられて状態を書いたとき。
        /// 例: 渡した状態  TODO.md › p3「Tasks」 12 行目  14:05:31
        /// </summary>
        private void ShowAiContextSummary(AiTab tab)
        {
            // カーソルを固定している会話には、固定した位置を出す（2026-11-22。エディタの画面の位置を出すと、利用者がそれを渡したと思い込む）
            if (tab != null && tab.Pin != null)
            {
                var pinned = AiGuard.IsGuarded(tab.Pin.File) ? "AI に渡さない文書（場所を渡していない）"
                    : PinLabel(tab.Pin) + (File.Exists(tab.Pin.File) ? string.Empty : "（ファイルが見つからない）");
                AiContextText.Text = tab.Name + " に渡した状態（固定）  " + pinned + ExcelSummaryPart() + "  " + DateTime.Now.ToString("HH:mm:ss");
                AiContextText.ToolTip = tab.Pin.File;
                RefreshAiFileLinks();
                return;
            }

            // どの会話に渡したか（選んでいないタブの頼みのこともある）。分からなければそう出す
            var who = tab != null ? tab.Name + " に" : "（どの会話か分からない）";
            var view = Active;
            if (view == null || view.Owner == null)
            {
                AiContextText.Text = who + "渡した状態  ファイルを開いていません" + ExcelSummaryPart() + "  " + DateTime.Now.ToString("HH:mm:ss");
                AiContextText.ToolTip = null;
                RefreshAiFileLinks();
                return;
            }

            if (AiGuard.IsGuarded(view.FilePath))
            {
                AiContextText.Text = who + "渡していない  AI に渡さない文書（" + AiGuard.MarkerName + " のあるフォルダ）" + ExcelSummaryPart() + "  " + DateTime.Now.ToString("HH:mm:ss");
                AiContextText.ToolTip = null;
                RefreshAiFileLinks();
                return;
            }

            var selected = _drawing == null && !string.IsNullOrEmpty(view.SimpleSelectedText.Trim());   // 図エディタで覆っている間は選択を渡さない
            AiContextText.Text = who + "渡した状態  " + Path.GetFileName(view.FilePath) + " › " + PageLabel(view)
                                 + "  " + view.TextArea.Caret.Line + " 行目" + (selected ? "（選択あり）" : string.Empty)
                                 + ExcelSummaryPart() + "  " + DateTime.Now.ToString("HH:mm:ss");
            AiContextText.ToolTip = view.FilePath;
            RefreshAiFileLinks();
        }

        /// <summary>
        /// AI の欄の下端に、Claude が読むファイルを押せるボタンで並べる（2026-11-08。SPEC 19章）。あるものだけ出す。押すと .md は Tsuzuri で添付フォルダを作らずに、.json はメモ帳で開く（OpenAiFile）。
        /// 2段に分ける。上の段「Claude Code」は Claude Code が自分で（どこで動かしても）読むもの、
        /// 下の段「Tsuzuri」は Tsuzuri が独自に渡しているもの。
        /// </summary>
        private void RefreshAiFileLinks()
        {
            var folder = _aiTab != null && !string.IsNullOrEmpty(_aiTab.Folder) ? _aiTab.Folder
                : HasDocument && !string.IsNullOrEmpty(Active.FilePath) ? Path.GetDirectoryName(Active.FilePath) : null;
            var tsuzuri = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("指示", Path.Combine(AiFolder, "instructions.md")),
                new KeyValuePair<string, string>("今の状態", _aiContextPath),
                new KeyValuePair<string, string>("図の書き方", AiDrawingFormat),
                new KeyValuePair<string, string>("設定（フック）", _aiSettingsPath),
            };
            var generation = ++_aiFileLinksGeneration;

            // ファイルがあるかを見るのは裏で（\\Mac\ の上では1つごとに時間がかかり、上へたどると数十になる）。
            System.Threading.Tasks.Task.Run(() =>
            {
                var claude = ClaudeCodeFiles(folder);
                var exists = tsuzuri.Where(file => !string.IsNullOrEmpty(file.Value) && File.Exists(file.Value))
                    .Select(file => new AiFile { Label = file.Key, Path = file.Value, Exists = true }).ToList();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (generation != _aiFileLinksGeneration) return;   // 後から頼んだ分がある
                    AiFileLinks.Children.Clear();
                    AddAiFileRow("Claude Code", claude);
                    AddAiFileRow("Tsuzuri", exists);
                }));
            });
        }

        private int _aiFileLinksGeneration;

        /// <summary>
        /// Claude Code が自分で読むファイルのうち、利用者が見て直すもの（2026-11-08）。CLAUDE.md は無くても、MEMORY.md はあるときだけ。
        /// ・全体の CLAUDE.md（%USERPROFILE%\.claude。CLAUDE_CONFIG_DIR があればそこ）
        /// ・会話を始めたフォルダ（分からなければ開いている文書のフォルダ）の CLAUDE.md
        /// ・そのフォルダの自動の記憶（projects\フォルダの名前\memory\MEMORY.md。名前はフォルダの場所の英数字以外を - にしたもの）
        /// 上のフォルダの CLAUDE.md・rules・settings・取り込み（@）なども読まれるが、使っていないので出さない（全部出すとボタンが多すぎた）。
        /// </summary>
        private static List<AiFile> ClaudeCodeFiles(string folder)
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

            // CLAUDE.md は無くても出す（押すと空のファイルを作って開く）。手で書き足す場所なので、無いと書く所が分からない（2026-11-08）。
            var files = new List<AiFile> { new AiFile { Label = "CLAUDE.md（全体）", Path = Path.Combine(home, "CLAUDE.md"), Creatable = true } };

            if (!string.IsNullOrEmpty(folder))
            {
                files.Add(new AiFile { Label = "CLAUDE.md（" + Path.GetFileName(folder.TrimEnd('\\')) + "）", Path = Path.Combine(folder, "CLAUDE.md"), Creatable = true });
                files.Add(new AiFile { Label = "MEMORY.md", Path = Path.Combine(home, "projects", Regex.Replace(folder, "[^A-Za-z0-9]", "-"), "memory", "MEMORY.md") });
            }

            foreach (var file in files)
            {
                try
                {
                    file.Exists = File.Exists(file.Path);
                }
                catch (Exception)
                {
                    // 読めない場所（\\Mac\ が切れたなど）。無いものとする
                }
            }

            return files.Where(file => file.Exists || file.Creatable).ToList();
        }

        /// <summary>AI の欄の下端に出すファイル1つ（2026-11-08）。</summary>
        private sealed class AiFile
        {
            public string Label;
            public string Path;
            public bool Exists;

            /// <summary>無くても出し、押すと空のファイルを作って開く（CLAUDE.md）。</summary>
            public bool Creatable;
        }

        /// <summary>見出し（Claude Code・Tsuzuri）とファイルのボタンの1段。ファイルが1つも無ければ出さない。</summary>
        private void AddAiFileRow(string title, List<AiFile> files)
        {
            var row = new WrapPanel();
            row.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Width = 84,
                Margin = new Thickness(6, 3, 0, 0),
                Foreground = (Brush)FindResource("TextMuted"),
            });

            foreach (var file in files)
            {
                var path = file.Path;
                var missing = !file.Exists;
                var button = new Button
                {
                    Content = missing ? file.Label + "（無し）" : file.Label,
                    FontSize = 12,
                    Padding = new Thickness(6, 1, 6, 1),
                    Foreground = (Brush)FindResource(missing ? "TextMuted" : "AccentText"),
                    ToolTip = path + (missing ? "（まだ無い。押すと空のファイルを作って開く）"
                        : IsMarkdown(path) ? "（Tsuzuri で開く。添付フォルダは作らない）" : "（メモ帳で開く）"),
                    Focusable = false,
                };
                var style = FindResource("ToolButton") as Style;
                if (style != null) button.Style = style;
                button.Click += delegate
                {
                    if (missing && !CreateEmptyFile(path)) return;
                    OpenAiFile(path);
                    if (missing) RefreshAiFileLinks();
                };
                row.Children.Add(button);
            }

            if (row.Children.Count > 1) AiFileLinks.Children.Add(row);
        }

        /// <summary>
        /// Claude が読むファイルを開く（2026-11-08）。.md は Tsuzuri で、添付フォルダを作らずに開く（Attachments.MarkBare）。
        /// .json などはメモ帳で開く。すでに添付フォルダのある文書は、ふつうに開く。
        /// </summary>
        private void OpenAiFile(string path)
        {
            if (!IsMarkdown(path))
            {
                OpenInNotepad(path);
                return;
            }

            if (!Directory.Exists(Attachments.FolderOf(path))) Attachments.MarkBare(path);
            OpenFileThen(path, (window, document) => window.GoToAnchor(document, null));
        }

        /// <summary>空のファイルを作る（無い CLAUDE.md を押したとき）。作れなければ知らせて false。</summary>
        private bool CreateEmptyFile(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Status("作れませんでした: " + path + "（" + ex.Message + "）");
                return false;
            }
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status("開けませんでした: " + ex.Message);
            }
        }

        /// <summary>
        /// 話しかけるたびに Claude に渡す今の状態（2026-11-22 に組み直した。プロンプトの見直しで、固定したカーソルがあると「見ているページ」などの呼び名が
        /// エディタの画面の側を指していたため）。
        /// ・この会話の話題の文書・ページ・カーソルは、いつも「ファイル」「見ているページ」「カーソル」「まず読む範囲」の呼び名で書く（指示はこの呼び名で決まりを書いている）。
        ///   カーソルを固定していなければエディタの画面の位置、固定していれば固定した位置
        /// ・固定していれば、エディタの画面の位置は「エディタの画面（参考）」として、別の呼び名（画面のファイル・画面のページ・画面のカーソル）で後ろに書く
        /// ・話題の文書の種類・添付フォルダ・変更の履歴の git・図の念押しは、話題の文書について書く
        /// </summary>
        /// <param name="tab">話しかけてきた会話のタブ。分からなければ null（フォルダ・固定は書かない）。</param>
        private string AiContext(string note, AiTab tab)
        {
            var text = new StringBuilder();
            text.Append("[Tsuzuri の今の状態 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("]\n");
            if (!string.IsNullOrEmpty(note)) text.Append(note).Append('\n');

            AppendAiProject(text, tab);   // この会話のプロジェクトフォルダ（MainWindow.AiPin.cs）

            var view = Active;
            var hasView = view != null && view.Owner != null;
            var viewGuarded = hasView && AiGuard.IsGuarded(view.FilePath);
            var pin = tab != null ? tab.Pin : null;

            // 固定・プロジェクトフォルダがあるかを、無いときも書く（2026-11-22。書かないと、前の頼みで固定していた・プロジェクトフォルダを指定していたことを、
            // Claude が続いていると思い込む）。どの会話か分からなければ、そう書く
            var editorShown = hasView && !viewGuarded;   // 下に「ファイル」「見ているページ」「カーソル」の行を書くか
            if (tab == null)
                text.Append("（どの会話からの頼みか分からなかったので、この会話のカーソルの固定とプロジェクトフォルダは渡せていない。")
                    .Append("前の頼みで固定やプロジェクトフォルダがあったなら、それが今も続いているかは分からない。読む・書く前に利用者に確かめる。")
                    .Append(editorShown ? "下の「ファイル」「見ているページ」「カーソル」はエディタの画面の今の位置" + (_drawing != null ? "（図エディタの下に隠れていて、今は見えていない）" : "") + "で、この会話が固定していた位置とは違うかもしれない）\n" : "）\n");
            else if (pin == null)
                text.Append("この会話はカーソルを固定していない（前の頼みで固定していても、今は解除されている）。")
                    .Append(editorShown ? (_drawing != null ? "下の「ファイル」「見ているページ」「カーソル」は、エディタの画面の今の位置（図エディタの下に隠れていて、今は見えていない）。\n" : "下の「ファイル」「見ているページ」「カーソル」は、エディタの画面の今の位置。\n") : "\n");

            PageDocument subject = null;   // 話題の文書（図の念押し・未保存の断りに使う）
            if (pin != null)
            {
                subject = AppendAiPinnedSubject(text, pin);   // 固定した位置（MainWindow.AiPin.cs）
                AppendEditorReference(text, hasView ? view : null, viewGuarded);
            }
            else if (!hasView)
            {
                text.Append("ファイルは開いていない。\n");
            }
            else if (viewGuarded)
            {
                // AI に渡さない場所の文書（2026-11-09。AiGuard）。場所・ページ・カーソルの行・選んだ文字を書かない。頼みそのものは送るので、ほかの話はできる
                text.Append("見ている文書は、利用者が AI に渡さないと決めた場所（目印 ").Append(AiGuard.MarkerName)
                    .Append(" のあるフォルダの中）にある。場所も中身も渡していない。尋ねたり探したりしない。\n");
            }
            else
            {
                subject = view.Owner;
                AppendDocumentFacts(text, subject);
                int first, last;
                bool toEnd;
                var ranged = subject.LineRangeOf(view.PageId, out first, out last, out toEnd);
                var line = view.TextArea.Caret.Line;
                AppendPageAndCursor(text, ranged, first, last, toEnd, PageLabel(view), line,
                    ": " + view.Document.GetText(view.Document.GetLineByNumber(line)));

                // 図エディタで覆っている間は、本文で選んでいる文字を渡さない（2026-11-23。見えていない文字を「これ」と取り違えないように）
                var selected = view.SimpleSelectedText;
                if (_drawing == null && !string.IsNullOrEmpty(selected.Trim()))
                    text.Append("選んでいる文字:\n").Append(Fenced(selected.Replace("\r\n", "\n")));
            }

            if (hasView)
            {
                // AI に渡さない文書のページは題を伏せる（2026-11-15。右に開いていると、その題が渡っていた）
                text.Append(pin != null ? "エディタの画面に開いているページの並び（参考。左から）: " : "開いているページの並び（左から）: ")
                    .Append(string.Join(" > ", _stack.Select(page => AiGuard.IsGuarded(page.FilePath) ? "（AI に渡さない文書）" : PageLabel(page)).ToArray())).Append('\n');
            }

            AppendUnsaved(text, subject);
            // 固定しているときは、話題の文書だけを除く（エディタの画面の文書も「ほかに開いている文書」に出し、名前で頼まれても探せるように）
            AppendOtherDocuments(text, pin != null ? subject : hasView && !viewGuarded ? view.Owner : null);
            AppendCalendarContext(text);  // カレンダーを開いていれば、出している週・選んでいる日（2026-11-21）
            AppendDrawingContext(text, pin != null, tab == null);   // 図エディタを出していれば、図のファイル・選んでいる図形（2026-11-23。MainWindow.Drawing.cs）
            AppendExcelContext(text);   // ランチャーの Excel の一覧で選んだブックとシートの名前（2026-11-24。MainWindow.Excel.cs）
            AppendExcelFiles(text, tab);   // この会話の Excel の作業ファイル（2026-11-25）
            AppendOutlookContext(text);   // ランチャーの Outlook の一覧で選んだフォルダのパス（2026-11-26。MainWindow.Outlook.cs）
            AppendOutlookFiles(text, tab);   // この会話の Outlook の下書きの作業ファイル
            AppendAiShellContext(text);   // 共通ターミナルの画面（2026-11-08）

            // 図の頼みは、最初の指示に書くだけでは罫線で描かれた（2026-11-01）。話しかけるたびに1行だけ念を押す（書き方そのものは渡さない）。
            // 形は決め打ちせず、指定が無ければ利用者に尋ねさせる（2026-11-01）。話題の文書で決める（2026-11-22）
            if (_drawing != null && tab != null && pin == null && !AiGuard.IsGuarded(_drawing.JsonPath))
            {
                // 図エディタを出している間は、図の頼みは図エディタの図に描かせる（2026-11-23。形を尋ねさせない）。
                // どの会話か分からないとき（前の頼みで固定していたかもしれない）・固定しているときは、ふだんの念押しのまま
                text.Append("断りの無い図を描く・足す・直す頼みは、形を尋ねずに、上の「図エディタ」の段の「図のファイル」の .json を書き換える（新しい .json は作らない。書き方は ")
                    .Append(AiDrawingFormat).Append("。形（罫線・Mermaid）や、ほかの文書・ほかの図をはっきり指していれば、それに従う。「図エディタで描いて」が形の指定か開いている図のことか分からなければ（特に「新しい図」と言われたら）尋ねる）");
                text.Append(_drawing.FileIsCurrent ? "。\n" : "。ただし段に「今の図を書けていない」とあるので、直す前に利用者に確かめる。\n");
            }
            else if (subject != null)
            {
                if (subject.IsPlain)
                    text.Append("話題の文書はテキストファイルなので、図は頼まれても罫線で書く（図エディタ・Mermaid は使えない。罫線の図をコードブロック ``` で囲まない）。\n");
                else if (Attachments.IsBare(subject.FilePath))
                    text.Append("新しく図を頼まれて形の指定が無ければ、描く前に、罫線・Mermaid のどちらで描くかを尋ねる（話題の文書に図エディタの図は置けない）。\n");
                else
                    text.Append("新しく図を頼まれて形の指定が無ければ、描く前に、図エディタ・罫線・Mermaid のどれで描くかを尋ねる（図エディタに決まったら、描く前に ")
                        .Append(AiDrawingFormat).Append(" を読む。図は話題の文書の添付フォルダに置く）。\n");
            }

            return text.ToString();
        }

        /// <summary>話題の文書の「ファイル」「種類」か「添付フォルダ」「変更の履歴の git」の行。</summary>
        private static void AppendDocumentFacts(StringBuilder text, PageDocument document)
        {
            text.Append("ファイル: ").Append(document.FilePath).Append('\n');
            if (document.IsPlain)
                text.Append("種類: テキストファイル（Markdown ではない。エディタは飾らずにそのまま出す。文字コード ").Append(document.EncodingName)
                    .Append("。読み書きはこの文字コードのまま。見出し・リンク・図などの Markdown の書き方や、ページの印を足さない）\n");
            else if (Attachments.IsBare(document.FilePath))
                text.Append("添付フォルダ: なし（AI の欄から開いた文書。画像・図エディタの図は置けない。フォルダを作らない）\n");
            else
                text.Append("添付フォルダ（画像・図を置く）: ").Append(Attachments.FolderOf(document.FilePath)).Append('\n');

            // ファイル名を付けない（2026-11-03）。この記録に入っているのはこの文書の .md だけなので、付けなくても同じものが出る。
            // 付けると、名前を変える前の記録が出ない。--follow を付けても、大文字小文字だけの変更（TODO.md → todo.md）は Windows の git が辿れなかった。
            var history = DocumentHistory.GitDirOf(document.FilePath);
            if (Directory.Exists(history))
                text.Append("変更の履歴の git: ").Append(history).Append('\n');
        }

        /// <summary>
        /// 話題の「見ているページ」「カーソル」「まず読む範囲」の行。
        /// ページの範囲は終わりまで書く（2026-11-01。「このページに書いて」で、ファイルの末尾（最後のページ）に足さないように）。
        /// </summary>
        /// <param name="cursorTail">カーソルの行のあとに続ける文字（「: 行の文字」か、見つからなかった断り）。</param>
        private static void AppendPageAndCursor(StringBuilder text, bool ranged, int first, int last, bool toEnd, string pageLabel, int line, string cursorTail)
        {
            text.Append("見ているページ: ").Append(pageLabel);
            if (ranged)
            {
                text.Append("（中身はファイルの ").Append(first).Append("〜").Append(last).Append(" 行目");
                text.Append(toEnd ? "。ファイルの末尾まで" : "。" + (last + 1) + " 行目からは別のページ").Append('）');
            }
            text.Append('\n');

            text.Append("カーソル: ページの ").Append(line).Append(" 行目");
            if (ranged) text.Append("（ファイルの ").Append(first - 1 + line).Append(" 行目）");
            text.Append(cursorTail).Append('\n');
            if (!ranged)
                text.Append("（このページの範囲をファイルの行で数えられなかった。行番号はページの中のもの。範囲は `<!--page: ID-->` の行を Grep で探して決める）\n");

            // まず読む範囲（2026-11-13）。何も言わないと Claude は先頭から 2000 行読むことがあり、数万行の文書では会話がすぐ埋まる。
            // カーソルの前後をページの範囲の中で渡す。ページが短ければページ全体。絶対ではなく、足りなければ広げさせる。
            if (!ranged || first <= 0) return;
            var at = first - 1 + line;
            int from = first, to = last;
            if (last - first + 1 > ReadAroundLines * 2)
            {
                from = Math.Max(first, at - ReadAroundLines);
                to = Math.Min(last, at + ReadAroundLines);
            }
            text.Append("まず読む範囲: ファイルの ").Append(from).Append("〜").Append(to).Append(" 行目（Read の offset ").Append(from)
                .Append("・limit ").Append(to - from + 1).Append("）。先頭から読まない。足りなければ広げる\n");
        }

        /// <summary>
        /// カーソルを固定しているときの、エディタの画面の位置（参考）。話題の呼び名（ファイル・見ているページ・カーソル）とは別の呼び名で書き、
        /// 「まず読む範囲」は書かない（Claude が話題の範囲と取り違えないように）。
        /// </summary>
        private void AppendEditorReference(StringBuilder text, MarkdownView view, bool guarded)
        {
            if (_drawing != null)
                text.Append("エディタの画面（参考）: この会話の話題ではない（利用者がほかの会話の作業で動かしている。今は図エディタの下に隠れていて見えていない。下の「画面の〜」の行は、利用者が「エディタの画面」とはっきり言ったとき、または画面のファイルの名前で頼んだときだけ相手にする。図エディタを出している間の「選んだところ」は図エディタで選んでいる図形）。\n");
            else
                text.Append("エディタの画面（参考）: この会話の話題ではない（利用者がほかの会話の作業で動かしている。下の「画面の〜」の行は、利用者が「エディタの画面」「選んだところ」とはっきり言ったとき、または画面のファイルの名前で頼んだときだけ相手にする）。\n");
            if (view == null)
            {
                text.Append("- 画面ではファイルを開いていない\n");
                return;
            }
            if (guarded)
            {
                text.Append("- 画面では AI に渡さない場所の文書を見ている（場所も中身も渡していない。尋ねたり探したりしない）\n");
                return;
            }

            int first, last;
            bool toEnd;
            var ranged = view.Owner.LineRangeOf(view.PageId, out first, out last, out toEnd);
            var line = view.TextArea.Caret.Line;
            text.Append("- 画面のファイル: ").Append(view.FilePath).Append('\n');
            text.Append("- 画面のページ: ").Append(PageLabel(view));
            if (ranged) text.Append("（ファイルの ").Append(first).Append("〜").Append(last).Append(" 行目）");
            text.Append('\n');
            text.Append("- 画面のカーソル: ページの ").Append(line).Append(" 行目");
            if (ranged) text.Append("（ファイルの ").Append(first - 1 + line).Append(" 行目）");
            text.Append('\n');

            var selected = view.SimpleSelectedText;
            if (_drawing == null && !string.IsNullOrEmpty(selected.Trim()))   // 図エディタで覆っている間は渡さない（2026-11-23）
                text.Append("- 画面で選んでいる文字（利用者が「選んだところ」「選んだ文字」と言ったら、これ）:\n").Append(Fenced(selected.Replace("\r\n", "\n")));
        }

        /// <summary>
        /// 保存できていない編集がある文書の断り（2026-11-22。前は「この文書は書き換えず」とだけ書き、どの文書かが分からなかった）。
        /// この画面の文書と話題の文書のうち、未保存のものを名前で書く。閉じていないコードブロックで保存できないなら、その理由も書く。
        /// ほかの画面の文書は「ほかに開いている文書」の行に書く（AppendOtherDocuments）。
        /// </summary>
        private void AppendUnsaved(StringBuilder text, PageDocument subject)
        {
            var documents = _stack.Select(view => view.Owner).Where(owner => owner != null).Distinct().ToList();
            if (subject != null && !documents.Contains(subject)) documents.Add(subject);

            foreach (var document in documents)
            {
                if (AiGuard.IsGuarded(document.FilePath)) continue;
                if (!document.IsDirty)
                {
                    // 外で書き換わったのに読み直せていない（2026-11-22。閉じていないコードブロックのあとにページの印を書かれた等）。行番号は古い
                    var stale = StaleReason(document);
                    if (stale != null && subject != null && !ReferenceEquals(document, subject) && SamePath(document.FilePath, subject.FilePath))
                    {
                        // 話題の位置はファイルから読み直した今の中身で書いた（固定した文書）。古いのは画面の側だけ
                        text.Append("エディタの画面に開いているこの文書の中身は、ファイルが外で書き換わる前のまま: ").Append(document.FilePath)
                            .Append("（「エディタの画面（参考）」の行番号は書き換わる前のもの。話題の「見ているページ」「カーソル」「まず読む範囲」は、ファイルから読み直した今のもの）\n");
                    }
                    else if (stale != null)
                        text.Append("ファイルが外で書き換わったのに、エディタの中身は書き換わる前のままの文書: ").Append(document.FilePath)
                            .Append("（今の状態に書いたこの文書のページの範囲・行番号は、書き換わる前のもの。`<!--page: ID-->` の行を Grep で探して範囲を決め直す。読み直していないわけ: ")
                            .Append(stale).Append("）\n");
                    continue;
                }

                string unclosed = null;
                try
                {
                    unclosed = document.UnclosedFence();
                }
                catch (Exception)
                {
                }

                text.Append("エディタに保存できていない編集がある文書: ").Append(document.FilePath).Append("（この文書は書き換えない。");
                if (unclosed != null)
                    text.Append("この文書には閉じていないコードブロックがあるため、エディタが保存を断っている。利用者がそのコードブロックを閉じるまで保存できないので、「保存して」とは頼まず、閉じるよう伝える。エディタの知らせ: ")
                        .Append("「").Append(unclosed.TrimEnd('。')).Append("」。");
                else
                    text.Append("先にエディタで保存してもらうよう利用者に頼む。");
                text.Append("ファイルの中身はエディタの画面より古く、今の状態に書いたこの文書の行番号もファイルと合わない）\n");
            }
        }

        /// <summary>未保存の編集は無いのに、ファイルが外で書き換わっていて、読み直すと失敗するなら、そのわけ。読み直せる・変わっていないなら null。</summary>
        private static string StaleReason(PageDocument document)
        {
            try
            {
                if (!document.IsChangedOnDisk || !File.Exists(document.FilePath)) return null;
                PageDocument.Load(document.FilePath);
                return "まだ読み直していない（ほかの画面の文書か、ダイアログを出していた。その画面に移ったときなどに読み直す）";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 中身を ``` で囲む（2026-11-22）。中に ``` があると囲みが途中で切れるので、中身のいちばん長い ` の並びより1つ長くする。
        /// </summary>
        private static string Fenced(string content)
        {
            var longest = 0;
            var run = 0;
            foreach (var c in content)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > longest) longest = run;
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return fence + "\n" + content + "\n" + fence + "\n";
        }

        /// <summary>
        /// 見ている文書のほかに開いている文書を、今の状態に足す（2026-11-13）。この画面で右に開いた別の文書・ほかの画面の文書。
        /// 画面 B の AI の欄で「A の文書について」と話しても通じるように。場所と、その画面で見ているページ、保存できていない編集があるかを書く。
        /// AI に渡さない場所（AiGuard）の文書は書かない（あることも書かない）。
        /// </summary>
        /// <param name="current">見ている文書。無ければ null。</param>
        private void AppendOtherDocuments(StringBuilder text, PageDocument current)
        {
            var lines = new List<string>();
            var listed = new List<string>();
            if (current != null && !string.IsNullOrEmpty(current.FilePath)) listed.Add(current.FilePath);

            Func<string, bool> fresh = path =>
            {
                if (string.IsNullOrEmpty(path) || AiGuard.IsGuarded(path)) return false;
                if (listed.Any(seen => SamePath(seen, path))) return false;
                listed.Add(path);
                return true;
            };

            for (var i = 0; i < OpenWindows.Count; i++)
            {
                var window = OpenWindows[i];
                // タブの名前は一番左の文書の題。それが AI に渡さない文書なら出さない（2026-11-15）
                var where = ReferenceEquals(window, this) ? "この画面"
                    : window._stack.Count > 0 && AiGuard.IsGuarded(window._stack[0].FilePath) ? "画面 " + (i + 1)
                    : "画面 " + (i + 1) + "（タブ「" + window.WindowLabel(false) + "」）";
                var shown = window.Active;
                foreach (var group in window._stack.Where(view => view.Owner != null).GroupBy(view => view.Owner))
                {
                    var first = group.First();
                    if (!fresh(first.FilePath)) continue;

                    var line = new StringBuilder("- ").Append(where).Append(": ");
                    var link = window.WordPressLinkOf(first);
                    line.Append(link != null ? "WordPress の記事「" + first.Title + "」の一時ファイル " : string.Empty).Append(first.FilePath);
                    if (shown != null && ReferenceEquals(shown.Owner, group.Key))
                        line.Append("（その画面で見ているページ: ").Append(PageLabel(shown)).Append("）");
                    if (group.Any(view => view.IsDirty))
                        line.Append("（エディタに保存できていない編集がある。書き換えない）");
                    lines.Add(line.ToString());
                }
            }

            if (lines.Count == 0) return;
            text.Append("ほかに開いている文書（利用者が名前やタブの名前で指したら、これのこと。中身はファイルを読む）:\n");
            foreach (var line in lines) text.Append(line).Append('\n');
        }

        /// <summary>今の状態の「まず読む範囲」で、カーソルの前後に何行ずつ取るか。ページがこの2倍より短ければページ全体。</summary>
        private const int ReadAroundLines = 100;

        private static string PageLabel(MarkdownView view)
        {
            var name = string.IsNullOrEmpty(view.PageId) ? "ルート" : view.PageId;
            return name + "「" + view.Title + "」" + (view.IsReadOnly ? "（ファイルから消えたページ）" : string.Empty);
        }

        /// <summary>
        /// 外で書き換わった文書を読み直す。フックの知らせと、エディタに戻ったとき（Activated）。
        /// 未保存の編集がある文書は読み直さない（読み直すとその編集が消える）。そのときは、保存の確かめ（外部変更で止まる）に任せて知らせる。
        /// 読み直して無くなったページを開いていれば、そのページから右を閉じる。
        /// </summary>
        private void ReloadChangedDocuments()
        {
            // ダイアログを出している間は読み直さない（2026-10-24）。ダイアログの最中にも知らせ（フック・Activated）は届くが、
            // そこで画面の並びや中身を変えると、ダイアログを出した操作が古い前提のまま進む
            // （開く位置がずれる・確認した一覧と違うページを消す等）。このアプリのダイアログはどれもこの窓が親なので、
            // 出ている間は Windows がこの窓を操作できなくする。それで見分ける。閉じてこの窓に戻ったとき（Activated）に読み直す。
            if (IsShowingDialog()) return;

            if (_calendar != null) _calendar.ReloadIfChanged();   // Claude が予定を書き換えたら出し直す（2026-11-21）
            if (_launcher != null && _filer != null) _filer.ReloadIfChanged();   // ランチャーのファイラーの一覧も、フォルダの中身が変わっていれば読み直す（2026-11-24）
            if (_launcher != null) RefreshExcelListSoon();   // ランチャーの Excel の一覧も（2026-11-24。MainWindow.Excel.cs）。続けて来る分は 1.5 秒まとめる（2026-11-26 のレビュー）
            foreach (var view in _stack) view.RecheckImages();      // 画像は、ここでだけ確かめ直す（スクロールでは確かめない。2026-11-23）
            ReloadDrawingIfChanged();   // 図エディタで開いている図を Claude などが書き換えたら、図エディタに読み込む（2026-11-23）

            var documents = _stack.Select(view => view.Owner).Where(owner => owner != null).Distinct().ToList();
            var reloaded = new List<PageDocument>();

            foreach (var document in documents)
            {
                bool changed;
                try
                {
                    changed = document.IsChangedOnDisk && File.Exists(document.FilePath);
                }
                catch (Exception)
                {
                    continue;   // 共有が一瞬見えない等。次のきっかけで確かめ直す
                }

                if (!changed) continue;

                if (document.IsDirty)
                {
                    Status("外で書き換えられましたが、未保存の編集があるため読み直していません: " + Path.GetFileName(document.FilePath));
                    continue;
                }

                try
                {
                    document.Reload();
                    reloaded.Add(document);
                }
                catch (Exception ex)
                {
                    // 書いている途中を読んだ等。今の中身のまま、次のきっかけで読み直す。
                    Status("外での書き換えを読み直せませんでした: " + ex.Message);
                }
            }

            if (reloaded.Count == 0) return;

            // 無くなったページから右は閉じる。ルートはいつもあるので、先頭は残る。
            // 右には別のファイルのページもありうる。閉じるとそのファイルの未保存の編集が消えるので、そこにしか無い文書に
            // 未保存の編集があれば閉じない（KeepClosingDocuments。保存はしない）。2026-10-24 に、保存せずに閉じて別のファイルの編集が黙って消えたため。
            // 前は閉じる前に保存していたが、2026-10-25 に自動保存をやめた。
            var lost = _stack.FindIndex(view => reloaded.Contains(view.Owner) && !view.Owner.Contains(view.PageId));
            if (lost > 0 && KeepClosingDocuments(lost))
            {
                while (_stack.Count > lost) _stack.RemoveAt(_stack.Count - 1);
                if (!_stack.Contains(_focused)) _focused = _stack[_stack.Count - 1];
            }
            else
            {
                lost = -1;   // 閉じない。無くなったページは、打てば行き先の無いページの扱いになる（別名で書き出す）
            }

            foreach (var view in _stack)
            {
                if (reloaded.Contains(view.Owner)) view.ReloadFromOwner();
            }

            if (lost > 0) RebuildStack(false);

            // 見出しの構成が同じなら、アウトラインの木は作り直さず行番号だけ合わせる（打ったときと同じ。2026-11-11）。
            // 前は読み直すたびに作り直していて、2万行・見出し千あまりの文書を Claude が書き換えるたびに、木を丸ごと作り直していた
            // （隠れた画面で起きると、次に前に出したときに配置と描画をまとめて払い、1 秒ほど止まって GC が 50 回走った）。
            RefreshOutline();
            UpdateChrome();

            Status("外での書き換えを読み直しました: " + string.Join("、", reloaded.Select(d => Path.GetFileName(d.FilePath)).ToArray()));
        }

        /// <summary>
        /// スタックの from から右を閉じてよいか。そこにしか無い文書に未保存の編集があれば閉じない（false）。
        /// 外からの知らせで動くので、尋ねもせず、勝手に保存もしない（2026-10-25。自動保存をやめた）。
        /// 閉じなかった無くなったページは、打てば行き先の無いページの扱いになる（別名で書き出す）。
        /// </summary>
        private bool KeepClosingDocuments(int from)
        {
            var staying = _stack.Take(from).Select(view => view.Owner).ToList();
            var unsaved = _stack.Skip(from).Select(view => view.Owner)
                .FirstOrDefault(owner => owner != null && owner.IsDirty && !staying.Contains(owner));
            if (unsaved == null) return true;

            Status("無くなったページを閉じると、保存していない「" + Path.GetFileName(unsaved.FilePath) + "」の編集も閉じるので、閉じずに残しました。");
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);
    }
}

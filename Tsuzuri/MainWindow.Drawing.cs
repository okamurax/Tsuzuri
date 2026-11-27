using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 図エディタを画面の中に重ねて出す（2026-11-23。SPEC 8.4.3。利用者の考え）。
    //
    // 前は本体と同じ位置・大きさの別の窓（ShowDialog）で、閉じるまで本体の AI の欄を使えなかった。
    // 今はアウトラインと本文をまとめて覆う部品（DrawingHost）で、右の AI の欄は見えたまま使える。
    // ・Claude に話しかけた時点で、図エディタの今の図を .json（と .png）に書いてから渡す（FlushDrawingForAi）。Claude はファイルを読んで書き換える
    // ・Claude などがその .json を書き換えたら、図エディタに読み込む（ReloadDrawingIfChanged）。元に戻すの1段になり、Ctrl+Z で戻せる
    // ・保存せずに閉じたら、開く前の .json・.png に戻す（開いたときに中身を取っておく。Claude が書き換えた分も戻す。新しい図なら書いたファイルを消す）
    // ・同時に開ける図は画面ごとに1つ。同じ図をほかの画面で開いていれば開かない
    // ・画面のタブを切り替えても図エディタはその画面に残り、戻ると続きが出る（画面ごとに MainWindow が別なので、そのまま）
    // ・出している間、カレンダーは出せない（同じように覆うのでかぶる）。ランチャーは上に重ねて出せる
    public partial class MainWindow
    {
        /// <summary>図エディタで開いている図。</summary>
        private sealed class DrawingSession
        {
            public DrawingEditor.DrawingEditorView View;

            /// <summary>図を置くフォルダ（文書の添付フォルダ）と名前（拡張子なし）。</summary>
            public string Folder;
            public string Stem;

            /// <summary>新しく描く図（保存したら、Target のカーソルの位置にリンクを入れる）。</summary>
            public bool IsNew;
            public MarkdownView Target;

            /// <summary>開く前の .json・.png の中身（保存せずに閉じたら戻す）。PngExisted が false なら .png は無かった。</summary>
            public byte[] OriginalJson;
            public byte[] OriginalPng;
            public bool PngExisted;
            public DateTime OriginalStamp;

            /// <summary>最後に自分で書いた・読み込んだときの .json の更新時刻と中身（それと違えば、ほかで書き換わった）。</summary>
            public DateTime KnownStamp;
            public string KnownJson;

            /// <summary>
            /// ほかで書き換わったのに図エディタに読み込めなかった .json の更新時刻（無ければ MinValue）。ファイルがこのままの間は、図エディタの図で黙って上書きしない
            /// （AI に渡すための書き出しはしない。保存・保存せずに閉じるときは尋ねる。2026-11-27 のレビュー。前は「読んだ」ことにして、次の書き出しで古い図を書き戻した）。
            /// </summary>
            public DateTime UnreadableStamp = DateTime.MinValue;
            public string UnreadableReason;

            /// <summary>開いてから、AI に渡すために .json を書いた。</summary>
            public bool Wrote;

            /// <summary>今回の頼みの前に、図エディタの今の図をファイルに書けた（書かなくても同じだった）か。今の状態の書き方を変える。</summary>
            public bool FileIsCurrent;

            public string JsonPath
            {
                get { return Path.Combine(Folder, Stem + ".json"); }
            }

            public string PngPath
            {
                get { return Path.Combine(Folder, Stem + ".png"); }
            }
        }

        private DrawingSession _drawing;

        /// <summary>図エディタを出しているか。出していれば知らせ、図エディタへキーを戻して true（もう1つは開かない）。</summary>
        private bool DrawingBusy()
        {
            if (_drawing == null) return false;
            Status("図エディタで「" + _drawing.Stem + "」を開いています。保存するか終了してから、ほかの図を開いてください");
            _drawing.View.FocusCanvas();
            return true;
        }

        /// <summary>
        /// 図エディタを出す。json は描き直す図（新しく描くなら null）。target は新しい図のリンクを入れるページ。
        /// 開けない図（新しい版の図・ほかの画面で開いている）は知らせて開かない。
        /// </summary>
        private void OpenDrawingEditor(string folder, string stem, string json, bool isNew, MarkdownView target)
        {
            if (DrawingBusy()) return;

            var session = new DrawingSession { Folder = folder, Stem = stem, IsNew = isNew, Target = target };
            var other = OpenWindows.FirstOrDefault(window => window != this && window._drawing != null && SamePath(window._drawing.JsonPath, session.JsonPath));
            if (other != null)
            {
                Status("この図は、ほかの画面の図エディタで開いています: " + stem);
                return;
            }

            string refusal;
            var view = DrawingEditor.DrawingEditorView.Create(json, out refusal);
            if (view == null)
            {
                MessageBox.Show(this, refusal, "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 開く前のファイルを取っておく（保存せずに閉じたら戻す）。読めなければ開かない（戻せなくなるため）
            if (!isNew)
            {
                try
                {
                    session.OriginalStamp = LastWriteOf(session.JsonPath);
                    session.OriginalJson = File.ReadAllBytes(session.JsonPath);
                    session.PngExisted = File.Exists(session.PngPath);
                    if (session.PngExisted) session.OriginalPng = File.ReadAllBytes(session.PngPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "図のファイルを読めませんでした。図は変えていません。" + Environment.NewLine + Environment.NewLine + ex.Message,
                        "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                session.KnownStamp = session.OriginalStamp;
                session.KnownJson = json;
            }
            else
            {
                session.KnownStamp = DateTime.MinValue;   // まだファイルは無い
            }

            // 覆うので、ランチャー・カレンダーは閉じる（カレンダーで打ちかけの予定を保存できなければ、開かない）
            CloseLauncher();
            if (!CloseCalendar()) return;

            session.View = view;
            view.Saving = result => SaveDrawing(session, result);
            view.Finished += delegate { EndDrawing(session); };
            _drawing = session;
            DrawingHost.Child = view;
            DrawingHost.Visibility = Visibility.Visible;
            PaintSideTabs();   // 図エディタが見えている間は、つまみを隠す
            Status("図エディタ: " + stem + "（右の AI の欄はそのまま使えます。保存か終了で本文に戻ります）");

            // 覆われた本文に打ち込まないよう、キー入力を図エディタへ移す（ランチャー・カレンダーと同じ。組み版のあとで）
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_drawing == session) view.FocusCanvas();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 図エディタの「保存」。書けたら true（図エディタを閉じる）。
        /// 書く前に、図のファイルがほかで（AI など）書き換わっていれば、それを図エディタに読み込んで知らせ、保存しない（黙って上書きすると、その書き換えが消える）。
        /// </summary>
        private bool SaveDrawing(DrawingSession session, DrawingEditor.DrawingResult result)
        {
            if (session != _drawing) return false;
            if (ReloadDrawingIfChanged())
            {
                MessageBox.Show(this,
                    "保存する前に、この図のファイルがほかで（AI など）書き換わっていたので、図エディタに読み込みました。" + Environment.NewLine +
                    "読み込む前の図には Ctrl+Z で戻せます。確かめてから、もう一度保存してください。",
                    "図エディタ", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            // 読めない書き換えのまま: 上書きしてよいか尋ねる（新しい図で、まだファイルを知らないときは尋ねない。下の WriteDrawing が同じ名前を上書きせずに知らせる）
            if (DrawingFileUnreadable(session) && !(session.IsNew && session.KnownStamp == DateTime.MinValue))
            {
                var stamp = session.UnreadableStamp;
                if (MessageBox.Show(this,
                        "この図のファイルは、ほかで（AI など）書き換わっていて、図エディタに読み込めません（" + session.UnreadableReason + "）。" + Environment.NewLine +
                        "図エディタの図で上書きすると、その書き換えは消えます。上書きして保存しますか？",
                        "図エディタ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return false;
                // 尋ねている間にまた書き換わっていたら（AI が直したなど）、上書きせずに読み込みからやり直す
                if (LastWriteOf(session.JsonPath) != stamp) return SaveDrawing(session, result);
            }

            // 新しい図で、まだ自分で書いても読み込んでもいなければ（ファイルを知らない）、同じ名前が先にできていても上書きしない
            if (!WriteDrawing(session.Folder, session.Stem, result, session.IsNew && session.KnownStamp == DateTime.MinValue)) return false;

            foreach (var view in _stack) view.RefreshImages();   // 同じ図を見せているページすべてで、画像を読み直す

            if (!session.IsNew)
            {
                Status("図を保存しました: " + session.Stem + ".png");
                return true;
            }

            var target = session.Target != null && _stack.Contains(session.Target) ? session.Target : Active;
            if (target != null && CanInsertInto(target))
            {
                target.InsertAtCaret("![" + EscapeLabel(session.Stem) + "](" + ToLinkTarget(session.Stem + ".png") + ")");
                Status("図を入れました: " + Path.GetFileName(session.Folder) + "/" + session.Stem + ".png");
            }
            else
            {
                Status("図は保存しましたが、本文にリンクを入れられませんでした: " + session.PngPath);
            }
            return true;
        }

        /// <summary>図エディタが閉じた。保存しなかったなら、開く前のファイルに戻す。図エディタを外し、キーを本文へ戻す。</summary>
        private void EndDrawing(DrawingSession session)
        {
            if (session != _drawing) return;

            if (!session.View.WasSaved && RestoreDrawingFiles(session))
                foreach (var view in _stack) view.RefreshImages();

            var hadFocus = DrawingHost.IsKeyboardFocusWithin;
            _drawing = null;
            DrawingHost.Child = null;
            DrawingHost.Visibility = Visibility.Collapsed;
            PaintSideTabs();
            if (hadFocus && Active != null) Active.TextArea.Focus();
        }

        /// <summary>
        /// 保存せずに閉じたとき、開いてから書き換わった .json・.png を開く前に戻す（AI に渡すために書いた分・Claude が書き換えた分）。
        /// 新しい図なら、書いたファイルを消す。戻したら true。戻せなければ知らせる。
        /// </summary>
        private bool RestoreDrawingFiles(DrawingSession session)
        {
            try
            {
                if (session.IsNew)
                {
                    var any = false;
                    foreach (var path in new[] { session.JsonPath, session.PngPath })
                    {
                        if (!File.Exists(path)) continue;
                        File.Delete(path);
                        any = true;
                    }
                    if (any) Status("保存しなかったので、AI に渡すために書いた図を消しました: " + session.Stem);
                    return any;
                }

                if (!session.Wrote && LastWriteOf(session.JsonPath) == session.OriginalStamp) return false;   // 開いてから誰も書いていない
                if (DrawingFileUnreadable(session)
                    && MessageBox.Show(this,
                        "この図のファイルは、ほかで（AI など）書き換わっていて、図エディタに読み込めません（" + session.UnreadableReason + "）。" + Environment.NewLine +
                        "保存せずに閉じるので図を開く前に戻しますか？ 戻すと、その書き換えは消えます（「いいえ」なら .json は書き換わったまま残し、画像だけ開く前に戻します）。",
                        "図エディタ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    // 画像は、保存しなかった手直しの図かもしれないので開く前に戻す（本文に捨てた図が出ないように）
                    RestoreDrawingPng(session);
                    Status("保存しなかったので画像を開く前に戻しました。図のファイル（.json）は、ほかで書き換わったまま残しました: " + session.Stem);
                    return true;
                }

                var temp = session.JsonPath + ".saving";
                WriteDrawingTemp(temp, session.OriginalJson);
                ReplaceDrawingFile(temp, session.JsonPath, false);
                RestoreDrawingPng(session);
                Status("保存しなかったので、図を開く前に戻しました（AI が書き換えた分も）: " + session.Stem);
                return true;
            }
            catch (Exception ex)
            {
                TryDeleteDrawingTemp(session.JsonPath + ".saving");
                TryDeleteDrawingTemp(session.PngPath + ".saving");
                MessageBox.Show(this, "図を開く前の形に戻せませんでした。ファイルは AI に渡した・AI が書き換えたときのままです。" + Environment.NewLine + Environment.NewLine +
                    session.JsonPath + Environment.NewLine + ex.Message, "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>
        /// 図のファイルがほかで（AI の欄の Claude など）書き換わっていたら、図エディタに読み込む（元に戻すの1段になる）。読み込んだら true。
        /// 外で変わった文書を読み直すとき（窓が前に出た・Claude が書いた）と、保存の前に呼ぶ。
        /// </summary>
        private bool ReloadDrawingIfChanged()
        {
            var session = _drawing;
            if (session == null) return false;

            var stamp = LastWriteOf(session.JsonPath);
            if (stamp == session.KnownStamp || stamp == DateTime.MinValue) return false;   // 変わっていない・まだ無い（消えたなら、保存で書き直す）
            if (stamp == session.UnreadableStamp) return false;   // 読めないと分かっている書き換えのまま（知らせ直さない）

            string json;
            try
            {
                json = File.ReadAllText(session.JsonPath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return false;   // 書いている最中など。次のきっかけで見る
            }

            if (json == session.KnownJson)
            {
                session.KnownStamp = stamp;
                return false;
            }

            string error;
            if (!session.View.Reload(json, out error))
            {
                // 「読んだ」ことにしない（KnownStamp・KnownJson を変えない）。このままの間は、図エディタの図で上書きしない
                session.UnreadableStamp = stamp;
                session.UnreadableReason = error;
                Status("図のファイルがほかで書き換わりましたが、図エディタに読み込めませんでした（図エディタの図はそのまま。ファイルは上書きしません）: " + error);
                return false;
            }
            session.KnownStamp = stamp;
            session.UnreadableStamp = DateTime.MinValue;

            session.KnownJson = session.View.CurrentJson();   // 図エディタの書き方にそろえる（同じ図を次の頼みで書き直さない。ファイルと比べるのは更新時刻で）
            Status("図のファイルがほかで（AI など）書き換わったので、図エディタに読み込みました。Ctrl+Z で読み込む前に戻せます: " + session.Stem);
            return true;
        }

        /// <summary>画像（.png）を開く前に戻す（無かったなら消す）。</summary>
        private void RestoreDrawingPng(DrawingSession session)
        {
            if (session.PngExisted)
            {
                var temp = session.PngPath + ".saving";
                WriteDrawingTemp(temp, session.OriginalPng);
                ReplaceDrawingFile(temp, session.PngPath, false);
            }
            else if (File.Exists(session.PngPath))
            {
                File.Delete(session.PngPath);
            }
        }

        /// <summary>図のファイルが、読み込めなかった書き換えのままか。</summary>
        private static bool DrawingFileUnreadable(DrawingSession session)
        {
            return session.UnreadableStamp != DateTime.MinValue && LastWriteOf(session.JsonPath) == session.UnreadableStamp;
        }

        /// <summary>
        /// Claude に話しかけたとき、図エディタの今の図を .json と .png に書く（Claude はファイルを読むので。2026-11-23）。
        /// 書く前に、ほかでの書き換えがあれば読み込む。書けなかったら、AI に伝える断りを返す（書けた・図エディタを出していなければ null）。
        /// </summary>
        private string FlushDrawingForAi()
        {
            var session = _drawing;
            if (session == null) return null;

            session.FileIsCurrent = false;
            if (AiGuard.IsGuarded(session.JsonPath)) return null;   // AI に渡さない場所の図。Claude は読まないので書かない（知らせに場所も載せない）
            ReloadDrawingIfChanged();
            if (DrawingFileUnreadable(session))
                return "図のファイルはほかで書き換わっていて、図エディタに読み込めなかった（" + session.UnreadableReason + "）。図エディタに見えている図はファイルと違い、ファイルには書き戻していない（ファイルは書き換わったまま）。図を直す前に利用者に確かめる。";
            var json = session.View.CurrentJson();
            if (json == session.KnownJson && File.Exists(session.JsonPath))   // ファイルと同じ
            {
                session.FileIsCurrent = true;
                return null;
            }

            try
            {
                var temp = session.JsonPath + ".saving";
                WriteDrawingTemp(temp, new UTF8Encoding(false).GetBytes(json));
                ReplaceDrawingFile(temp, session.JsonPath, false);
                session.Wrote = true;
                session.KnownJson = json;
                session.KnownStamp = LastWriteOf(session.JsonPath);
                session.FileIsCurrent = true;
            }
            catch (Exception ex)
            {
                TryDeleteDrawingTemp(session.JsonPath + ".saving");
                Status("図エディタの図を AI に渡すために書けませんでした: " + ex.Message);
                return File.Exists(session.JsonPath)
                    ? "図エディタの今の図をファイルに書けなかった。図のファイルは、図エディタに見えている図より古い。"
                    : "図エディタの今の図をファイルに書けなかった。図のファイルはまだ無い。";
            }

            // 画像も合わせる（本文の図・Claude が画像を見るとき）。作れなくても .json は書けている（Claude が書けば作り直す）
            try
            {
                var png = session.View.RenderCurrentPng();
                var temp = session.PngPath + ".saving";
                WriteDrawingTemp(temp, png);
                ReplaceDrawingFile(temp, session.PngPath, false);
            }
            catch (Exception)
            {
                TryDeleteDrawingTemp(session.PngPath + ".saving");
            }
            return null;
        }

        /// <summary>
        /// AI に渡す今の状態の「図エディタ」の段（2026-11-23）。図エディタを出していなければ何も書かない。
        /// AI に渡さない場所の図なら、場所も中身も書かない。
        /// </summary>
        private void AppendDrawingContext(StringBuilder text, bool pinned, bool unknownTab)
        {
            var session = _drawing;
            if (session == null) return;

            text.Append("図エディタ: 開いている（エディタの画面のアウトラインと本文を覆っている。利用者に何が見えているかは「見えているもの」の段）");
            if (pinned)
                text.Append("。この会話の話題ではなくエディタの画面の側のもの。利用者が「図エディタ」「図エディタの図」「図エディタで選んだ図形」とはっきり言ったときだけ、これを指す（「図」だけで分からなければ尋ねる。「図エディタで描いて」が形の指定か、開いている図のことか分からなければ尋ねる）。")
                    .Append(_launcher != null ? "ただし「選んだ図形」は図エディタで選んでいる図形。上にランチャーを重ねている間の「選んだところ」「選んだもの」は、何を指すかを尋ねる\n"
                                              : "ただし「選んだところ」「選んだもの」「選んだ図形」は、図エディタで選んでいる図形\n");
            else if (unknownTab)
                text.Append("。どの会話からの頼みか分からないので、図の頼みがこの図を指すかを利用者に確かめてから扱う\n");
            else
                text.Append('\n');
            if (AiGuard.IsGuarded(session.JsonPath))
            {
                text.Append("  図は、利用者が AI に渡さないと決めた場所にある。場所も中身も渡していない。尋ねたり探したりしない\n");
                return;
            }

            text.Append("  図のファイル: ").Append(session.JsonPath).Append("（画像は同じ名前の .png。Tsuzuri が作る）\n");
            if (DrawingFileUnreadable(session))
                text.Append("  今回はこのファイルに図エディタの今の図を書けていない（このファイルはほかで書き換わっていて、図エディタに読み込めなかった（").Append(session.UnreadableReason).Append("）ので、書き戻していない）。ファイルのほうが新しく、図エディタに見えている図とは違う。直す前に利用者に確かめる\n");
            else if (session.FileIsCurrent)
                text.Append("  このファイルには、図エディタの今の図（利用者がまだ保存していない手直しも）が書いてある。直すときは、このファイルを読んでから書き換える\n");
            else if (File.Exists(session.JsonPath))
                text.Append("  今回はこのファイルに図エディタの今の図を書けていない。ファイルの図は、図エディタに見えている図より古いかもしれない。直す前に利用者に確かめる\n");
            else
                text.Append("  今回はこのファイルに図エディタの今の図を書けていない（ファイルはまだ無い）。直す前に利用者に確かめる\n");
            if (session.IsNew)
                text.Append("  これは新しく描いている図で、まだ本文にリンクしていない。利用者が図エディタで保存すると Tsuzuri が本文にリンクを入れるので、リンクは書かない\n");

            var ids = session.View.SelectedIds;
            if (ids.Count > 0)
                text.Append("  図エディタで選んでいる図形の id: ").Append(string.Join(", ", ids)).Append('\n');
            else
                text.Append("  図エディタで選んでいる図形: なし\n");
        }
    }
}

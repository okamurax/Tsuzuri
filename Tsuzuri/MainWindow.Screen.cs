using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 画面の状態を覚える・戻す（SPEC 4章・7章。2026-10-25）。
    //
    // 終了したときと同じ画面で起動し、ブックマークを開いたときも登録したときと同じ画面にする。
    // 覚えるのは、開いていたページの並び（別のファイルのページも）と、それぞれのカーソル・スクロール・検索欄、
    // フォーカスのあったページ、ランチャー、AI の欄（ScreenState）。
    // 前は、起動ではルートのページとカーソルの行だけ、ブックマークではいちばん右のページと全幅だけを戻していた。
    public partial class MainWindow
    {
        /// <summary>RestoreScreen の結果。戻した。</summary>
        private const int ScreenRestored = 1;

        /// <summary>RestoreScreen の結果。戻すページが無い・開けなかった（呼び出し側の今までの開き方に任せる）。</summary>
        private const int ScreenNotApplicable = 0;

        /// <summary>RestoreScreen の結果。保存するか尋ねて「キャンセル」された（何もしない）。</summary>
        private const int ScreenCancelled = -1;

        /// <summary>
        /// RestoreScreen の結果。左端（ルート）のファイルを開こうとして、開けないと知らせた・フォルダを作るのを断られた（2026-10-27）。
        /// 戻せなかったことは ScreenNotApplicable と同じだが、起動のときは前回のファイルを開き直さない（同じことを2度尋ねない）。
        /// </summary>
        private const int ScreenOpenFailed = -2;

        /// <summary>今の画面の状態。</summary>
        private ScreenState CaptureScreen()
        {
            var screen = new ScreenState
            {
                Pages = new List<PageState>(),
                Focus = Math.Max(0, _stack.IndexOf(Active)),
                Launcher = _launcher != null,
                Ai = AiPane.Visibility == Visibility.Visible,
            };

            foreach (var view in _stack)
            {
                // ファイルから消えたページから右は、戻しても開けないので覚えない。
                if (string.IsNullOrEmpty(view.FilePath) || view.IsReadOnly) break;

                screen.Pages.Add(new PageState
                {
                    File = view.FilePath,
                    Page = view.PageId ?? string.Empty,
                    Line = view.TextArea.Caret.Line,
                    Column = view.TextArea.Caret.Column,
                    Scroll = view.VerticalOffset,
                    Search = string.IsNullOrEmpty(view.SearchText) ? null : view.SearchText,
                });
            }

            return screen;
        }

        /// <summary>
        /// 画面をその状態に戻す。開いていないファイルは開く。ページが無くなっていれば、そこから右は戻さない。
        /// confirm なら、画面から外れるファイルに未保存の編集があるとき保存するか尋ねる（ブックマークを開くとき）。
        /// ランチャーと AI の欄も、覚えたとおりに開け閉めする。
        /// requiredFile を渡したら、戻した並びにそのファイルが無いときは戻さない（ブックマークのファイル。2026-10-27）。
        ///
        /// 2026-10-27 に順番を変えた。前は先に「覚えた並びにあるファイル」を残るものとして尋ね、そのあと開けない・ページが無いところで
        /// 止めていたので、止めた所より右のファイルの未保存の編集が、尋ねられないまま捨てられた。また、尋ねたあとで「戻せない」になると、
        /// 呼び出し側の開き方でもう一度尋ねていた。今は、先に戻す並びを決め（スタックには触らない）、戻すと決まってから、
        /// 本当に残る文書のページだけを残るものとして尋ねる。
        /// </summary>
        private int RestoreScreen(ScreenState screen, bool confirm, string requiredFile = null)
        {
            if (screen == null || screen.Pages == null || screen.Pages.Count == 0) return ScreenNotApplicable;

            // 画面に出すのはいちばん右のページだけ（SPEC 6章）。左右2画面だった前の版で覚えたものは、
            // フォーカスのあったページより右を捨てて、そのページを出す。並びを決める前に切る。
            var saved = screen.Focus >= 0 && screen.Focus < screen.Pages.Count - 1
                ? screen.Pages.Take(screen.Focus + 1).ToList()
                : screen.Pages;

            // 文書をそろえる。開いていなければ開く（スタックには触らない）。開けない・ページが無いところで止める（そこから右は戻さない）。
            // ここで新しく開いたファイルの状態（state.json）は、戻すと決まるまで手元に置かない（AdoptState しない）。
            // 戻さなかったとき・並びに入らなかったときに、使わない状態やブックマークを残さないため。
            var pages = new List<PageState>();
            var documents = new List<PageDocument>();
            var opened = new Dictionary<PageDocument, DocumentState>();
            var openFailed = false;

            foreach (var page in saved)
            {
                if (page == null || string.IsNullOrEmpty(page.File)) break;

                var path = ShellActions.CanonicalPath(page.File);
                var document = documents.FirstOrDefault(open => SamePath(open.FilePath, path)) ?? FindOpenDocument(path);
                if (document == null)
                {
                    if (!File.Exists(path)) break;

                    // 開けなければ OpenFile が知らせている（フォルダを作るのを断られたときも）。
                    DocumentState state;
                    if (!OpenFile(path, out document, out state))
                    {
                        openFailed = true;
                        break;
                    }

                    opened[document] = state;
                }

                if (!document.Contains(page.Page ?? string.Empty)) break;

                pages.Add(page);
                documents.Add(document);
            }

            // 左端はいつもファイルのルート。そうでなければ戻せない。
            // 左端のファイルを開こうとして知らせた・断られたなら、呼び出し側でもう一度開こうとしない（起動のとき）。
            if (pages.Count == 0) return openFailed ? ScreenOpenFailed : ScreenNotApplicable;
            if (!string.IsNullOrEmpty(pages[0].Page)) return ScreenNotApplicable;

            if (requiredFile != null && !documents.Any(document => SamePath(document.FilePath, requiredFile))) return ScreenNotApplicable;

            // 戻すと決まってから尋ねる。戻したあとも画面に残るのは、戻す並びにある文書のページだけ。
            if (confirm && !ConfirmLeaving(_stack.Where(view => view.Owner != null && documents.Contains(view.Owner)), null))
                return ScreenCancelled;

            // 尋ねている間に、外での書き換えの読み直し（Activated）でページが無くなったら、戻すのをやめる。
            // そこから右を切ると、尋ねたときに残るはずだった文書が画面から外れ、その未保存の編集が尋ねられないまま捨てられるため。
            // まだ何も閉じていないので、「保存しない」を選んでいても編集は画面に残る。
            for (var i = 0; i < pages.Count; i++)
            {
                if (documents[i].Contains(pages[i].Page ?? string.Empty)) continue;

                Status("画面を戻している間にファイルが書き換えられたため、戻すのをやめました。");
                return ScreenCancelled;
            }

            // 新しく開いた文書のうち、戻す並びに入ったものの状態だけを手元に置く。
            foreach (var entry in opened)
            {
                if (documents.Contains(entry.Key)) AdoptState(entry.Key.FilePath, entry.Value);
            }

            // 今の画面に同じページがあれば使い回す（元に戻すの履歴が残る）。無ければ作る。
            var spare = _stack.ToList();
            var views = new List<MarkdownView>();
            for (var i = 0; i < pages.Count; i++)
            {
                var reuse = spare.FirstOrDefault(view => IsSamePage(view, documents[i], pages[i].Page));
                if (reuse != null) spare.Remove(reuse);
                views.Add(reuse ?? NewView(documents[i], pages[i].Page));
            }

            StoreHeadingBookmarks();
            RememberFileStates();

            _stack.Clear();
            _stack.AddRange(views);
            _focused = views[views.Count - 1];

            for (var i = 0; i < views.Count; i++) views[i].SearchText = pages[i].Search;

            _settings.LastFile = views[0].FilePath;

            RebuildStack(false);
            AfterStackChanged();

            // カーソルとスクロールは、並べ終わってから置く（先だとスクロールが効かない）。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                for (var i = 0; i < views.Count; i++)
                {
                    if (_stack.Contains(views[i])) views[i].RestorePosition(pages[i].Line, pages[i].Column, pages[i].Scroll);
                }
            }), DispatcherPriority.Loaded);

            RestorePanels(screen);
            return ScreenRestored;
        }

        /// <summary>ランチャーと AI の欄を、覚えたとおりに開け閉めする。</summary>
        private void RestorePanels(ScreenState screen)
        {
            if (screen == null) return;

            if (screen.Launcher && _launcher == null) ToggleLauncher();
            else if (!screen.Launcher && _launcher != null) CloseLauncher();

            var aiShown = AiPane.Visibility == Visibility.Visible;
            if (screen.Ai != aiShown) ToggleAi();
        }

        /// <summary>
        /// 起動したとき、終了したときの画面に戻す（OpenLastFile から）。戻せなければ false（前のファイルのルートを開く）。
        /// 開いていたページが無くても、ランチャーと AI の欄は戻す。
        /// 左端のファイルを開こうとして、開けないと知らせた・フォルダを作るのを断られたときも true にして、前のファイルを開き直させない
        /// （2026-10-27。前は同じファイルについてもう一度尋ね・知らせていた）。
        /// </summary>
        private bool RestoreLastScreen()
        {
            if (_settings.Collapsed != null)
            {
                foreach (var path in _settings.Collapsed) _collapsedPaths.Add(path);
            }

            var screen = _settings.Screen;
            if (screen == null) return false;

            var result = RestoreScreen(screen, false);
            if (result == ScreenRestored) return true;

            RestorePanels(screen);
            return result == ScreenOpenFailed;
        }

        /// <summary>終了するとき、今の画面の状態とたたんでいた見出しを覚える（app.json に書くのは StoreLayout）。</summary>
        private void RememberLastScreen()
        {
            _settings.Screen = CaptureScreen();
            _settings.Collapsed = _collapsedPaths.Count > 0 ? _collapsedPaths.ToList() : null;
        }
    }
}

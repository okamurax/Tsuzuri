using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Editing;
using Tsuzuri.Export;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 見出しのブックマーク。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 見出しのブックマーク（SPEC 7章）
        //
        // 書いている最中に、頻繁に・手軽に付け外しするためのもの。
        // 登録と解除は本文の行番号の右ボタンのダブルクリック（2026-11-03。前はアウトラインの右クリック）、一覧はアウトラインの上に新しいものから積む。登録した見出しには • を出す。
        // 行番号ではなく見出しの文字で覚え、開いている間の書き換えには追いかける。
        // ファイルごとに .文書名/state.json に覚え、閉じても終了しても残す（2026-10-14。前は保存しなかった）。
        // 付け外しはすぐ書く。見出しの書き換えに追いかけた分は、本文の保存・ファイルを閉じる・終了のときに書く。
        // ---------------------------------------------------------------

        /// <summary>ブックマークしている見出しにだけ • を出す。</summary>
        private void UpdateMark(OutlineNode node)
        {
            var mark = node.Mark;
            if (mark == null) return;

            var registered = !string.IsNullOrEmpty(node.Text) && FindHeadingBookmark(Active, node.Text) != null;
            mark.Visibility = registered ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshMarks()
        {
            foreach (var item in _outlineOrder)
            {
                var node = item.Tag as OutlineNode;
                if (node != null) UpdateMark(node);
            }
        }

        /// <summary>
        /// その行の見出しをブックマークする。していれば外す（行番号の右ボタンのダブルクリック。2026-11-03）。
        /// 本文の行なら、その行が属する見出し（上で一番近い見出し）。見出しより前の行なら何もしない。
        /// </summary>
        private void ToggleHeadingBookmark(MarkdownView view, int lineNumber)
        {
            if (view == null || string.IsNullOrEmpty(view.FilePath)) return;

            if (WordPressLinkOf(view) != null)
            {
                Status("WordPress の記事の見出しはブックマークできません（記事は一時ファイルで開いているため）。");
                return;
            }

            var heading = view.HeadingAbove(lineNumber);
            if (heading == null || string.IsNullOrEmpty(heading.Text))
            {
                Status("見出しより前の行なので、ブックマークできません。");
                return;
            }

            var existing = FindHeadingBookmark(view, heading.Text);
            var name = HeadingText.Display(heading.Text);

            if (existing != null)
            {
                _headingBookmarks.Remove(existing);
                Status("見出しのブックマークを外しました: " + name);
            }
            else
            {
                var mark = new HeadingBookmark();
                mark.File = view.FilePath;
                mark.Page = view.PageId ?? string.Empty;
                mark.Text = heading.Text;
                mark.Line = heading.Line;
                RememberLayout(mark);

                _headingBookmarks.Insert(0, mark);   // 新しいものを上に積む
                Status("見出しをブックマークしました: " + name);
            }

            HeadingBookmarksChanged();
        }

        /// <summary>
        /// ブックマークしている見出しの、画面の状態を、今の画面で覚え直す（2026-10-19。右クリックの「ブックマークを今の画面で覚え直す」）。
        /// 前は外して付け直すしかなかった。一覧の中の位置は変えない。
        /// </summary>
        private void RememberHeadingBookmarkLayout(OutlineNode node)
        {
            if (node == null || !HasDocument || string.IsNullOrEmpty(node.Text)) return;

            var view = Active;
            var mark = FindHeadingBookmark(view, node.Text);
            if (mark == null) return;

            mark.Line = node.Line;
            RememberLayout(mark);

            HeadingBookmarksChanged();
            Status("ブックマークを今の画面で覚え直しました: " + node.Text);
        }

        private HeadingBookmark FindHeadingBookmark(MarkdownView view, string text)
        {
            if (view == null || string.IsNullOrEmpty(view.FilePath)) return null;

            var page = view.PageId ?? string.Empty;

            foreach (var mark in _headingBookmarks)
            {
                if (!SamePath(mark.File, view.FilePath)) continue;
                if (!string.Equals(mark.Page, page, StringComparison.Ordinal)) continue;
                if (string.Equals(mark.Text, text, StringComparison.Ordinal)) return mark;
            }

            return null;
        }

        private void HeadingBookmarksChanged()
        {
            RefreshHeadingBookmarks();
            RefreshMarks();
            StoreHeadingBookmarks();   // 付け外しはすぐ覚える。落ちても消えないように
        }

        /// <summary>開いているファイルの分だけを出す。1件も無ければ欄ごと隠す。</summary>
        private void RefreshHeadingBookmarks()
        {
            HeadingBookmarkList.Items.Clear();

            if (HasDocument)
            {
                foreach (var mark in _headingBookmarks)
                {
                    if (SamePath(mark.File, Active.FilePath))
                        HeadingBookmarkList.Items.Add(CreateHeadingBookmarkRow(mark));
                }
            }

            HeadingBookmarkPanel.Visibility = HeadingBookmarkList.Items.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// スタックから外れたファイルの分を、覚えてから手放す。読み込みは開くとき（AdoptState）。
        /// </summary>
        private void SyncHeadingBookmarks()
        {
            StoreHeadingBookmarks();

            var removed = _headingBookmarks.RemoveAll(
                mark => !_stack.Any(view => SamePath(view.FilePath, mark.File)));
            foreach (var file in _states.Keys.ToList())
            {
                if (!_stack.Any(view => SamePath(view.FilePath, file))) _states.Remove(file);
            }

            if (removed > 0) HeadingBookmarksChanged();
        }

        /// <summary>
        /// 手元のブックマークを .文書名/state.json に覚える。ファイルごとに、ページと見出しの文字の並び・画面の並びが変わったときだけ書く。
        /// 行だけの違いでは書かない（上で行を足すたびに書き込みが走らないように）。
        /// </summary>
        private void StoreHeadingBookmarks()
        {
            foreach (var entry in _states)
            {
                var file = entry.Key;
                var state = entry.Value;

                var marks = _headingBookmarks
                    .Where(mark => SamePath(mark.File, file))
                    .Select(mark => new HeadingMark { Page = mark.Page, Text = mark.Text, Line = mark.Line, Screen = mark.Screen })
                    .ToList();

                if (SameHeadingMarks(state.Headings, marks)) continue;

                state.Headings = marks.Count > 0 ? marks : null;
                SaveState(file, state);
            }
        }

        private static bool SameHeadingMarks(List<HeadingMark> saved, List<HeadingMark> marks)
        {
            var count = saved == null ? 0 : saved.Count;
            if (count != marks.Count) return false;

            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(saved[i].Page, marks[i].Page, StringComparison.Ordinal)) return false;
                if (!string.Equals(saved[i].Text, marks[i].Text, StringComparison.Ordinal)) return false;

                // 画面の状態は、登録・覚え直すたびに新しく作るので、同じものかどうかで見る。
                if (!ReferenceEquals(saved[i].Screen, marks[i].Screen)) return false;
            }

            return true;
        }

        private Border CreateHeadingBookmarkRow(HeadingBookmark mark)
        {
            var name = new TextBlock();
            name.Text = mark.Text.Length > 0 ? HeadingText.Display(mark.Text) : "(無題の見出し)";

            var shown = HeadingText.Display(mark.Text);
            var tip = string.IsNullOrEmpty(mark.Page) ? shown : shown + "（" + mark.Page + "）";

            var row = CreateRemovableRow(name, mark, tip, "ブックマークから外す",
                OpenHeadingBookmark_Click, RemoveHeadingBookmark_Click);

            // 押したまま動かすと並べ替え（SPEC 7章。2026-10-16）。✕ を押したときは始めない。
            row.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                if (IsButtonClick(e.OriginalSource as DependencyObject)) return;

                _bookmarkDrag = mark;
                _bookmarkDragStart = e.GetPosition(HeadingBookmarkList);
                _bookmarkDragging = false;
            };

            return row;
        }

        // ---------------------------------------------------------------
        // ドラッグで並べ替える（SPEC 7章。2026-10-16）
        //
        // 一覧の中だけで完結する並べ替えなので、OLE のドラッグ&ドロップは使わず、一覧がマウスを掴んで追う。
        // 動かした距離が小さいうちはただのクリック（見出しへ移る）。ドラッグしたあとは、行のクリックにしない。
        // 落とす先は、行の上半分ならその行の前、下半分なら後ろ。青い線で示す。
        // ---------------------------------------------------------------

        private HeadingBookmark _bookmarkDrag;
        private Point _bookmarkDragStart;
        private bool _bookmarkDragging;
        private int _bookmarkDropIndex = -1;

        private void SetUpHeadingBookmarkDrag()
        {
            HeadingBookmarkList.PreviewMouseMove += HeadingBookmarkList_PreviewMouseMove;
            HeadingBookmarkList.PreviewMouseLeftButtonUp += HeadingBookmarkList_PreviewMouseLeftButtonUp;
            HeadingBookmarkList.LostMouseCapture += delegate { EndBookmarkDrag(); };
        }

        private void HeadingBookmarkList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_bookmarkDrag == null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndBookmarkDrag();
                return;
            }

            var at = e.GetPosition(HeadingBookmarkList);

            if (!_bookmarkDragging)
            {
                if (Math.Abs(at.Y - _bookmarkDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                _bookmarkDragging = true;
                HeadingBookmarkList.CaptureMouse();
            }

            ShowBookmarkDropLine(BookmarkDropIndex(at.Y));
        }

        private void HeadingBookmarkList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_bookmarkDragging)
            {
                _bookmarkDrag = null;   // ただのクリック。行が受けて見出しへ移る
                return;
            }

            e.Handled = true;   // ドラッグしたあとは、行のクリックにしない

            var mark = _bookmarkDrag;
            var index = _bookmarkDropIndex;
            EndBookmarkDrag();

            if (mark != null && index >= 0) MoveHeadingBookmark(mark, index);
        }

        /// <summary>落とす先。表示している行の何番目の前か（行の数なら末尾）。</summary>
        private int BookmarkDropIndex(double y)
        {
            var count = HeadingBookmarkList.Items.Count;

            for (var i = 0; i < count; i++)
            {
                var row = HeadingBookmarkList.Items[i] as FrameworkElement;
                if (row == null) continue;

                var top = row.TranslatePoint(new Point(0, 0), HeadingBookmarkList).Y;
                if (y < top + row.ActualHeight / 2) return i;
            }

            return count;
        }

        /// <summary>落とす先に青い線を引く。行の上端（末尾なら最後の行の下端）に 2px。</summary>
        private void ShowBookmarkDropLine(int index)
        {
            _bookmarkDropIndex = index;

            var count = HeadingBookmarkList.Items.Count;
            for (var i = 0; i < count; i++)
            {
                var row = HeadingBookmarkList.Items[i] as Border;
                if (row == null) continue;

                row.BorderBrush = (Brush)FindResource("AccentBrush");
                row.BorderThickness = i == index ? new Thickness(0, 2, 0, 0)
                    : i == count - 1 && index == count ? new Thickness(0, 0, 0, 2)
                    : new Thickness(0);
            }
        }

        private void EndBookmarkDrag()
        {
            var dragging = _bookmarkDragging;

            _bookmarkDrag = null;
            _bookmarkDragging = false;
            _bookmarkDropIndex = -1;

            foreach (var item in HeadingBookmarkList.Items)
            {
                var row = item as Border;
                if (row != null) row.BorderThickness = new Thickness(0);
            }

            if (dragging && HeadingBookmarkList.IsMouseCaptured) HeadingBookmarkList.ReleaseMouseCapture();
        }

        /// <summary>
        /// 一覧の中で動かす。index は動かす前の表示での挿入位置（その行の前へ。行の数なら末尾）。
        /// 一覧に出しているのは開いているファイルの分だけなので、その分の並びだけを入れ替え、ほかのファイルの分の位置は変えない。
        /// </summary>
        private void MoveHeadingBookmark(HeadingBookmark mark, int index)
        {
            var file = mark.File;
            var shown = _headingBookmarks.Where(known => SamePath(known.File, file)).ToList();

            var from = shown.IndexOf(mark);
            if (from < 0 || index == from || index == from + 1) return;   // 自分の前後へ置いても同じ並び

            shown.RemoveAt(from);
            shown.Insert(index > from ? index - 1 : index, mark);

            // 全体の並びの中で、このファイルの分が占めていた場所に、新しい順で入れ直す。
            var next = 0;
            for (var i = 0; i < _headingBookmarks.Count; i++)
            {
                if (SamePath(_headingBookmarks[i].File, file)) _headingBookmarks[i] = shown[next++];
            }

            HeadingBookmarksChanged();   // 並びも state.json に覚える
        }

        private void RemoveHeadingBookmark_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var mark = button == null ? null : button.Tag as HeadingBookmark;
            if (mark == null) return;

            _headingBookmarks.Remove(mark);
            HeadingBookmarksChanged();
        }

        private void OpenHeadingBookmark_Click(object sender, MouseButtonEventArgs e)
        {
            if (IsButtonClick(e.OriginalSource as DependencyObject)) return;

            var container = sender as Border;
            var mark = container == null ? null : container.Tag as HeadingBookmark;
            if (mark != null) OpenHeadingBookmark(mark);
        }

        /// <summary>
        /// その見出しへ移る。見つからなくても勝手には外さない。
        /// 一時的に書き換えているだけかもしれないので、外すかどうかは ✕ で本人が決める。
        /// </summary>
        private void OpenHeadingBookmark(HeadingBookmark mark)
        {
            // 一覧に出ているのは開いているファイルの分だけなので、必ず見つかる。
            var document = FindOpenDocument(mark.File);
            if (document == null) return;

            string pageId;
            int line;
            if (!LocateHeading(document, mark, out pageId, out line))
            {
                Status("見出しが見つかりません: " + mark.Text);
                return;
            }

            // 登録したときの画面の状態を覚えていれば、そのとおりに戻してから、見出しのページを出す（2026-10-25）。
            // 戻せなければ（ページが無くなった等）、見出しのページをそのまま開く。
            // 戻した画面にブックマークのファイルが無ければ戻さない（2026-10-27）。戻しても、すぐ下の ShowPage がそのファイルのルートから
            // 開き直して画面を捨てるうえ、戻した時点でそのファイルの状態とブックマークを手放してしまい、このブックマークも一覧から消えていた。
            if (RestoreScreen(mark.Screen, true, mark.File) == ScreenCancelled) return;

            var view = FindView(document, pageId);
            if (view == null || !_visible.Contains(view)) view = ShowPage(document, pageId);

            if (view == null) return;

            // 見つけた場所を覚え直す。切り出しで別のページに移っていても、次からはそこを探す。
            mark.Page = pageId;
            mark.Line = line;

            // 開いたばかりのページはまだ並べ終わっていないので、並んでから移る。
            Dispatcher.BeginInvoke(new Action(delegate { view.GoToLine(line); }), DispatcherPriority.Loaded);
        }

        /// <summary>登録したときの画面の状態を覚える（SPEC 7章。2026-10-25）。</summary>
        private void RememberLayout(HeadingBookmark mark)
        {
            mark.Screen = CaptureScreen();
        }

        /// <summary>
        /// 覚えているページから探し、無ければ同じファイルの他のページを探す。
        /// 切り出しや戻すで、見出しが別のページに移ることがあるため。
        /// </summary>
        private static bool LocateHeading(PageDocument document, HeadingBookmark mark, out string pageId, out int line)
        {
            pageId = null;
            line = 0;

            var order = new List<string>();
            if (document.Contains(mark.Page)) order.Add(mark.Page);

            foreach (var page in document.Pages)
            {
                if (!string.Equals(page.Id, mark.Page, StringComparison.Ordinal)) order.Add(page.Id);
            }

            foreach (var id in order)
            {
                var entries = MarkdownParser.Headings(document.TextOf(id));
                var hint = string.Equals(id, mark.Page, StringComparison.Ordinal) ? mark.Line : 0;

                var index = NearestHeading(entries, mark.Text, hint);
                if (index < 0) continue;

                pageId = id;
                line = entries[index].Line;
                return true;
            }

            return false;
        }

        /// <summary>同じ文字の見出しが複数あれば、hint の行に一番近いもの。無ければ -1。</summary>
        private static int NearestHeading(List<OutlineHeading> entries, string text, int hint)
        {
            var best = -1;

            for (var i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(entries[i].Text, text, StringComparison.Ordinal)) continue;

                if (best < 0 || Math.Abs(entries[i].Line - hint) < Math.Abs(entries[best].Line - hint))
                    best = i;
            }

            return best;
        }

        /// <summary>
        /// そのページを画面に出す。スタックの決まり（隣り合うページはリンクでつながった親子）を
        /// 崩さないよう、ルートからのリンクの連鎖どおりに積む。
        /// 今のスタックと重なる部分はそのまま使い回す。
        /// </summary>
        private MarkdownView ShowPage(PageDocument document, string pageId)
        {
            // すでに連鎖の中にあれば、そこへ戻る。見えていればそのまま。
            for (var i = 0; i < _stack.Count; i++)
            {
                if (!IsSamePage(_stack[i], document, pageId)) continue;

                var found = _stack[i];

                // 帯にたたんだページなら、帯を押したときと同じく、それより右を閉じる。
                if (!_visible.Contains(found)) PopTo(i);

                return _visible.Contains(found) ? found : null;
            }

            var chain = document.PathTo(pageId);
            if (chain == null)
            {
                Status("ファイルの先頭からリンクを辿れないページにあります（孤立ページ）。");
                return null;
            }

            // 連鎖の先頭（ルート）がスタックにあれば、そこから先を積み直す。無ければ開き直す。
            var start = _stack.FindIndex(view => IsSamePage(view, document, chain[0]));

            if (start < 0)
            {
                // この文書の状態（state.json）を手放していれば、読み直してから積む（2026-10-27）。
                // 前は読み直さずに積み、状態の無いファイルがスタックに載って、あとで _states[...] を引くところで落ちた。
                // 読めなければ、OpenFile と同じく知らせて開かない。尋ねる前に読む（読めないのに今の画面を閉じさせない）。
                DocumentState state = null;
                if (StateOf(document.FilePath) == null && !LoadState(document.FilePath, out state)) return null;

                if (!ConfirmLeaving(new MarkdownView[0], document)) return null;

                if (state != null) AdoptState(document.FilePath, state);

                RememberFileStates();
                _stack.Clear();
                _stack.Add(NewView(document, chain[0]));
                _focused = _stack[0];
                start = 0;

                RebuildStack(false);
                AfterStackChanged();
            }

            var matched = 1;
            while (matched < chain.Count && start + matched < _stack.Count
                   && IsSamePage(_stack[start + matched], document, chain[matched]))
            {
                matched++;
            }

            // 道筋と食い違う右側は先に閉じる。残したまま積むと、道筋の次のページが右側にすでにあったとき
            // 「そこへ戻る」で何も起きず、続くページが親子でないページの右に積まれてしまう。
            if (start + matched < _stack.Count)
            {
                PopTo(start + matched - 1);
                if (_stack.Count != start + matched) return null;   // 保存できずに取りやめた
            }

            for (var j = matched; j < chain.Count; j++)
            {
                PushPage(_stack[start + j - 1], document, chain[j]);

                // 保存できずに中止したときは、積むのをやめる。
                if (!IsSamePage(_stack[_stack.Count - 1], document, chain[j])) return null;
            }

            return _stack[_stack.Count - 1];
        }

        /// <summary>
        /// 開いている間に見出しを書き換えたら、ブックマークも追いかける。
        ///
        /// 見出しの数が変わらず、ブックマークしていた見出しだけが消えたなら、
        /// 同じ位置の見出しが書き換えられたとみなす。1文字打つたびに呼ばれるので、
        /// 名前を丸ごと打ち直しても途中の姿を順に追って最後まで付いていける。
        /// 見出しの数が変わった（見出しを消した・# を外した）ときは追わない。
        /// </summary>
        private void TrackHeadingBookmarks(MarkdownView view, List<OutlineHeading> entries)
        {
            var key = view.FilePath + "#" + view.PageId;
            var previous = string.Equals(key, _trackedPage, StringComparison.OrdinalIgnoreCase)
                ? _trackedEntries
                : null;

            _trackedPage = key;
            _trackedEntries = entries;

            // 見出しの一覧は本文が変わるまで同じものが返る。キャレットが動いただけなら何もしない。
            if (previous == null || ReferenceEquals(previous, entries)) return;

            var page = view.PageId ?? string.Empty;
            var renamed = false;

            foreach (var mark in _headingBookmarks)
            {
                if (!SamePath(mark.File, view.FilePath)) continue;
                if (!string.Equals(mark.Page, page, StringComparison.Ordinal)) continue;

                var now = NearestHeading(entries, mark.Text, mark.Line);
                if (now >= 0)
                {
                    mark.Line = entries[now].Line;   // 上で行が増減しても手がかりを保つ
                    continue;
                }

                if (previous.Count != entries.Count) continue;

                var before = NearestHeading(previous, mark.Text, mark.Line);
                if (before < 0) continue;

                mark.Text = entries[before].Text;
                mark.Line = entries[before].Line;
                renamed = true;
            }

            if (renamed) RefreshHeadingBookmarks();
        }
    }
}

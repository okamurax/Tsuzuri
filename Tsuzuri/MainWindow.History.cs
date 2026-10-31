using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 見出しの履歴（2026-10-29）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 見出しの履歴（SPEC 7章）
        //
        // ある見出しを読み、別の見出しを探して確かめ、また前の見出しに戻って書き直す、という行き来のためのもの。
        // 本文で同じ見出しを2度クリックするかキーを押したら、その見出しを左の欄の下半分に新しいものから積む（2026-10-30。前はカーソルが入っただけで積んでいた）。
        // 押すとその見出しへ移る（見出しのブックマークと同じ移り方）。
        // 同じ見出しは1つにまとめ、入り直したら一番上へ上げる（すぐ前に見ていた見出しが、いつも上から2つめにある）。
        // 覚えるのは 100 件まで。古いものから捨てる。エディタを閉じたら消える（ファイルには書かない）。
        // 出すのは開いているファイルの分だけ（ブックマークと同じ）。ほかのファイルの分も、開き直せばまた出る。
        // ---------------------------------------------------------------

        private const int HeadingHistoryLimit = 100;

        /// <summary>見出しの履歴。新しいものが先頭。中身はブックマークと同じ形（画面の状態は覚えない）。</summary>
        private readonly List<HeadingBookmark> _headingHistory = new List<HeadingBookmark>();

        /// <summary>本文を1度クリックした見出し（ファイル・ページ・見出しの文字）。同じ見出しをもう1度クリックしたら履歴に積む。</summary>
        private HeadingBookmark _headingClicked;

        /// <summary>
        /// 本文をクリックした（2026-10-30）。クリックでカーソルが移ったあとに、その見出しを見る。
        /// 1度目は覚えるだけで、同じ見出しの中をもう1度クリックしたら積む。スクロールの途中で本文を掴んだだけで積まないように。
        /// </summary>
        private void NoteHeadingClick(MarkdownView view)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (view != Active || string.IsNullOrEmpty(view.FilePath)) return;

                var snapshot = view.GetOutline();
                var index = snapshot.CurrentIndex;
                if (index < 0 || index >= snapshot.Entries.Count) return;

                var page = view.PageId ?? string.Empty;
                var text = snapshot.Entries[index].Text;
                if (_headingClicked != null && SameHistoryHeading(_headingClicked, view.FilePath, page, text))
                {
                    RecordHeadingVisit(view, snapshot, true);
                    return;
                }

                _headingClicked = new HeadingBookmark { File = view.FilePath, Page = page, Text = text };
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// 本文でキーを押した（2026-10-30）。押す前にカーソルのいた見出しを積む（そこで書いている・読んでいる）。
        /// 矢印・PageUp/Down・Home/End は除く（読み進めているだけで、通り過ぎた見出しを積まないように）。修飾キーだけ・Esc も除く。
        /// </summary>
        private void NoteHeadingKey(MarkdownView view, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                case Key.PageUp: case Key.PageDown: case Key.Home: case Key.End:
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftShift: case Key.RightShift:
                case Key.LeftAlt: case Key.RightAlt: case Key.LWin: case Key.RWin:
                case Key.Escape:
                    return;
            }

            if (view != Active) return;
            RecordHeadingVisit(view, view.GetOutline(), true);
        }

        /// <summary>
        /// カーソルのいる見出しを履歴に積む。積むのは confirm のとき（同じ見出しを2度クリックした・キーを押した）だけ。
        /// アウトラインを出し直すたび（RefreshOutline）にも呼ぶが、そちらは一番上の見出しを追いかけるだけで積まない。
        /// 一番上と同じ見出しなら何もしない。一番上と同じ行で名前だけ違えば、その見出しを書き換えている最中とみなして名前を直す
        /// （1文字打つたびに積まないように）。
        /// </summary>
        private void RecordHeadingVisit(MarkdownView view, OutlineSnapshot snapshot, bool confirm)
        {
            if (view == null || string.IsNullOrEmpty(view.FilePath)) return;

            var index = snapshot.CurrentIndex;
            if (index < 0 || index >= snapshot.Entries.Count) return;

            var heading = snapshot.Entries[index];
            var page = view.PageId ?? string.Empty;

            var top = _headingHistory.Count > 0 ? _headingHistory[0] : null;
            if (top != null && SamePath(top.File, view.FilePath) && string.Equals(top.Page, page, StringComparison.Ordinal))
            {
                if (string.Equals(top.Text, heading.Text, StringComparison.Ordinal))
                {
                    top.Line = heading.Line;   // 上で行が増減しても手がかりを保つ
                    return;
                }

                if (top.Line == heading.Line)
                {
                    top.Text = heading.Text;
                    _headingHistory.RemoveAll(mark => !ReferenceEquals(mark, top) && SameHistoryHeading(mark, view.FilePath, page, heading.Text));
                    RefreshHeadingHistory();
                    return;
                }
            }

            if (!confirm) return;

            _headingClicked = null;
            _headingHistory.RemoveAll(mark => SameHistoryHeading(mark, view.FilePath, page, heading.Text));
            _headingHistory.Insert(0, new HeadingBookmark { File = view.FilePath, Page = page, Text = heading.Text, Line = heading.Line });
            if (_headingHistory.Count > HeadingHistoryLimit) _headingHistory.RemoveRange(HeadingHistoryLimit, _headingHistory.Count - HeadingHistoryLimit);

            RefreshHeadingHistory();
        }

        private static bool SameHistoryHeading(HeadingBookmark mark, string file, string page, string text)
        {
            return SamePath(mark.File, file)
                && string.Equals(mark.Page, page, StringComparison.Ordinal)
                && string.Equals(mark.Text, text, StringComparison.Ordinal);
        }

        /// <summary>開いているファイルの分だけを並べ直す。</summary>
        private void RefreshHeadingHistory()
        {
            HeadingHistoryList.Children.Clear();

            if (HasDocument)
            {
                foreach (var mark in _headingHistory)
                {
                    if (SamePath(mark.File, Active.FilePath)) HeadingHistoryList.Children.Add(CreateHeadingHistoryRow(mark));
                }
            }

            HeadingHistoryEmpty.Visibility = HeadingHistoryList.Children.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            HeadingHistoryScroll.ScrollToTop();
        }

        private Border CreateHeadingHistoryRow(HeadingBookmark mark)
        {
            var shown = mark.Text.Length > 0 ? HeadingText.Display(mark.Text) : "(無題の見出し)";

            // サブページの見出しは「ページへのリンクの文字 > 見出し」と出す（2026-10-30）。
            // 本体とサブページに同じ名前の見出しを書くことがあり、名前だけでは見分けられないため。本体の見出しはそのまま。
            // リンクが見つからない（切り離されたページ）ときは、紙の印だけ付ける。
            string label = null;
            if (!string.IsNullOrEmpty(mark.Page) && HasDocument && Active.Owner != null)
                label = Active.Owner.LinkTextTo(mark.Page);

            var name = new TextBlock();
            name.Text = label != null ? label + " > " + shown : shown;

            var tip = string.IsNullOrEmpty(mark.Page) ? shown : name.Text + "（" + mark.Page + "）";
            var icon = !string.IsNullOrEmpty(mark.Page) && label == null ? CreatePageMark() : null;

            return CreateRemovableRow(name, mark, tip, "履歴から消す", OpenHeadingHistory_Click, RemoveHeadingHistory_Click, icon);
        }

        /// <summary>リンクの見つからないサブページの見出しの頭に付ける紙の印（2026-10-30）。</summary>
        private TextBlock CreatePageMark()
        {
            return new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("SidebarHeaderText"),
            };
        }

        private void OpenHeadingHistory_Click(object sender, MouseButtonEventArgs e)
        {
            if (IsButtonClick(e.OriginalSource as DependencyObject)) return;

            var container = sender as Border;
            var mark = container == null ? null : container.Tag as HeadingBookmark;
            if (mark != null) OpenHeadingBookmark(mark);   // 画面の状態は覚えていないので、見出しのページを出して移るだけ
        }

        private void RemoveHeadingHistory_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var mark = button == null ? null : button.Tag as HeadingBookmark;
            if (mark == null) return;

            _headingHistory.Remove(mark);
            RefreshHeadingHistory();
        }

        /// <summary>アウトラインと履歴の高さを、覚えている割合にする。</summary>
        private void ApplyHistorySplit()
        {
            OutlineRow.Height = new GridLength(_settings.HistorySplit, GridUnitType.Star);
            HistoryRow.Height = new GridLength(1 - _settings.HistorySplit, GridUnitType.Star);
        }

        /// <summary>仕切りを動かしたら、アウトラインの高さの割合を覚える。</summary>
        private void HistorySplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (e.VerticalChange == 0) return;   // 押しただけ（ダブルクリックの2回目の離しも来る）

            var total = OutlineRow.ActualHeight + HistoryRow.ActualHeight;
            if (total < 1) return;

            _settings.HistorySplit = Math.Max(0.1, Math.Min(0.9, OutlineRow.ActualHeight / total));
            _settings.Save();
            ApplyHistorySplit();
        }

        /// <summary>仕切りをダブルクリックしたら、ちょうど半分ずつに戻す。</summary>
        private void HistorySplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _settings.HistorySplit = 0.5;
            _settings.Save();
            ApplyHistorySplit();
        }
    }
}

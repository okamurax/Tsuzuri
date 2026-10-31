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
    // MainWindow の一部: アウトライン（見出しの木・たたみ状態・項目の見た目）。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // アウトライン（SPEC 7章）
        // ---------------------------------------------------------------

        private void ScheduleOutlineRefresh()
        {
            _outlineRefresh.Stop();
            _outlineRefresh.Start();
        }

        private void RefreshOutline()
        {
            // 開いているファイルが変わったら、見出しのブックマーク欄をそのファイルの分に入れ替える。
            var context = HasDocument ? Active.FilePath : null;
            if (!string.Equals(context, _headingContext, StringComparison.OrdinalIgnoreCase))
            {
                _headingContext = context;
                RefreshHeadingBookmarks();
                RefreshHeadingHistory();
            }

            if (!HasDocument)
            {
                OutlineTree.Items.Clear();
                _outlineOrder.Clear();
                _outlineParents.Clear();
                _outlineSignature = null;
                UpdateFoldToggle();
                RefreshOutlineSearch();
                return;
            }

            var snapshot = Active.GetOutline();

            // 組み直す前に、書き換えられた見出しにブックマークを追いかけさせる。
            // 先にやっておけば、組み直した項目の • が新しい名前で付く。
            TrackHeadingBookmarks(Active, snapshot.Entries);

            var signature = OutlineSignature(snapshot.Entries);

            // 見出しの構成が変わったときだけ組み直す。入力のたびに開閉状態が
            // 飛ばないようにするため。
            if (signature != _outlineSignature)
            {
                RebuildOutline(snapshot.Entries);
                _outlineSignature = signature;
                UpdateFoldToggle();
            }
            else
            {
                // 構成は同じでも、上に行が増減すれば行番号はずれる。
                SyncOutlineLines(snapshot.Entries);
            }

            HighlightOutline(snapshot.CurrentIndex);
            RecordHeadingVisit(Active, snapshot, false);   // 履歴の一番上の見出しを追いかける（積むのはクリック2度・キー。2026-10-30）

            // 検索している間は、結果も本文の見出しに合わせて出し直す（打って見出しが変わった・別のファイルに替わった）。
            if (OutlineSearching) RefreshOutlineSearch();
        }

        private static string OutlineSignature(List<OutlineHeading> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
                sb.Append(entry.Level).Append(':').Append(entry.Text).Append('\n');

            return sb.ToString();
        }

        private void SyncOutlineLines(List<OutlineHeading> entries)
        {
            var count = Math.Min(entries.Count, _outlineOrder.Count);

            for (var i = 0; i < count; i++)
            {
                var node = _outlineOrder[i].Tag as OutlineNode;
                if (node != null) node.Line = entries[i].Line;
            }
        }

        private void RebuildOutline(List<OutlineHeading> entries)
        {
            RememberCollapsed();

            OutlineTree.Items.Clear();
            _outlineOrder.Clear();
            _outlineParents.Clear();

            if (entries.Count == 0)
            {
                var empty = new TreeViewItem();
                empty.Header = "（見出しがありません）";
                empty.Foreground = (Brush)FindResource("TextMuted");
                empty.IsEnabled = false;
                OutlineTree.Items.Add(empty);
                return;
            }

            // 直前の見出しのうち、自分より浅いものが親になる。
            var openItems = new List<TreeViewItem>();
            var openLevels = new List<int>();

            // たたみ状態は見出しの名前で覚える（木の中の位置で覚えると、ドラッグで節を動かしたときにずれるため）。
            // 鍵は「ファイル + ページ」「見出しの名前」「文書の中で同じ名前の見出しの何番目か」をつないだもの。
            // 親の名前もレベルも含めないので、親の外へ動かしても、◀ ▶ でレベルを変えても、たたみ状態が付いていく。
            // ファイルとページを頭に付けるので、別のファイルの同じ名前の見出しには移らない。
            var view = Active;
            var rootPath = (view != null ? view.FilePath + "|" + view.PageId : string.Empty);
            var sameNames = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                while (openLevels.Count > 0 && openLevels[openLevels.Count - 1] >= entry.Level)
                {
                    openItems.RemoveAt(openItems.Count - 1);
                    openLevels.RemoveAt(openLevels.Count - 1);
                }

                var node = new OutlineNode();
                node.Line = entry.Line;
                node.Text = entry.Text;
                node.Level = entry.Level;

                var item = new TreeViewItem();
                item.Header = CreateOutlineHeader(node, entry.Text.Length > 0 ? HeadingText.Display(entry.Text) : "(無題の見出し)");
                item.Padding = new Thickness(2);

                if (openItems.Count == 0)
                {
                    OutlineTree.Items.Add(item);
                }
                else
                {
                    var parent = openItems[openItems.Count - 1];
                    parent.Items.Add(item);
                    _outlineParents[item] = parent;
                }

                int nth;
                sameNames.TryGetValue(entry.Text, out nth);
                sameNames[entry.Text] = nth + 1;

                var path = rootPath + "\n" + entry.Text + "#" + nth;
                node.Path = path;
                item.Tag = node;

                item.IsExpanded = !_collapsedPaths.Contains(path);
                item.MouseLeftButtonUp += OutlineItem_Click;
                _outlineOrder.Add(item);

                openItems.Add(item);
                openLevels.Add(entry.Level);
            }
        }

        // ---------------------------------------------------------------
        // すべて閉じる・すべて開く（SPEC 7章）
        //
        // 検索欄の右端のボタン（⊟ / ⊞）。開いている見出しが1つでもあれば「すべて閉じる」、無ければ「すべて開く」。
        // 1つずつの開閉（三角）と同じ扱いなので、たたみ状態は見出しの名前で覚えられる。
        // ---------------------------------------------------------------

        private void OutlineFoldToggle_Click(object sender, RoutedEventArgs e)
        {
            var expand = !AnyOutlineExpanded();

            foreach (var item in _outlineOrder)
            {
                if (item.Items.Count > 0) item.IsExpanded = expand;
            }

            // 開いたときは、カーソルのある見出しが見えるように。閉じたときは一番上へ。
            if (!expand)
            {
                var scroll = FindDescendant<ScrollViewer>(OutlineTree);
                if (scroll != null) scroll.ScrollToTop();
            }
            else if (OutlineTree.SelectedItem is TreeViewItem)
            {
                ((TreeViewItem)OutlineTree.SelectedItem).BringIntoView();
            }

            UpdateFoldToggle();
        }

        /// <summary>子のある見出しのうち、開いているものがあるか。</summary>
        private bool AnyOutlineExpanded()
        {
            return _outlineOrder.Any(item => item.Items.Count > 0 && item.IsExpanded);
        }

        /// <summary>ボタンの文字を、押したときに起きることに合わせる。たためる見出しが無ければ押せない。</summary>
        private void UpdateFoldToggle()
        {
            if (OutlineFoldToggle == null) return;

            var foldable = _outlineOrder.Any(item => item.Items.Count > 0);
            OutlineFoldToggle.IsEnabled = foldable;
            // 記号だけ（文字は検索欄の右に収まらない）。⊟ でたたむ、⊞ で開く。何をするかはツールチップ。
            OutlineFoldToggle.Content = foldable && !AnyOutlineExpanded() ? "⊞" : "⊟";
            OutlineFoldToggle.ToolTip = foldable && !AnyOutlineExpanded()
                ? "アウトラインの見出しをすべて開く"
                : "アウトラインの見出しをすべてたたむ（見出し1だけにする）";
        }

        // ---------------------------------------------------------------
        // 見出しの検索（SPEC 7章）
        //
        // ファイラーの検索と同じ動き。打つと少し待ってから探し、空白で区切ると全部を含む見出しだけにする（大文字小文字は区別しない）。
        // 何か入っている間は、木の代わりに当たった見出しの一覧を出す。押すとその見出しへ移る。空にすると木に戻る（Esc で空にする）。
        // 木のたたみ状態には触らない。
        // ---------------------------------------------------------------

        private readonly DispatcherTimer _outlineSearchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        /// <summary>
        /// いま出している結果の元（検索の言葉・ファイルとページ・見出しの並び）。同じなら出し直さない。
        /// カーソルが動くたびにアウトラインは読み直されるので、そのたびに作り直すと結果のスクロールが先頭へ戻ってしまう。
        /// </summary>
        private string _outlineSearchKey;

        private void SetUpOutlineSearch()
        {
            _outlineSearchDelay.Tick += delegate
            {
                _outlineSearchDelay.Stop();
                RefreshOutlineSearch();
            };

            OutlineSearchBox.TextChanged += delegate
            {
                _outlineSearchDelay.Stop();
                _outlineSearchDelay.Start();
            };

            OutlineSearchBox.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape && OutlineSearchBox.Text.Length > 0)
                {
                    e.Handled = true;
                    OutlineSearchBox.Text = string.Empty;
                }
            };

            // 「検索」の文字や余白を押しても打てるように。
            OutlineSearchArea.MouseLeftButtonDown += delegate { OutlineSearchBox.Focus(); };
        }

        private string[] OutlineSearchWords()
        {
            return OutlineSearchBox.Text.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private bool OutlineSearching
        {
            get { return OutlineSearchWords().Length > 0; }
        }

        /// <summary>検索欄の中身で、結果の一覧を出し直す。空なら木に戻す。</summary>
        private void RefreshOutlineSearch()
        {
            var words = OutlineSearchWords();
            var searching = words.Length > 0;

            OutlineTree.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            OutlineSearchScroll.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

            var entries = searching && HasDocument ? Active.GetOutline().Entries : new List<OutlineHeading>();
            var key = !searching ? null
                : string.Join(" ", words) + "\n" + (HasDocument ? Active.FilePath + "|" + Active.PageId : string.Empty) + "\n" + OutlineSignature(entries);
            if (key != null && key == _outlineSearchKey) return;
            _outlineSearchKey = key;

            OutlineSearchResults.Children.Clear();
            if (!searching) return;

            if (!HasDocument)
            {
                OutlineSearchResults.Children.Add(OutlineSearchNote("ファイルを開くと、見出しを探せます。"));
                return;
            }

            // 親の見出しをたどりながら見る。結果の行に、どの見出しの中か（親の名前）を添えるため。
            var parents = new List<OutlineHeading>();
            var found = 0;

            foreach (var entry in entries)
            {
                while (parents.Count > 0 && parents[parents.Count - 1].Level >= entry.Level) parents.RemoveAt(parents.Count - 1);

                if (words.All(word => entry.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    OutlineSearchResults.Children.Add(CreateOutlineSearchRow(entry, parents));
                    found++;
                }

                parents.Add(entry);
            }

            if (found == 0) OutlineSearchResults.Children.Add(OutlineSearchNote("当たる見出しはありません。"));
        }

        private static TextBlock OutlineSearchNote(string text)
        {
            var note = new TextBlock();
            note.Text = text;
            note.FontSize = 12;
            note.Margin = new Thickness(16, 6, 12, 6);
            note.TextWrapping = TextWrapping.Wrap;
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            return note;
        }

        /// <summary>結果の1行。見出しの名前と、その下に薄く親の見出しの道のり。押すとその見出しへ移る。</summary>
        private Border CreateOutlineSearchRow(OutlineHeading entry, List<OutlineHeading> parents)
        {
            var name = new TextBlock();
            name.Text = entry.Text.Length > 0 ? HeadingText.Display(entry.Text) : "(無題の見出し)";
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.SetResourceReference(TextBlock.ForegroundProperty, "SidebarText");

            var panel = new StackPanel();
            panel.Children.Add(name);

            var path = string.Join(" › ", parents.Select(parent => HeadingText.Display(parent.Text)));
            if (path.Length > 0)
            {
                var where = new TextBlock();
                where.Text = path;
                where.FontSize = 11;
                where.TextTrimming = TextTrimming.CharacterEllipsis;
                where.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                panel.Children.Add(where);
            }

            var row = new Border();
            row.Child = panel;
            row.Padding = new Thickness(16, 4, 12, 4);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.ToolTip = (path.Length > 0 ? path + " › " : string.Empty) + name.Text;

            row.MouseEnter += delegate { row.SetResourceReference(Border.BackgroundProperty, "HoverFill"); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (Active == null) return;

                // 結果を出したあとに上の行が増減していても、押した時点の本文から同じ名前で一番近い見出しを探し直す。
                var now = Active.GetOutline().Entries;
                var index = NearestHeading(now, entry.Text, entry.Line);
                if (index >= 0) Active.GoToLine(now[index].Line);
            };

            return row;
        }

        /// <summary>組み直しの前に、たたまれている項目を覚えておく。</summary>
        private void RememberCollapsed()
        {
            foreach (var root in OutlineTree.Items.OfType<TreeViewItem>())
                CollectCollapsed(root);
        }

        private void CollectCollapsed(TreeViewItem item)
        {
            var node = item.Tag as OutlineNode;
            if (node != null)
            {
                if (!item.IsExpanded && item.Items.Count > 0) _collapsedPaths.Add(node.Path);
                else _collapsedPaths.Remove(node.Path);
            }

            foreach (var child in item.Items.OfType<TreeViewItem>())
                CollectCollapsed(child);
        }

        /// <summary>キャレットがいる見出しを選択状態にする（SPEC 7章）。</summary>
        private void HighlightOutline(int index)
        {
            if (index < 0 || index >= _outlineOrder.Count) return;

            var item = _outlineOrder[index];
            if (item.IsSelected) return;

            TreeViewItem parent;
            var child = item;
            while (_outlineParents.TryGetValue(child, out parent))
            {
                parent.IsExpanded = true;
                child = parent;
            }

            item.IsSelected = true;
            item.BringIntoView();
        }

        /// <summary>
        /// 選択の変化ではなくクリックで飛ばす。
        /// カーソルのある見出しは自動で選択状態になるため、それをクリックしても
        /// SelectedItemChanged が起きず、H2 以下が反応しなかった。
        /// </summary>
        private void OutlineItem_Click(object sender, MouseButtonEventArgs e)
        {
            // ドラッグで動かしたときのボタン離しは、OutlineTree_PreviewMouseLeftButtonUp で止めるので、ここへは来ない。
            // 名前を打つ欄の中のクリックは、欄に任せる。親の見出しの項目にも伝えない（伝わると親の見出しへ移ってしまう）。
            if (IsInsideTextBox(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                return;
            }

            if (IsExpanderClick(e.OriginalSource as DependencyObject)) return;

            var item = sender as TreeViewItem;
            if (item == null) return;

            e.Handled = true;   // 親の項目まで伝わらないようにする

            var node = item.Tag as OutlineNode;
            if (node == null || Active == null) return;

            GoToOutlineNode(node);
        }

        /// <summary>
        /// その見出しを選び、エディタのその行へ移る。
        /// </summary>
        private void GoToOutlineNode(OutlineNode node)
        {
            if (node == null || Active == null) return;

            var item = _outlineOrder.FirstOrDefault(each => each.Tag == node);
            if (item != null) item.IsSelected = true;

            Active.GoToLine(node.Line);
        }

        private static bool IsInsideTextBox(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                if (source is TextBoxBase) return true;

                var visual = source as Visual;
                if (visual == null) return false;
                source = VisualTreeHelper.GetParent(visual);
            }

            return false;
        }

        /// <summary>開閉の三角などのボタンと、名前を打つ欄を押したときは移動しない。</summary>
        private static bool IsExpanderClick(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase) return true;
                if (source is TextBoxBase) return true;   // 名前を打つ欄（その中のクリックは、移動もドラッグもしない）
                if (source is TreeViewItem) return false;

                var visual = source as Visual;
                if (visual == null) return false;

                source = VisualTreeHelper.GetParent(visual);
            }

            return false;
        }


        /// <summary>
        /// 見出しの操作の右クリックのメニュー（SPEC 7章）。ファイラー・ランチャーと同じ Windows 標準のメニュー（2026-10-16。前は記号のボタンの列）。
        /// 先頭に見出しの名前を押せない見出しとして出し、追加・レベル・ブックマーク・名前の変更と削除をまとまりごとに区切る。
        /// 名前の欄を出す操作は、メニューが閉じてから出す。閉じるときにフォーカスが動き、出したばかりの欄が消えないように。
        /// </summary>
        private void ShowOutlineMenu(OutlineNode node)
        {
            Action<Action> later = action => Dispatcher.BeginInvoke(action, DispatcherPriority.Input);

            var named = !string.IsNullOrEmpty(node.Text);
            var registered = named && FindHeadingBookmark(Active, node.Text) != null;

            var menu = new ContextMenu();
            menu.Items.Add(Views.FilerView.MenuTitle(named ? HeadingText.Display(node.Text) : "(無題の見出し)"));
            menu.Items.Add(new Separator());

            // 見出しの追加と名前の変更は、2026-10-25 にメニューから外した（本文で Ctrl+Enter・Ctrl+Shift+Enter で足し、名前は本文で直す）。
            menu.Items.Add(OutlineMenuItem("レベルを上げる（小見出しも一緒に）", node.Level > 1, () => ChangeHeadingLevel(node, -1)));
            menu.Items.Add(OutlineMenuItem("レベルを下げる（小見出しも一緒に）", node.Level < 6, () => ChangeHeadingLevel(node, 1)));
            menu.Items.Add(CreateSortMenu(node, menu));
            menu.Items.Add(new Separator());

            if (registered) menu.Items.Add(OutlineMenuItem("ブックマークを今の画面で覚え直す", true, () => RememberHeadingBookmarkLayout(node)));
            menu.Items.Add(OutlineMenuItem(registered ? "ブックマークから外す" : "ブックマークする", named, () => ToggleHeadingBookmark(node)));
            menu.Items.Add(new Separator());

            menu.Items.Add(OutlineMenuItem("削除…（中身と小見出しも）", true, () => later(() => DeleteOutlineHeading(node))));

            menu.IsOpen = true;
        }

        /// <summary>
        /// 「『本社』の中の見出しを並べ替え（5 件） ▸ 昇順／降順」（2026-10-27）。並べ替えるのは、いつも右クリックした見出しの中（直下の子）。
        /// 右クリックした見出しそのものは動かない。子が2つ未満なら押せない。
        /// メニューにマウスを載せている間は、動く子の見出しに色を付けて、範囲を見せる。
        /// </summary>
        private MenuItem CreateSortMenu(OutlineNode node, ContextMenu menu)
        {
            var item = _outlineOrder.FirstOrDefault(entry => ReferenceEquals(entry.Tag, node));
            var children = item == null
                ? new List<OutlineNode>()
                : item.Items.OfType<TreeViewItem>().Select(child => child.Tag as OutlineNode).Where(child => child != null).ToList();

            var name = HeadingText.Display(node.Text);
            if (string.IsNullOrEmpty(name)) name = "(無題の見出し)";
            if (name.Length > 16) name = name.Substring(0, 16) + "…";

            var sort = new MenuItem();
            sort.Header = "「" + name + "」の中の見出しを並べ替え（" + children.Count + " 件）";
            sort.IsEnabled = children.Count >= 2;
            sort.Items.Add(OutlineMenuItem("昇順", true, () => SortOutlineChildren(node, false)));
            sort.Items.Add(OutlineMenuItem("降順", true, () => SortOutlineChildren(node, true)));

            Action<bool> mark = on =>
            {
                foreach (var child in children)
                {
                    if (child.Frame != null) child.Frame.Background = on ? (Brush)FindResource("AccentFill") : Brushes.Transparent;
                }
            };
            sort.MouseEnter += delegate { mark(true); };
            sort.SubmenuOpened += delegate { mark(true); };
            sort.MouseLeave += delegate { if (!sort.IsSubmenuOpen) mark(false); };
            sort.SubmenuClosed += delegate { mark(false); };
            menu.Closed += delegate { mark(false); };

            return sort;
        }

        private static MenuItem OutlineMenuItem(string header, bool enabled, Action click)
        {
            var item = new MenuItem();
            item.Header = header;
            item.IsEnabled = enabled;
            item.Click += delegate { click(); };
            return item;
        }

        /// <summary>アウトラインの項目。見出しの名前と、ブックマークしていれば右端に •。操作は右クリック（SPEC 7章）。</summary>
        private FrameworkElement CreateOutlineHeader(OutlineNode node, string display)
        {
            // 幅はサイドバーに収まり、はみ出す見出しは … で切る。全文はツールチップで見せる。
            var text = new TextBlock();
            text.Text = display;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.ToolTip = display;

            // ブックマークしている見出しの印。付け外しはホバーのボタンか右クリック。
            var mark = new TextBlock();
            mark.Text = "•";
            mark.FontSize = 12;
            mark.Margin = new Thickness(6, 0, 4, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.ToolTip = "ブックマークしている見出し（外すのは右クリック）";
            mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            DockPanel.SetDock(mark, Dock.Right);

            var panel = new DockPanel();
            panel.Background = Brushes.Transparent;   // 余白の上でもマウスを拾う
            panel.Children.Add(mark);
            panel.Children.Add(text);

            node.Mark = mark;
            node.Label = text;
            node.Panel = panel;
            UpdateMark(node);

            var host = new Grid();
            host.Background = Brushes.Transparent;
            host.Children.Add(panel);

            // 見出しをつかんで運ぶと、その節を好きな位置へ動かせる（SPEC 7章）。
            // 落とす位置は、この枠の上端（この見出しの手前）か、最後の項目の下端（文書の最後）に青い線で出す。
            // 線の分だけ高さが変わらないよう、透明な線をいつも引いておき、色だけ変える。
            var frame = new Border();
            frame.Child = host;
            frame.BorderThickness = new Thickness(0, 2, 0, 2);
            frame.BorderBrush = Brushes.Transparent;
            frame.Background = Brushes.Transparent;
            frame.ToolTip = display + Environment.NewLine + "ドラッグで好きな位置へ動かす（落とした位置の見出しにレベルを合わせ、配下も同じだけずらす）" +
                            Environment.NewLine + "右クリックで、見出しの追加・レベル・ブックマーク・名前の変更・削除";
            // 右クリックで、左クリックと同じくその見出しへ移って選んでから、メニューを出す。
            // 選んでいる見出し（エディタのカーソルの位置）と、メニューの対象をそろえるため。選んでいる印で、どの見出しのメニューか分かる。
            frame.MouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (_outlineDragging) return;
                if (node.Label != null && node.Label.Parent != node.Panel) return;   // 名前を打っている間は出さない

                GoToOutlineNode(node);
                ShowOutlineMenu(node);
            };
            frame.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                if (IsExpanderClick(e.OriginalSource as DependencyObject)) return;

                _outlineDragNode = node;
                _outlineDragStart = e.GetPosition(OutlineTree);
                _outlineDragging = false;
            };
            node.Frame = frame;

            return frame;
        }
    }
}

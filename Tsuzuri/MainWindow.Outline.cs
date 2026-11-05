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
                _outlineOwner = null;
                _outlineStashes.Clear();
                UpdateFoldButtons();
                return;
            }

            var started = PerfLog.Start();
            var snapshot = Active.GetOutline();
            PerfLog.Step("見出しを集める", started);
            PerfLog.StepNote("見出し数", snapshot.Entries.Count);

            // 組み直す前に、書き換えられた見出しにブックマークを追いかけさせる。
            // 先にやっておけば、組み直した項目の • が新しい名前で付く。
            started = PerfLog.Start();
            TrackHeadingBookmarks(Active, snapshot.Entries);
            PerfLog.Step("ブックマークの追従", started);

            started = PerfLog.Start();
            var signature = OutlineSignature(snapshot.Entries);
            PerfLog.Step("構成の比較用", started);

            // 見出しの構成が変わったときだけ組み直す。入力のたびに開閉状態が
            // 飛ばないようにするため。
            if (signature != _outlineSignature)
            {
                // ページを移ったときは、今の木を元のページの分としてしまい、移った先の木がしまってあればそれを戻す（2026-11-05）。
                // 前は移るたびに作り直していて、2万行の本体（見出しが数千）からサブページを開いて戻るたびに、本体の木を全部作り直して遅かった。
                var owner = Active;
                started = PerfLog.Start();
                if (!ReferenceEquals(owner, _outlineOwner)) StashOutline();
                PerfLog.Step("木をしまう", started);

                started = PerfLog.Start();
                if (ReferenceEquals(owner, _outlineOwner) || !RestoreOutline(owner, signature))
                {
                    RebuildOutline(snapshot.Entries);
                    PerfLog.Step("木を作り直す", started);
                }
                else
                {
                    PerfLog.Step("木を戻す", started);
                }

                _outlineOwner = owner;
                _outlineSignature = signature;
                UpdateFoldButtons();
                SyncOutlineLines(snapshot.Entries);
            }
            else
            {
                // 構成は同じでも、上に行が増減すれば行番号はずれる。
                SyncOutlineLines(snapshot.Entries);
            }

            started = PerfLog.Start();
            HighlightOutline(snapshot.CurrentIndex);
            PerfLog.Step("今の見出しを選ぶ", started);
            started = PerfLog.Start();
            RecordHeadingVisit(Active, snapshot, false);
            PerfLog.Step("見出しの履歴", started);   // 履歴の一番上の見出しを追いかける（積むのはクリック2度・キー。2026-10-30）
        }

        /// <summary>しまっておいたアウトラインの木。ページ（MarkdownView）ごとに1つ。</summary>
        private sealed class OutlineStash
        {
            public string Signature;
            public string RootPath;
            public List<TreeViewItem> Roots;
            public List<TreeViewItem> Order;
            public Dictionary<TreeViewItem, TreeViewItem> Parents;
        }

        /// <summary>今アウトラインに出している木が、どのページのものか。</summary>
        private MarkdownView _outlineOwner;

        private readonly Dictionary<MarkdownView, OutlineStash> _outlineStashes = new Dictionary<MarkdownView, OutlineStash>();

        private static string OutlineRootPath(MarkdownView view)
        {
            return view != null ? view.FilePath + "|" + view.PageId : string.Empty;
        }

        /// <summary>今の木を、そのページの分としてしまう。木は外すだけで、項目はそのまま持っておく（作り直さずに戻せる）。</summary>
        private void StashOutline()
        {
            var owner = _outlineOwner;
            if (owner != null && _outlineSignature != null && _stack.Contains(owner))
            {
                RememberCollapsed();
                _outlineStashes[owner] = new OutlineStash
                {
                    Signature = _outlineSignature,
                    RootPath = OutlineRootPath(owner),
                    Roots = OutlineTree.Items.OfType<TreeViewItem>().ToList(),
                    Order = _outlineOrder.ToList(),
                    Parents = new Dictionary<TreeViewItem, TreeViewItem>(_outlineParents),
                };
            }

            OutlineTree.Items.Clear();
            _outlineOrder.Clear();
            _outlineParents.Clear();
            _outlineOwner = null;
        }

        /// <summary>
        /// しまっておいた木を戻す。見出しの構成とファイル・ページが変わっていないときだけ（変わっていれば作り直す）。
        /// 閉じたページの分は、ここで捨てる。
        /// </summary>
        private bool RestoreOutline(MarkdownView owner, string signature)
        {
            foreach (var gone in _outlineStashes.Keys.Where(view => !_stack.Contains(view)).ToList())
                _outlineStashes.Remove(gone);

            OutlineStash stash;
            if (owner == null || !_outlineStashes.TryGetValue(owner, out stash)) return false;
            _outlineStashes.Remove(owner);

            if (stash.Signature != signature || stash.RootPath != OutlineRootPath(owner)) return false;

            OutlineTree.Items.Clear();
            foreach (var root in stash.Roots) OutlineTree.Items.Add(root);
            _outlineOrder.Clear();
            _outlineOrder.AddRange(stash.Order);
            _outlineParents.Clear();
            foreach (var pair in stash.Parents) _outlineParents[pair.Key] = pair.Value;

            // しまっている間にほかの窓などでたたみ状態が変わっていれば合わせる。
            foreach (var item in _outlineOrder)
            {
                var node = item.Tag as OutlineNode;
                if (node != null && item.Items.Count > 0) item.IsExpanded = !_collapsedPaths.Contains(node.Path);
            }

            return true;
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
        // 開く・たたむボタン（SPEC 7章。2026-11-04）
        //
        // サイドバーの一番上の ⊟ 1 2 3 ⊞。数字 n は「見出し n まで開く」で、レベルが n 以下の見出しを開き、それより深いものはたたむ。
        // ⊟ はすべてたたみ、⊞ はすべて開く（Tag の 0 と 99）。
        // 1つずつの開閉（三角）と同じ扱いなので、たたみ状態は見出しの名前で覚えられる。
        // ---------------------------------------------------------------

        private void OutlineFold_Click(object sender, RoutedEventArgs e)
        {
            int depth;
            if (!int.TryParse((sender as FrameworkElement)?.Tag as string, out depth)) return;

            foreach (var item in _outlineOrder)
            {
                var node = item.Tag as OutlineNode;
                if (item.Items.Count > 0 && node != null) item.IsExpanded = node.Level <= depth;
            }

            // すべてたたんだときは一番上へ。ほかは、カーソルのある見出しが見えていればそこを見せる。
            if (depth == 0)
            {
                var scroll = FindDescendant<ScrollViewer>(OutlineTree);
                if (scroll != null) scroll.ScrollToTop();
            }
            else
            {
                var selected = OutlineTree.SelectedItem as TreeViewItem;
                if (selected != null && selected.IsVisible) selected.BringIntoView();
            }
        }

        /// <summary>たためる見出しが無ければ、ボタンを押せなくする。</summary>
        private void UpdateFoldButtons()
        {
            if (OutlineFoldArea == null) return;

            var foldable = _outlineOrder.Any(item => item.Items.Count > 0);
            OutlineFoldArea.IsEnabled = foldable;
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

            // ブックマークの付け外しは、2026-11-03 にメニューから外した（本文の行番号を右ボタンでダブルクリック）。
            if (registered)
            {
                menu.Items.Add(OutlineMenuItem("ブックマークを今の画面で覚え直す", true, () => RememberHeadingBookmarkLayout(node)));
                menu.Items.Add(new Separator());
            }

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

            // ブックマークしている見出しの印。付け外しは本文の行番号の右ボタンのダブルクリック。
            var mark = new TextBlock();
            mark.Text = "•";
            mark.FontSize = 12;
            mark.Margin = new Thickness(6, 0, 4, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.ToolTip = "ブックマークしている見出し（付け外しは、本文の行番号を右ボタンでダブルクリック）";
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
                            Environment.NewLine + "右クリックで、レベル・並べ替え・削除";
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

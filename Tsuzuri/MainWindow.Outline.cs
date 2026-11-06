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
                _outlineBuiltSignature = null;
                DropOutlineStashes();
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
                _outlineBuiltSignature = signature;
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
            public TreeView Tree;
            public List<TreeViewItem> Order;
            public Dictionary<TreeViewItem, TreeViewItem> Parents;
        }

        /// <summary>今アウトラインに出している木が、どのページのものか。</summary>
        private MarkdownView _outlineOwner;

        /// <summary>今の木を作ったときの見出しの構成。_outlineSignature と違い、作り直させるために null にされることはない。</summary>
        private string _outlineBuiltSignature;

        private readonly Dictionary<MarkdownView, OutlineStash> _outlineStashes = new Dictionary<MarkdownView, OutlineStash>();

        private static string OutlineRootPath(MarkdownView view)
        {
            return view != null ? view.FilePath + "|" + view.PageId : string.Empty;
        }

        /// <summary>アウトラインの木に、ドラッグで節を動かす処理をつなぐ。</summary>
        private void HookOutlineTree(TreeView tree)
        {
            tree.PreviewMouseLeftButtonDown += OutlineTree_PreviewMouseLeftButtonDown;
            tree.PreviewMouseMove += OutlineTree_PreviewMouseMove;
            tree.PreviewMouseLeftButtonUp += OutlineTree_PreviewMouseLeftButtonUp;
            tree.LostMouseCapture += delegate { EndOutlineDrag(); };
        }

        /// <summary>空の木を作って、今の木と同じ所に置く（見た目は今の木と同じ）。</summary>
        private TreeView CreateOutlineTree(TreeView like)
        {
            var tree = new TreeView();
            tree.Padding = like.Padding;
            ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
            HookOutlineTree(tree);

            var host = like.Parent as Panel;
            if (host != null) host.Children.Add(tree);
            return tree;
        }

        private static void DropOutlineTree(TreeView tree)
        {
            var host = tree.Parent as Panel;
            if (host != null) host.Children.Remove(tree);
        }

        /// <summary>
        /// 今の木を、そのページの分としてしまう（2026-11-05）。木（TreeView）ごと隠して持っておき、新しいページには空の木を出す。
        /// 項目を外して付け直す形（最初の作り）では、外すのに 0.3 秒、付け直したあとの配置に 1〜3 秒かかった（見出し 1189 の文書）。
        /// 隠した木は配置を覚えたままなので、出し直すだけなら配置し直さずに済む。
        /// </summary>
        private void StashOutline()
        {
            var owner = _outlineOwner;
            var tree = OutlineTree;

            if (owner != null && _outlineBuiltSignature != null && _stack.Contains(owner))
            {
                RememberCollapsed();
                tree.Visibility = Visibility.Collapsed;

                OutlineStash old;
                if (_outlineStashes.TryGetValue(owner, out old)) DropOutlineTree(old.Tree);

                _outlineStashes[owner] = new OutlineStash
                {
                    Signature = _outlineBuiltSignature,
                    RootPath = OutlineRootPath(owner),
                    Tree = tree,
                    Order = _outlineOrder.ToList(),
                    Parents = new Dictionary<TreeViewItem, TreeViewItem>(_outlineParents),
                };

                OutlineTree = CreateOutlineTree(tree);
            }
            else
            {
                tree.Items.Clear();
            }

            _outlineOrder.Clear();
            _outlineParents.Clear();
            _outlineOwner = null;
            _outlineBuiltSignature = null;
        }

        /// <summary>
        /// しまっておいた木を出す。見出しの構成とファイル・ページが変わっていないときだけ（変わっていれば捨てて、作り直させる）。
        /// 閉じたページの分は、ここで捨てる。
        /// </summary>
        private bool RestoreOutline(MarkdownView owner, string signature)
        {
            foreach (var gone in _outlineStashes.Where(pair => !_stack.Contains(pair.Key)).ToList())
            {
                DropOutlineTree(gone.Value.Tree);
                _outlineStashes.Remove(gone.Key);
            }

            OutlineStash stash;
            if (owner == null || !_outlineStashes.TryGetValue(owner, out stash)) return false;
            _outlineStashes.Remove(owner);

            if (stash.Signature != signature || stash.RootPath != OutlineRootPath(owner))
            {
                DropOutlineTree(stash.Tree);
                return false;
            }

            // StashOutline が置いた空の木と入れ替える。
            DropOutlineTree(OutlineTree);
            OutlineTree = stash.Tree;
            OutlineTree.Visibility = Visibility.Visible;

            _outlineOrder.Clear();
            _outlineOrder.AddRange(stash.Order);
            _outlineParents.Clear();
            foreach (var pair in stash.Parents) _outlineParents[pair.Key] = pair.Value;

            // しまっている間にほかの窓などでたたみ状態が変わっていれば合わせる。
            foreach (var item in _outlineOrder)
            {
                var node = item.Tag as OutlineNode;
                if (node != null && item.Items.Count > 0)
                {
                    var expanded = !_collapsedPaths.Contains(node.Path);
                    if (item.IsExpanded != expanded) item.IsExpanded = expanded;
                }
            }

            return true;
        }

        private void DropOutlineStashes()
        {
            foreach (var stash in _outlineStashes.Values) DropOutlineTree(stash.Tree);
            _outlineStashes.Clear();
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

            AddRotateButtons(OutlineTree.Items);
            foreach (var each in _outlineOrder) AddRotateButtons(each.Items);
        }

        /// <summary>
        /// 同じ階層の見出し（items）が2つ以上あれば、その先頭の見出しに ▲▼ を付ける（SPEC 7章。2026-11-06）。
        /// ▲ でみな1つ上がり（先頭は末尾へ）、▼ でみな1つ下がる（末尾が先頭へ）。回すと木は組み直され、新しい先頭に付く。
        /// ボタンは同じ位置に残るので、マウスを動かさずに続けて押せる。
        /// </summary>
        private void AddRotateButtons(ItemCollection items)
        {
            if (items.Count < 2) return;

            var first = items[0] as TreeViewItem;
            var node = first?.Tag as OutlineNode;
            if (node == null || node.Row == null) return;

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.VerticalAlignment = VerticalAlignment.Center;
            buttons.Margin = new Thickness(2, 0, 0, 0);
            buttons.Children.Add(RotateButton(node, true, "▲", "同じ階層の見出しを回す（みな1つ上がり、先頭は末尾へ）"));
            buttons.Children.Add(RotateButton(node, false, "▼", "同じ階層の見出しを逆に回す（みな1つ下がり、末尾が先頭へ）"));
            DockPanel.SetDock(buttons, Dock.Right);

            // ブックマークの • より右に置く（• の有無で位置が変わらないように）。
            node.Row.Children.Insert(0, buttons);
        }

        /// <summary>
        /// 回すボタン1つ。同じ大きさの四角い縁で囲む（縁が無いと、▲ と ▼ の字の形の違いで上下にずれて見えるため。2026-11-06）。
        /// </summary>
        private FrameworkElement RotateButton(OutlineNode node, bool up, string symbol, string tip)
        {
            var button = new Button();
            button.Content = symbol;
            button.Style = (Style)FindResource("ToolButton");
            button.FontSize = 8;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(0);
            button.Focusable = false;
            button.ToolTip = tip;
            button.SetResourceReference(ForegroundProperty, "TextMuted");
            button.Click += (sender, e) =>
            {
                e.Handled = true;
                RotateOutlineSiblings(node, up);
            };

            var muted = ((SolidColorBrush)FindResource("TextMuted")).Color;
            var box = new Border();
            box.Child = button;
            box.Width = 16;
            box.Height = 16;
            box.Margin = new Thickness(2, 0, 0, 0);
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(3);
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, muted.R, muted.G, muted.B));
            return box;
        }

        // ---------------------------------------------------------------
        // 開く・たたむボタン（SPEC 7章。2026-11-04）
        //
        // サイドバーの一番上の右端の ⊟ ⊞。⊟ はすべてたたみ、⊞ はすべて開く（Tag の 0 と 99）。
        // Tag の数 n は「レベルが n 以下の見出しを開き、それより深いものはたたむ」（2026-11-06 に 1 2 3 のボタンはやめたが、作りはそのまま）。
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

        /// <summary>開閉の三角などのボタンを押したときは移動しない（ドラッグも始めない）。</summary>
        private static bool IsExpanderClick(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase) return true;
                if (source is TreeViewItem) return false;

                var visual = source as Visual;
                if (visual == null) return false;

                source = VisualTreeHelper.GetParent(visual);
            }

            return false;
        }


        /// <summary>
        /// 見出しの操作の右クリックのメニュー（SPEC 7章）。ファイラー・ランチャーと同じ Windows 標準のメニュー（2026-10-16。前は記号のボタンの列）。
        /// 先頭に見出しの名前を押せない見出しとして出し、レベル｜ブックマークの覚え直し（登録している見出しだけ）｜削除 をまとまりごとに区切る。
        /// 削除の確かめは、メニューが閉じてから出す。
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
            node.Row = panel;
            UpdateMark(node);

            var host = new Grid();
            host.Background = Brushes.Transparent;
            host.Children.Add(panel);

            // 見出しをつかんで運ぶと、その節を好きな位置へ動かせる（SPEC 7章）。
            // 落とす位置は、この枠の上端（この見出しの手前）・下端（この見出しの節の後ろ・文書の最後）に青い線で、
            // この見出しの中へ入れるときは枠を薄い青で塗って出す（OutlineDrag.cs の ShowOutlineDropMark）。
            // 線の分だけ高さが変わらないよう、透明な線をいつも引いておき、色だけ変える。
            var frame = new Border();
            frame.Child = host;
            frame.BorderThickness = new Thickness(0, 2, 0, 2);
            frame.BorderBrush = Brushes.Transparent;
            frame.Background = Brushes.Transparent;
            frame.ToolTip = display + Environment.NewLine + "ドラッグで好きな位置へ動かす（落とした位置に合わせてレベルを変え、配下も同じだけずらす）" +
                            Environment.NewLine + "右クリックで、レベル・削除";
            // 右クリックで、左クリックと同じくその見出しへ移って選んでから、メニューを出す。
            // 選んでいる見出し（エディタのカーソルの位置）と、メニューの対象をそろえるため。選んでいる印で、どの見出しのメニューか分かる。
            frame.MouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (_outlineDragging) return;

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

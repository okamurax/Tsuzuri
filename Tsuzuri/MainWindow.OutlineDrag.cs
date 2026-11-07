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
    // MainWindow の一部: アウトラインのドラッグで節を動かす。右クリックのレベルの上げ下げ・同じ階層を回す ▲▼ も、同じ書き換え（MarkdownView）を使うのでここに置く。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // アウトラインのドラッグで節を動かす（SPEC 7章）
        //
        // 見出しをつかんで運び、好きな位置（どの見出しの手前でも、文書の最後でも）へ落とす。
        // 親の外へも、レベルの違う見出しのあいだへも動かせる。落とした位置の見出しと同じレベル（中へ入れるときは1つ下）にし、配下も同じだけずらす。
        // 本文の書き換えは MarkdownView.MoveSectionTo に任せる（1回の置き換え・読み方が変わるなら取りやめ）。
        // アウトラインの外で離したら何もしない。上端・下端に寄せている間はスクロールする。
        // ---------------------------------------------------------------

        private OutlineNode _outlineDragNode;
        private Point _outlineDragStart;
        private bool _outlineDragging;
        private bool _outlineReleased;

        /// <summary>落とす位置の種類。どの見出しに対してか（_outlineDropAnchor）と組みで使う。</summary>
        private enum OutlineDrop
        {
            /// <summary>その見出しの手前に、その見出しと同じレベルで（項目の上の3分の1）。</summary>
            Before,

            /// <summary>その見出しの節のすぐ後ろに、その見出しと同じレベルで（項目の下の3分の1）。</summary>
            After,

            /// <summary>その見出しの中の最後に、その見出しの1つ下のレベルで（項目のまん中）。</summary>
            Into,

            /// <summary>文書の最後に、文書の中で一番浅いレベルで（一番下の項目より下）。見出しは使わない。</summary>
            End,
        }

        private OutlineDrop _outlineDropKind;
        private OutlineNode _outlineDropAnchor;
        private bool _outlineDropValid;
        private Border _outlineDropMark;

        /// <summary>つかんだまま上端・下端に寄せている間、少しずつスクロールする。</summary>
        private readonly DispatcherTimer _outlineAutoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        private const double OutlineEdgeZone = 24;
        private const double OutlineEdgeStep = 12;

        /// <summary>
        /// アウトラインで押すたびに、前のつかみを消す。押したのが見出しの枠なら、このあと枠の側でつかみ直す
        /// （トンネルなので木の側が先に呼ばれる）。前のつかみが残っていると、スクロールバーをつまんだときなどに、その節のドラッグが始まってしまう。
        /// </summary>
        private void OutlineTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_outlineDragging) return;
            _outlineDragNode = null;
        }

        private void OutlineTree_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_outlineDragNode == null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // 押したまま外で離したなど、離したことを受け取れなかったつかみは捨てる。
                if (!_outlineDragging) _outlineDragNode = null;
                return;
            }

            var now = e.GetPosition(OutlineTree);

            if (!_outlineDragging)
            {
                if (Math.Abs(now.X - _outlineDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(now.Y - _outlineDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                _outlineDragging = true;
                _outlineReleased = false;
                OutlineTree.CaptureMouse();
                OutlineTree.Cursor = Cursors.SizeAll;   // 四方向の矢印
            }

            ShowOutlineDropMark(now);
            if (OutlineEdgeDirection(now) != 0) _outlineAutoScroll.Start();
            e.Handled = true;
        }

        /// <summary>上端に寄せていれば -1、下端なら 1、それ以外は 0。アウトラインの外なら 0（落とさないので、スクロールもしない）。</summary>
        private int OutlineEdgeDirection(Point at)
        {
            if (at.X < 0 || at.X > OutlineTree.ActualWidth || at.Y < 0 || at.Y > OutlineTree.ActualHeight) return 0;
            if (at.Y < OutlineEdgeZone) return -1;
            if (at.Y > OutlineTree.ActualHeight - OutlineEdgeZone) return 1;
            return 0;
        }

        private void OutlineAutoScroll()
        {
            var at = Mouse.GetPosition(OutlineTree);
            var direction = _outlineDragging ? OutlineEdgeDirection(at) : 0;
            var scroll = direction != 0 ? FindDescendant<ScrollViewer>(OutlineTree) : null;
            if (scroll == null)
            {
                _outlineAutoScroll.Stop();
                return;
            }

            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + direction * OutlineEdgeStep);
            scroll.UpdateLayout();

            // スクロールした分、マウスの下の項目が変わるので、落とす位置を追わせる。
            ShowOutlineDropMark(at);
        }

        private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var found = child as T ?? FindDescendant<T>(child);
                if (found != null) return found;
            }

            return null;
        }

        private void OutlineTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_outlineDragging)
            {
                _outlineDragNode = null;
                return;
            }

            e.Handled = true;
            _outlineReleased = true;
            OutlineTree.ReleaseMouseCapture();   // LostMouseCapture から EndOutlineDrag が呼ばれる
        }

        /// <summary>
        /// 放したとき。落としたことにするのはボタンを離したときだけ（別のウィンドウへ移るなどでつかみが外れたら何もしない）。
        /// </summary>
        private void EndOutlineDrag()
        {
            if (!_outlineDragging) return;

            var node = _outlineDragNode;
            var kind = _outlineDropKind;
            var anchor = _outlineDropAnchor;
            var valid = _outlineDropValid && _outlineReleased;

            _outlineDragging = false;
            _outlineReleased = false;
            _outlineDragNode = null;
            _outlineAutoScroll.Stop();
            OutlineTree.Cursor = null;
            ShowDropMarkOn(null, false);

            if (valid && node != null) MoveSectionTo(node, kind, anchor);
        }

        /// <summary>マウスの位置から落とす位置を決め、青い線を出す。動かせない・並びもレベルも変わらない位置なら出さない。</summary>
        private void ShowOutlineDropMark(Point at)
        {
            _outlineDropValid = false;
            _outlineDropAnchor = null;
            _outlineDropKind = OutlineDrop.End;

            // アウトラインの外（本文の側・ステータスバーの辺りなど）では落とさない。そこで離せば取りやめになる。
            if (at.X < 0 || at.Y < 0 || at.X > OutlineTree.ActualWidth || at.Y > OutlineTree.ActualHeight)
            {
                ShowDropMarkOn(null, false);
                return;
            }

            var visible = _outlineOrder.Where(item => item.IsVisible && (item.Tag as OutlineNode) != null
                && ((OutlineNode)item.Tag).Frame != null && ((OutlineNode)item.Tag).Frame.IsVisible).ToList();
            if (visible.Count == 0)
            {
                ShowDropMarkOn(null, false);
                return;
            }

            // マウスの下の項目を高さで3つに分ける（SPEC 7章）。
            //   上の3分の1 … その見出しの手前に、その見出しと同じレベルで。線はその項目の上端
            //   まん中     … その見出しの中の最後に、1つ下のレベルで。項目を薄い青で塗る
            //   下の3分の1 … その見出しの節のすぐ後ろに、その見出しと同じレベルで。線はその項目の下端。
            //                開いていて子が見えていれば、最初の子の手前に、子と同じレベルで（線は子の上端）
            // 線は項目の枠に引くので、項目の字下げの分だけ左が空き、入るレベルが線の左端で分かる。
            // 一番下の項目より下なら、文書の最後に一番浅いレベルで。
            var index = -1;
            var zone = 0;   // -1 上、0 まん中、1 下
            for (var i = 0; i < visible.Count; i++)
            {
                var frame = ((OutlineNode)visible[i].Tag).Frame;
                var y = OutlineTree.TranslatePoint(at, frame).Y;
                if (y < 0) { index = i; zone = -1; break; }
                if (y < frame.ActualHeight)
                {
                    index = i;
                    zone = y < frame.ActualHeight / 3 ? -1 : y >= frame.ActualHeight * 2 / 3 ? 1 : 0;
                    break;
                }
            }

            OutlineDrop kind;
            OutlineNode anchor;
            if (index < 0)
            {
                kind = OutlineDrop.End;
                anchor = null;
            }
            else
            {
                anchor = (OutlineNode)visible[index].Tag;
                kind = zone < 0 ? OutlineDrop.Before : zone == 0 ? OutlineDrop.Into : OutlineDrop.After;

                var item = visible[index];
                // ただし、その最初の子がつかんでいる見出しなら、「この見出しの節の後ろ」のまま（子の手前では何も変わらないため）。
                if (kind == OutlineDrop.After && item.IsExpanded && item.Items.Count > 0 && index + 1 < visible.Count
                    && visible[index + 1].Tag != _outlineDragNode)
                {
                    kind = OutlineDrop.Before;
                    anchor = (OutlineNode)visible[index + 1].Tag;
                }
            }

            if (!CanDrop(_outlineDragNode, kind, anchor))
            {
                ShowDropMarkOn(null, false);
                return;
            }

            _outlineDropValid = true;
            _outlineDropKind = kind;
            _outlineDropAnchor = anchor;

            switch (kind)
            {
                case OutlineDrop.Before:
                    ShowDropMarkOn(anchor.Frame, false);
                    break;
                case OutlineDrop.After:
                    ShowDropMarkOn(anchor.Frame, true);
                    break;
                case OutlineDrop.Into:
                    ShowDropIntoMark(anchor.Frame);
                    break;
                default:
                    ShowDropMarkOn(((OutlineNode)visible[visible.Count - 1].Tag).Frame, true);
                    break;
            }
        }

        /// <summary>
        /// 落とす位置を、本文の見出しの番号で表す。beforeIndex の見出しの手前（entries.Count なら文書の最後）に、level で入る。
        /// from は動かす見出しの番号、anchor は落とす先の見出しの番号（End では使わない）。
        /// 自分の配下へ入る・並びもレベルも変わらない・レベルが 1〜6 に収まらないときは false。
        /// </summary>
        private static bool DropTarget(List<OutlineHeading> entries, int from, OutlineDrop kind, int anchor,
            out int beforeIndex, out int level)
        {
            beforeIndex = entries.Count;
            level = entries[from].Level;

            var end = SectionEndIndex(entries, from);

            switch (kind)
            {
                case OutlineDrop.Before:
                    beforeIndex = anchor;
                    level = entries[anchor].Level;
                    break;
                case OutlineDrop.After:
                    if (anchor > from && anchor < end) return false;   // 自分の配下の後ろ
                    beforeIndex = SectionEndIndex(entries, anchor);
                    level = entries[anchor].Level;
                    break;
                case OutlineDrop.Into:
                    if (anchor >= from && anchor < end) return false;  // 自分自身・自分の配下の中
                    beforeIndex = SectionEndIndex(entries, anchor);
                    level = entries[anchor].Level + 1;
                    break;
                default:
                    level = ShallowestOutside(entries, from);
                    break;
            }

            if (level < 1 || level > 6) return false;
            if (beforeIndex > from && beforeIndex < end) return false;   // 自分の配下の手前

            // 自分の手前・自分の節のすぐ後ろで、レベルも同じなら、何も変わらない。
            if ((beforeIndex == from || beforeIndex == end) && level == entries[from].Level) return false;

            return true;
        }

        /// <summary>その位置へ落とせるか（線を出すか）。</summary>
        private bool CanDrop(OutlineNode moving, OutlineDrop kind, OutlineNode anchor)
        {
            if (moving == null || Active == null) return false;

            var order = _outlineOrder.Select(item => item.Tag as OutlineNode).ToList();
            var from = order.IndexOf(moving);
            if (from < 0) return false;

            var entries = Active.GetOutline().Entries;
            if (entries.Count != order.Count) return false;   // アウトラインが追いついていない

            var to = -1;
            if (kind != OutlineDrop.End)
            {
                to = order.IndexOf(anchor);
                if (to < 0) return false;
            }

            int beforeIndex, level;
            return DropTarget(entries, from, kind, to, out beforeIndex, out level);
        }

        /// <summary>index の見出しの節の終わり（同じかそれより浅い次の見出しの番号。無ければ entries.Count）。</summary>
        private static int SectionEndIndex(List<OutlineHeading> entries, int index)
        {
            for (var k = index + 1; k < entries.Count; k++)
            {
                if (entries[k].Level <= entries[index].Level) return k;
            }

            return entries.Count;
        }

        /// <summary>動かす節（index とその配下）の外にある見出しのうち、一番浅いレベル。無ければ動かす見出しのレベル。</summary>
        private static int ShallowestOutside(List<OutlineHeading> entries, int index)
        {
            var end = entries.Count;
            for (var k = index + 1; k < entries.Count; k++)
            {
                if (entries[k].Level <= entries[index].Level) { end = k; break; }
            }

            var shallowest = 0;
            for (var k = 0; k < entries.Count; k++)
            {
                if (k >= index && k < end) continue;
                if (shallowest == 0 || entries[k].Level < shallowest) shallowest = entries[k].Level;
            }

            return shallowest == 0 ? entries[index].Level : shallowest;
        }

        /// <summary>落とす位置の青い線。frame の上端（after なら下端）に出す。null で消す。</summary>
        private void ShowDropMarkOn(Border frame, bool after)
        {
            if (_outlineDropMark != null)
            {
                _outlineDropMark.BorderBrush = Brushes.Transparent;
                _outlineDropMark.Background = Brushes.Transparent;
            }

            _outlineDropMark = frame;
            if (frame == null) return;

            var accent = (Brush)FindResource("AccentBrush");
            frame.BorderBrush = after
                ? new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5),
                        new GradientStop(((SolidColorBrush)accent).Color, 0.5), new GradientStop(((SolidColorBrush)accent).Color, 1),
                    }, 90)
                : new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(((SolidColorBrush)accent).Color, 0), new GradientStop(((SolidColorBrush)accent).Color, 0.5),
                        new GradientStop(Colors.Transparent, 0.5), new GradientStop(Colors.Transparent, 1),
                    }, 90);
        }

        /// <summary>その見出しの中へ落とす印。枠を薄い青で塗り、上下に線を引く。</summary>
        private void ShowDropIntoMark(Border frame)
        {
            ShowDropMarkOn(null, false);
            if (frame == null) return;

            _outlineDropMark = frame;

            var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
            frame.Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            frame.BorderBrush = new SolidColorBrush(accent);
        }

        /// <summary>節を落とした位置へ動かし（レベルも合わせる。SPEC 7章）、動かした見出しへ移る。</summary>
        private void MoveSectionTo(OutlineNode node, OutlineDrop kind, OutlineNode anchor)
        {
            var view = Active;
            if (view == null) return;

            if (!CanWriteTo(view))
            {
                Status("このページはファイルから消えているため、動かせません。");
                return;
            }

            // アウトラインの行番号は少し遅れて追いつくので、離した時点の本文から、同じ名前で一番近い見出しを探し直す。
            var entries = view.GetOutline().Entries;
            var index = NearestHeading(entries, node.Text, node.Line);
            if (index < 0) return;

            var to = -1;
            if (kind != OutlineDrop.End)
            {
                to = NearestHeading(entries, anchor.Text, anchor.Line);
                if (to < 0) return;
            }

            int beforeIndex, level;
            if (!DropTarget(entries, index, kind, to, out beforeIndex, out level)) return;

            var beforeLine = beforeIndex < entries.Count ? entries[beforeIndex].Line : -1;
            var line = view.MoveSectionTo(entries[index].Line, beforeLine, level);

            if (line == MarkdownView.LevelOutOfRange)
            {
                Status("その位置に合わせてレベルをずらすと、配下の見出しが ###### より深くなるため、動かしませんでした。");
                return;
            }

            if (line == MarkdownView.LevelNotAtx)
            {
                Status("下線式（=== / ---）の見出しを含むため、レベルを合わせられず、動かしませんでした。エディタで # の見出しに書き換えてください。");
                return;
            }

            if (line == MarkdownView.LevelNotPlain)
            {
                Status("引用・リスト・前付け（文書の頭の --- で囲んだ部分）の中の見出しが関わるため、動かしませんでした（途中で切れる・レベルを合わせられないため）。");
                return;
            }

            if (line == MarkdownView.MoveChangesMeaning)
            {
                Status("動かすと Markdown の読み方が変わる（見出しが消える・増える・リストや引用に入る）ため、取りやめました。" +
                       "閉じていないコードブロックなどが無いか確かめてください。");
                return;
            }

            if (line == MarkdownView.MoveUnchanged) return;

            if (line < 0)
            {
                Status("その位置へは動かせません（自分の配下には入れられません）。");
                return;
            }

            Status("「" + node.Text + "」を動かしました。");

            view.GoToLine(line);
        }

        /// <summary>
        /// その見出しのレベルを1つ上げる・下げる。配下の小見出しも同じだけずらす（親子の形を保つ。2026-10-25。前はその行の # だけ変えていた）。
        /// 位置は動かさない。節を動かす処理（MoveSectionTo）を、その場に置いたままレベルだけ変える形で使う。
        /// 変えたあとの見出しの顔ぶれを確かめてから1回で書き換えるので、元に戻すも1回で戻る。
        /// </summary>
        private void ChangeHeadingLevel(OutlineNode node, int delta)
        {
            var view = Active;
            if (view == null) return;

            if (!CanWriteTo(view))
            {
                Status("このページはファイルから消えているため、変えられません。");
                return;
            }

            // アウトラインの行番号は少し遅れて追いつくので、押した時点の本文から、同じ名前で一番近い見出しを探し直す。
            var entries = view.GetOutline().Entries;
            var index = NearestHeading(entries, node.Text, node.Line);
            if (index < 0) return;

            var line = entries[index].Line;
            var level = entries[index].Level + delta;
            var name = HeadingText.Display(node.Text);

            if (level < 1 || level > 6)
            {
                Status(delta < 0 ? "これ以上レベルを上げられません（# は1つが最小）。" : "これ以上レベルを下げられません（# は6つが最大）。");
                return;
            }

            var result = view.MoveSectionTo(line, line, level);

            if (result == MarkdownView.LevelNotAtx)
            {
                Status("下線式（=== / ---）の見出しは # が無いので、ここではレベルを変えられません。エディタで # の見出しに書き換えてください。");
                return;
            }

            if (result == MarkdownView.LevelNotPlain)
            {
                Status("引用・リスト・前付けの中の見出しは、ここではレベルを変えられません。エディタで直してください。");
                return;
            }

            if (result == MarkdownView.LevelOutOfRange)
            {
                Status(delta < 0
                    ? "これ以上レベルを上げられません（# は1つが最小）。"
                    : "小見出しが # 6つより深くなるため、レベルを下げられません。");
                return;
            }

            if (result == MarkdownView.MoveChangesMeaning)
            {
                Status("レベルを変えると、ほかの見出しの読み方が変わってしまうため、変えませんでした。");
                return;
            }

            if (result < 0)
            {
                Status("見出しが見つからなかったため、変えませんでした。もう一度押してください。");
                return;
            }

            node.Level = level;
            Status("「" + name + "」を小見出しも一緒に " + new string('#', level) + " にしました。");
        }

        /// <summary>
        /// その見出しと同じ階層の見出しを1つずつ回す（まとまりの先頭の ▲▼。SPEC 7章。2026-11-06）。孫と本文は一緒に動く。
        /// 押したあとは、回したあとの先頭の見出しへ移る（次に来た塊を見せる）。1回の書き換えなので、元に戻すも1回で戻る。
        /// 回したら、アウトラインをその場で組み直す（2026-11-07）。前は 60ms 後に組み直していて、そのあいだに連打すると古いボタンを押すことになり、
        /// 同じ名前の見出しが別の階層にあると、名前で探し直したときにそちらを拾って、別のまとまりを回すことがあった。
        /// 探し直すときも、名前だけでなくレベルも同じ見出しにする。
        /// </summary>
        private void RotateOutlineSiblings(OutlineNode node, bool up)
        {
            var view = Active;
            if (view == null) return;

            if (!CanWriteTo(view))
            {
                Status("このページはファイルから消えているため、変えられません。");
                return;
            }

            // 押してから落ち着くまでの時間を perf.log に書く（2026-11-07。app.json の perfLog が true のときだけ）。
            // アウトラインは少し遅れて組み直すので、書き出すのはその後（_outlineRefresh の Tick から FinishStepsLater）。
            PerfLog.BeginSteps(up ? "回す ▲" : "回す ▼");
            var started = PerfLog.Start();

            // 押した時点の本文から、同じ名前・同じレベルで一番近い見出しを探し直す（アウトラインが本文に追いついていないときのため）。
            var entries = view.GetOutline().Entries;
            var index = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Level != node.Level || !string.Equals(entries[i].Text, node.Text, StringComparison.Ordinal)) continue;
                if (index < 0 || Math.Abs(entries[i].Line - node.Line) < Math.Abs(entries[index].Line - node.Line)) index = i;
            }
            PerfLog.Step("見出しを探す", started);
            PerfLog.StepNote("見出し数", entries.Count);
            if (index < 0) { PerfLog.EndSteps(); return; }

            var result = view.RotateSiblings(entries[index].Line, up);
            if (result < 0) PerfLog.EndSteps();

            if (result == MarkdownView.SiblingsTooFew) return;

            if (result == MarkdownView.LevelNotPlain)
            {
                Status("引用・リスト・前付け（文書の頭の --- で囲んだ部分）の中の見出しがあるため、回せません。エディタで直してください。");
                return;
            }

            if (result == MarkdownView.MoveChangesMeaning)
            {
                Status("回すと、ほかの見出しの読み方が変わってしまう（見出しが消える・増える・リストや引用に入る）ため、回しませんでした。閉じていないコードブロックなどが無いか確かめてください。");
                return;
            }

            if (result < 0)
            {
                Status("見出しが見つからなかったため、回しませんでした。もう一度押してください。");
                return;
            }

            started = PerfLog.Start();
            view.GoToLine(result);
            PerfLog.Step("行へ移る", started);
            _rotateTiming = PerfLog.Stepping;

            RefreshOutlineNow();
        }

        /// <summary>回すボタンの時間を測っている途中か。アウトラインを組み直したあとで書き出す。</summary>
        private bool _rotateTiming;
    }
}

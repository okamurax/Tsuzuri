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
    // MainWindow の一部: アウトラインの右クリック（見出しの追加・削除・名前の変更・レベル・ブックマーク）。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 見出しを足す・消す・名前を変える —— アウトラインの右クリック（SPEC 7章）
        //
        // 本文の書き換えは MarkdownView に任せる（1回の置き換え・読み方が変わるなら取りやめ）。
        // ---------------------------------------------------------------

        /// <summary>
        /// そのページを書き換えてよいか。ファイルからページが消えていたら書かない。
        /// IsReadOnly は、消えたことに気づいた後でしか立たないので、それだけでは足りない
        /// （気づく前に書くと、成功したと知らせたあとで、保存されずに消える）。
        /// </summary>
        private static bool CanWriteTo(MarkdownView view)
        {
            return view != null && !view.IsReadOnly && view.Owner != null && view.Owner.Contains(view.PageId);
        }

        private enum OutlineEdit
        {
            /// <summary>その見出しの節のすぐ後ろに、同じレベルで足す。</summary>
            Sibling,

            /// <summary>その見出しの中の最後に、1つ下のレベルで足す。</summary>
            Child,

            Rename,
        }

        /// <summary>右クリックのメニューを出している・名前を打っている見出しの枠。薄く塗って、どれに対する操作か見せる。</summary>
        private Border _outlineMenuFrame;

        private void MarkOutlineMenuFrame(Border frame)
        {
            if (_outlineMenuFrame != null && _outlineMenuFrame != _outlineDropMark) _outlineMenuFrame.Background = Brushes.Transparent;

            _outlineMenuFrame = frame;
            if (frame != null) frame.Background = (Brush)FindResource("HoverFill");
        }

        /// <summary>
        /// 名前を打つ欄を、アウトラインの中に出す（SPEC 7章）。別の窓（ポップアップ）に出すと、日本語入力の動きがおかしくなるため。
        /// 追加は、見出しが入る場所に空の段を先に作って、そこに欄を出す（本文はまだ書き換えない）。
        /// 名前の変更は、その見出しの名前の場所を欄に替える。本文に書いてあるとおりの名前（書式も含む）から始める。
        /// Enter で決める。Esc か欄の外を押すとやめる（外を押して決めると、思わぬ見出しができる・名前が変わるため）。
        /// 打っている間にアウトラインが組み直されたら（本文の見出しが変わった）、欄は消え、やめたことになる。
        /// </summary>
        private void BeginOutlineName(OutlineNode node, OutlineEdit mode)
        {
            var view = Active;
            if (view == null) return;

            if (!CanWriteTo(view))
            {
                Status("このページはファイルから消えているため、書き換えられません。");
                return;
            }

            var initial = string.Empty;
            if (mode == OutlineEdit.Rename)
            {
                var entries = view.GetOutline().Entries;
                var index = NearestHeading(entries, node.Text, node.Line);
                if (index < 0)
                {
                    Status("見出しが見つかりません。");
                    return;
                }

                initial = view.HeadingName(entries[index].Line);
                if (initial == null)
                {
                    Status("この見出しは、ここでは名前を変えられません（引用・リストの中や、2行以上の下線式の見出し）。エディタで直してください。");
                    return;
                }
            }

            if (node == null) return;
            var item = _outlineOrder.FirstOrDefault(each => each.Tag == node);
            if (item == null) return;   // 組み直しで消えた

            var box = new TextBox();
            box.Text = initial;
            box.Padding = new Thickness(1, 0, 1, 0);
            box.MinWidth = 80;
            box.VerticalAlignment = VerticalAlignment.Center;
            box.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");

            switch (mode)
            {
                case OutlineEdit.Sibling:
                    box.ToolTip = "「" + node.Text + "」と同じ階層（" + new string('#', node.Level) + "）に追加する見出しの名前。Enter で決定、Esc でやめる";
                    break;
                case OutlineEdit.Child:
                    box.ToolTip = "「" + node.Text + "」の下の階層（" + new string('#', node.Level + 1) + "）に追加する見出しの名前。Enter で決定、Esc でやめる";
                    break;
                default:
                    box.ToolTip = "見出しの新しい名前。Enter で決定、Esc でやめる";
                    break;
            }

            // 欄を置く。追加は、見出しが入る場所に空の段を作る。名前の変更は、名前の表示と差し替える。
            TreeViewItem slot = null;
            ItemCollection slotOwner = null;

            if (mode == OutlineEdit.Rename)
            {
                node.Panel.Children.Remove(node.Label);
                node.Panel.Children.Add(box);   // 最後の子なので、残りの幅いっぱいに広がる
            }
            else
            {
                slot = new TreeViewItem();
                slot.Padding = new Thickness(2);
                slot.Header = box;
                slot.IsExpanded = true;

                if (mode == OutlineEdit.Child)
                {
                    // その見出しの中の最後。たたんでいれば開く。
                    item.IsExpanded = true;
                    slotOwner = item.Items;
                    slotOwner.Add(slot);
                }
                else
                {
                    // その見出しのすぐ後ろ（木の上では、配下を含めた節の後ろ）。
                    TreeViewItem parent;
                    slotOwner = _outlineParents.TryGetValue(item, out parent) ? parent.Items : OutlineTree.Items;
                    slotOwner.Insert(slotOwner.IndexOf(item) + 1, slot);
                }
            }

            var done = false;
            Action<bool> finish = commit =>
            {
                if (done) return;
                done = true;

                var name = box.Text;

                if (slot != null)
                {
                    if (slotOwner.Contains(slot)) slotOwner.Remove(slot);
                }
                else if (box.Parent == node.Panel)
                {
                    node.Panel.Children.Remove(box);
                    node.Panel.Children.Add(node.Label);
                }

                MarkOutlineMenuFrame(null);

                // 決めたら、書き換えてからエディタへ移る。やめたら、エディタへ入力を返す。
                if (commit) ApplyOutlineName(node, mode, name, initial);
                else if (Active != null) Active.TextArea.Focus();
            };

            box.PreviewKeyDown += (sender, e) =>
            {
                // 日本語入力の変換中の Enter・Esc は、変換を決める・取り消すためのもの。ここでは受け取らない。
                if (e.Key == Key.ImeProcessed) return;

                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    finish(true);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    finish(false);
                }
            };
            box.LostKeyboardFocus += delegate { finish(false); };

            MarkOutlineMenuFrame(node.Frame);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (done) return;

                if (slot != null) slot.BringIntoView();
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();

                // 欄に入力が入らなかった（その間にアウトラインが組み直された等）なら、やめたことにする。
                if (!box.IsKeyboardFocusWithin) finish(false);
            }), DispatcherPriority.Input);
        }

        private void ApplyOutlineName(OutlineNode node, OutlineEdit mode, string name, string initial)
        {
            var view = Active;
            if (view == null) return;

            name = (name ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (mode == OutlineEdit.Rename && name == (initial ?? string.Empty).Trim()) return;   // 変えていない

            if (name.Length == 0)
            {
                Status("名前が空なので、何もしませんでした。");
                return;
            }

            // 名前を打っている間に本文が変わっているかもしれないので、決めた時点の本文から見出しを探し直す。
            var headingLine = 0;
            if (node != null)
            {
                var entries = view.GetOutline().Entries;
                var index = NearestHeading(entries, node.Text, node.Line);
                if (index < 0)
                {
                    Status("見出しが見つからないため、何もしませんでした。");
                    return;
                }

                headingLine = entries[index].Line;
            }

            int result;
            switch (mode)
            {
                case OutlineEdit.Sibling:
                    result = view.InsertHeading(headingLine, false, name);
                    break;
                case OutlineEdit.Child:
                    result = view.InsertHeading(headingLine, true, name);
                    break;
                default:
                    result = view.RenameHeading(headingLine, name);
                    break;
            }

            if (result == MarkdownView.HeadingNameInvalid)
            {
                Status("その名前では見出しとして読めない（ほかの見出しが変わる・最後の # が消える・行の頭が # や - だと形が変わる）ため、何もしませんでした。");
                return;
            }

            if (result == MarkdownView.HeadingNotEditable)
            {
                Status(mode == OutlineEdit.Rename
                    ? "この見出しは、ここでは名前を変えられません。エディタで直してください。"
                    : "すぐ後ろが引用・リストの中の見出しなので、ここには足せません（引用やリストが途中で切れるため）。エディタで書いてください。");
                return;
            }

            if (result == MarkdownView.LevelOutOfRange)
            {
                Status("###### より深い見出しは作れません。");
                return;
            }

            if (result <= 0)
            {
                Status("見出しが見つからないため、何もしませんでした。");
                return;
            }

            if (mode == OutlineEdit.Rename)
            {
                Status("見出しの名前を「" + name + "」にしました。");
                return;
            }

            Status("見出し「" + name + "」を追加しました。");

            // 足した見出しへ移る。
            view.GoToLine(result);
        }

        /// <summary>見出しを中身（小見出しも含む）ごと消す。消す前に確かめる。</summary>
        private void DeleteOutlineHeading(OutlineNode node)
        {
            var view = Active;
            if (view == null) return;

            if (!CanWriteTo(view))
            {
                Status("このページはファイルから消えているため、書き換えられません。");
                return;
            }

            // 確かめる前の姿を覚えておく（2026-10-27）。確かめている間に読み直し（Activated）などで本文が変わっていたら、
            // 探し直すと別の場所の同じ名前の見出しを消しかねないので、消さずに知らせる。
            var owner = view.Owner;
            var pageId = view.PageId ?? string.Empty;
            var before = view.Text;

            MarkOutlineMenuFrame(node.Frame);
            MessageBoxResult answer;
            try
            {
                answer = MessageBox.Show(this,
                    "「" + node.Text + "」を削除しますか？" + Environment.NewLine + Environment.NewLine +
                    "見出しと、その中身（小見出しも含む）が本文から消えます。" + Environment.NewLine +
                    "消した直後なら、エディタの元に戻す（Ctrl+Z）で戻せます。",
                    "見出しの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            }
            finally
            {
                MarkOutlineMenuFrame(null);
            }

            if (answer != MessageBoxResult.Yes) return;

            // 確かめている間に、ページ・文書・本文が変わっていないか確かめる。変わっていたら消さない（探し直さない）。
            if (!ReferenceEquals(Active, view) || !ReferenceEquals(view.Owner, owner)
                || !string.Equals(view.PageId ?? string.Empty, pageId, StringComparison.Ordinal)
                || !string.Equals(view.Text, before, StringComparison.Ordinal))
            {
                MessageBox.Show(this,
                    "確かめている間に本文が変わった（外での書き換えの読み直しなど）ため、「" + node.Text + "」は削除しませんでした。" + Environment.NewLine +
                    "アウトラインで見出しを確かめてから、もう一度やり直してください。",
                    "見出しの削除", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 本文は確かめる前と同じなので、覚えていた行の見出しを消す。
            // アウトラインの更新が追いついていなかったときだけ、確かめる前と同じ本文の中で一番近い同じ名前の見出しにする。
            var entries = view.GetOutline().Entries;
            var index = entries.FindIndex(entry => entry.Line == node.Line
                                                   && string.Equals(entry.Text, node.Text, StringComparison.Ordinal));
            if (index < 0) index = NearestHeading(entries, node.Text, node.Line);
            if (index < 0)
            {
                Status("見出しが見つからないため、削除しませんでした。");
                return;
            }

            var result = view.DeleteSection(entries[index].Line);

            if (result == MarkdownView.MoveChangesMeaning)
            {
                Status("削除すると Markdown の読み方が変わる（ほかの見出しが消える・増える）ため、取りやめました。" +
                       "閉じていないコードブロックなどが無いか確かめてください。");
                return;
            }

            if (result == MarkdownView.HeadingNotEditable)
            {
                Status("引用・リストの中の見出しが関わるため、ここでは削除できません（引用やリストが途中で切れるため）。エディタで消してください。");
                return;
            }

            if (result < 0)
            {
                Status("見出しが見つからないため、削除しませんでした。");
                return;
            }

            Status("「" + node.Text + "」を削除しました。");
        }
    }
}

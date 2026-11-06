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
    // MainWindow の一部: アウトラインの右クリックの「削除…」と、書き換えてよいかの確かめ（CanWriteTo）。
    // レベルと同じ階層を回す ▲▼ は MainWindow.OutlineDrag.cs、メニューは MainWindow.Outline.cs（ShowOutlineMenu）にある。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 見出しを消す —— アウトラインの右クリック（SPEC 7章）
        //
        // 本文の書き換えは MarkdownView に任せる（1回の置き換え・読み方が変わるなら取りやめ）。
        // 前はここで見出しを足す・名前を変える欄も出していたが、2026-10-25 にメニューから外し、2026-11-06 に処理も消した。
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

        /// <summary>削除を確かめている間の見出しの枠。薄く塗って、どれを消すのか見せる。</summary>
        private Border _outlineMenuFrame;

        private void MarkOutlineMenuFrame(Border frame)
        {
            if (_outlineMenuFrame != null && _outlineMenuFrame != _outlineDropMark) _outlineMenuFrame.Background = Brushes.Transparent;

            _outlineMenuFrame = frame;
            if (frame != null) frame.Background = (Brush)FindResource("HoverFill");
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

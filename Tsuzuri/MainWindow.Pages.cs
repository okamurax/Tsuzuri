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
    // MainWindow の一部: 切り出しと戻し・保存・一覧の行。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 切り出しと戻し（SPEC 5章）
        //
        // どちらも同じファイルの中だけで完結する。
        // ファイルの作成も削除も起きないので、気軽に往復できる。
        // ---------------------------------------------------------------

        /// <summary>
        /// サブページへのリンクの表示名。中身からは取らず、いつも同じ文字にする（SPEC 5章。2026-10-14）。
        /// 「先へ進む」と読める印にする（「...」は小さすぎて押しづらく、「［...］」を経てこれにした）。
        /// リンクの表示名の中なので、> は引用の記号にはならない。
        /// </summary>
        private const string SubpageLabel = ">>";

        /// <summary>
        /// サブページを作り、その場にリンクを残す（SPEC 5章）。
        /// 範囲を選んでいれば、その範囲をサブページへ移す。選んでいなければ、空のサブページを作り、カーソルの位置にリンクを入れる。
        /// 選んでいなくてもカーソルの行に文字があれば、その文字をリンクの表示名にする。それ以外の表示名はいつも SubpageLabel（[>>](#ID)）。
        /// </summary>
        private void Extract_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (view == null || view.Owner == null)
            {
                Status("先にファイルを開いてください。");
                return;
            }

            if (view.IsReadOnly)
            {
                Status("このページはファイルから消えているため、サブページを作れません。");
                return;
            }

            // 選んでいれば、その範囲（見出し単位とは限らないので推測しない）。選んでいなければカーソルの位置に空のページ。
            var selected = !string.IsNullOrEmpty(view.SimpleSelectedText.Trim());
            var rangeStart = selected ? view.SelectionStart : view.CaretOffset;
            var rangeLength = selected ? view.SelectionLength : 0;
            var body = selected ? view.SimpleSelectedText : string.Empty;
            var label = SubpageLabel;

            // 選んでいなくても、カーソルの行に文字があれば、その文字をリンクの表示名にして空のページを作る（2026-10-23）。
            // 行頭の # や - などはリンクの外に残す。中に入れると見出しや箇条書きでなくなるため。
            // 文字はリンクの表示名になるだけで、新しいページには写さない。
            int labelStart, labelLength;
            if (!selected && LineLabelRange(view, out labelStart, out labelLength))
            {
                rangeStart = labelStart;
                rangeLength = labelLength;
                label = view.Document.GetText(labelStart, labelLength);
            }

            var document = view.Owner;
            var name = selected ? PageName(body) : label != SubpageLabel ? label : "（空のページ）";
            var id = document.AddPage(body);
            var page = document.PageOf(id);
            var at = document.IndexOf(id);

            // ページの追加とリンクへの置き換えを、元に戻す1回の操作にする。
            // 別々だと、元に戻して孤立したページを消したあとにやり直したとき、
            // リンクだけが戻って指す先が無く、本文が消える。
            // 元に戻すときにページを消すのは、切り出したときのままのときだけ。
            // 切り出したあとにそのページを書き換えていたら、消さずに残す（孤立ページとして見つけられる）。
            var replaced = view.ReplaceRange(rangeStart, rangeLength,
                "[" + label + "](#" + id + ")",
                () => document.RestorePage(page, at),
                () =>
                {
                    if (document.TextOf(id) == body) document.RemovePage(id);
                    else Status("作ったサブページは書き換えられているので、消さずに残しました（孤立ページ）。");
                });

            if (!replaced)
            {
                document.RemovePage(id);
                Status("選んだ範囲を置き換えられなかったので、サブページの作成をやめました。");
                return;
            }

            Status("サブページを作りました: " + name);

            PushPage(view, view.Owner, id);
        }

        /// <summary>行頭の印（見出し・引用・箇条書き・番号・チェックボックス）。リンクの外に残す。</summary>
        private static readonly System.Text.RegularExpressions.Regex LinePrefix =
            new System.Text.RegularExpressions.Regex(@"^[ \t]*(?:>[ \t]*)*(?:#{1,6}[ \t]+|(?:[-*+]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)?");

        /// <summary># の見出しの閉じの #。名前の後ろの「空白 + # の並び」か、名前が # の並びだけのもの（CommonMark の規則）。</summary>
        private static readonly System.Text.RegularExpressions.Regex ClosingHashes =
            new System.Text.RegularExpressions.Regex(@"(?:^|[ \t]+)#+$");

        /// <summary>
        /// カーソルの行で、リンクの表示名にする文字の範囲。行頭の印・# の見出しの閉じの #・行末の空白は含めない。
        /// 文字が無い行や、[ ] を含む行（すでにリンクがあるなど。表示名に入れると崩れる）は false。
        /// 行末が \ の行（] が飾りの外しになってリンクにならない）、表・コードの中・区切り線の行も false（2026-10-24）。
        /// </summary>
        private static bool LineLabelRange(MarkdownView view, out int start, out int length)
        {
            var line = view.Document.GetLineByOffset(view.CaretOffset);
            start = 0;
            length = 0;

            var kind = view.GetLineInfo(line.LineNumber).Kind;
            if (kind == LineKind.Code || kind == LineKind.Fence || kind == LineKind.Table
                || kind == LineKind.Rule || kind == LineKind.Setext) return false;

            var text = view.Document.GetText(line.Offset, line.Length);
            var prefix = LinePrefix.Match(text).Length;
            var content = text.Substring(prefix).TrimEnd();

            // # の見出しの閉じの #（`## 予定 ##` の後ろの ##）もリンクの外に残す（2026-10-27）。
            // 前は `## [予定 ##](#p1)` になり、閉じの # が表示名に入っていた。閉じの # は前に空白があるものだけ（`C#` の # は名前の一部）。
            if (text.Substring(0, prefix).TrimEnd(' ', '\t').EndsWith("#", StringComparison.Ordinal))
                content = ClosingHashes.Replace(content, string.Empty).TrimEnd();

            start = line.Offset + prefix;
            length = content.Length;
            return content.Length > 0 && content.IndexOfAny(new[] { '[', ']' }) < 0 && !content.EndsWith("\\");
        }

        /// <summary>
        /// 今開いているサブページを、親の本文に戻す。
        ///
        /// 親ではなく子で実行する。親でやるには戻したいリンクにカーソルを置く必要があるが、
        /// リンクの上をクリックするとそのページが開いてしまい、指定しづらい。
        /// 子で実行すれば「今読んでいるこのページを畳む」という一意な操作になる。
        /// </summary>
        private void Inline_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (view == null || view.Owner == null) return;

            var document = view.Owner;
            var id = view.PageId;

            if (string.IsNullOrEmpty(id))
            {
                Status("解除したいサブページを開いてから押してください。");
                return;
            }

            // 親は、スタックで左隣にある実際にたどってきたページを先に探す（SPEC 5.3）。
            var index = _stack.IndexOf(view);
            var preferred = index > 0 && ReferenceEquals(_stack[index - 1].Owner, document)
                ? _stack[index - 1].PageId
                : null;

            string parentId;
            int offset, length;

            if (!document.FindLinkTo(id, preferred, out parentId, out offset, out length))
            {
                Status("このページを指すリンクが見つかりません（自分の中や子ページの中のリンクは使いません）。");
                return;
            }

            // 先に自分を閉じて親へ移る。本文を書き換えてから閉じようとすると、
            // 保存できずに閉じるのを断られたとき、消えたページの画面が残り、
            // そこへ打った内容が行き場を失う。

            // スタックの先頭はいつもファイルのルートで、ルートは戻せない（親が無い）ので、ここへは来ないはず。
            // 来たら、自分を閉じずに親を書き換えることになるので、やめる。
            if (index <= 0)
            {
                Status("このページは解除できません。");
                return;
            }

            PopTo(index - 1);

            if (_stack.Contains(view))
            {
                Status("ページを閉じられなかったので、解除をやめました。");
                return;
            }

            // 閉じる間に保存が走る。念のため位置は探し直す。
            if (!document.FindLinkTo(id, preferred, out parentId, out offset, out length))
            {
                Status("このページを指すリンクが見つかりません。");
                return;
            }

            var body = document.TextOf(id) ?? string.Empty;
            var page = document.PageOf(id);
            var at = document.IndexOf(id);
            var parent = FindView(document, parentId);

            // 親を開いていれば、そちらの編集面を直す。開いていなければ文書を直す。
            // 編集面では、ページの削除もリンクの置き換えと同じ1回の操作にする。
            // 別々だと、親で元に戻したときにリンクだけが戻り、本文はどこにも無くなる。
            bool replaced;

            if (parent != null)
            {
                // やり直し（Ctrl+Y）でもページを消す。ただし戻したときのままのときだけ。
                // 元に戻したあと子ページを開いて書き足していたら、消すとその分が失われるので残す。
                // 親には戻したときの本文がもう一度入るが、子ページの新しい内容は孤立ページとして残る。
                replaced = parent.ReplaceRange(offset, length, body,
                    () =>
                    {
                        if (document.TextOf(id) == body) document.RemovePage(id);
                        else Status("解除したあとに書き換えられたページなので、消さずに残しました（孤立ページ）。");
                    },
                    () => document.RestorePage(page, at));
            }
            else
            {
                replaced = document.ReplaceIn(parentId, offset, length, body);
                if (replaced) document.RemovePage(id);
            }

            // 置き換えられなかったのにページだけ消すと、本文が失われる。
            if (!replaced)
            {
                Status("リンクを本文に置き換えられなかったので、解除をやめました。");
                return;
            }

            // 戻した先の親のページを見せる（SPEC 5章）。親へのリンクが消えていて別の親に戻したときは、
            // いま右にいる親とは別のページなので、そこへ移る。開いていなければ、ルートからの道筋で開く。
            var last = _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
            if (last == null || !string.Equals(last.PageId ?? string.Empty, parentId ?? string.Empty, StringComparison.Ordinal)
                || !ReferenceEquals(last.Owner, document))
            {
                var shown = ShowPage(document, parentId);
                if (shown != null) _focused = shown;
            }

            Status("サブページを解除して、本文に戻しました。");
        }

        /// <summary>
        /// ファイルを開いたところからリンクを辿って行き着けないサブページを知らせ、削除するか尋ねる。
        /// リンクを消すとページだけが残り、開く手段が無くなるため。
        /// 削除は元に戻せないので、「はい」のあとにもう一度確かめる。
        /// </summary>
        private void Unreferenced_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (view == null || view.Owner == null)
            {
                Status("先にファイルを開いてください。");
                return;
            }

            var document = view.Owner;
            var pages = document.FindUnreferenced();
            if (pages.Count == 0)
            {
                Status("ファイルからリンクされていないページはありません。");
                return;
            }

            var list = new StringBuilder();
            foreach (var page in pages)
            {
                list.Append(page.Id).Append("　").Append(PageName(page.Text)).Append(Environment.NewLine);
            }

            var answer = MessageBox.Show(this,
                "ファイルからリンクされていないページが " + pages.Count + " 件あります。" + Environment.NewLine +
                Environment.NewLine + list +
                Environment.NewLine + "残す場合は、ファイルから辿れるページに [名前](#p1) の形でリンクを書いてください。" +
                Environment.NewLine + Environment.NewLine + "これらのページを削除しますか？",
                "孤立ページ", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            answer = MessageBox.Show(this,
                "孤立ページ " + pages.Count + " 件を削除します。" + Environment.NewLine +
                "元に戻す（Ctrl+Z）では戻せません。保存しないで閉じれば戻ります。" + Environment.NewLine +
                Environment.NewLine + "本当に削除しますか？",
                "孤立ページの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            DeletePages(document, pages);
        }

        /// <summary>
        /// 確かめた一覧のうち、今も孤立していて、確かめたときと同じページ（読み直しで入れ替わっていない）だけ（2026-10-27）。
        /// 確かめている間に、外での書き換えの読み直し（Activated）や AI がそのページにリンクを書くことがあるため、消す直前に数え直す。
        /// </summary>
        private static List<PageRegion> StillOrphaned(PageDocument document, List<PageRegion> pages)
        {
            var now = document.FindUnreferenced();

            return pages.Where(page => now.Any(found => ReferenceEquals(found, page))
                                       && ReferenceEquals(document.PageOf(page.Id), page)).ToList();
        }

        /// <summary>
        /// ツールバーの「未参照の添付」（2026-10-18）。添付フォルダ（.文書名/）のファイルのうち、文書のどのページからも参照されていないものを知らせる。
        /// 孤立ページと同じく、ごみ箱へ移すか尋ねる（既定は「いいえ」。一覧を見るだけなら「いいえ」）。
        /// ネットワーク上の場所（\\Mac\ など）はごみ箱が使えず完全に消えるので、知らせてもう一度確かめる。
        /// </summary>
        private void UnreferencedAttachments_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (view == null || view.Owner == null)
            {
                Status("先にファイルを開いてください。");
                return;
            }

            var document = view.Owner;
            var folder = Attachments.FolderOf(document.FilePath);

            List<string> files;
            try
            {
                files = Attachments.FindUnreferenced(document);
            }
            catch (Exception ex)
            {
                Status("添付フォルダを調べられませんでした: " + ex.Message);
                return;
            }

            if (files.Count == 0)
            {
                Status("本文から参照されていない添付はありません。");
                return;
            }

            // 一覧は添付フォルダからの相対で。長いときは途中で切る。
            const int Shown = 30;
            var list = new StringBuilder();
            foreach (var file in files.Take(Shown))
                list.Append(file.Substring(folder.Length).TrimStart('\\', '/')).Append(Environment.NewLine);
            if (files.Count > Shown) list.Append("ほか ").Append(files.Count - Shown).Append(" 件").Append(Environment.NewLine);

            var permanent = FilerView.IsNetworkPath(folder);

            var answer = MessageBox.Show(this,
                "本文から参照されていない添付が " + files.Count + " 件あります（" + Path.GetFileName(folder) + " フォルダ）。" + Environment.NewLine +
                Environment.NewLine + list +
                Environment.NewLine + "残す場合は、本文にリンクを書いてください。" +
                Environment.NewLine + Environment.NewLine +
                (permanent ? "これらのファイルを削除しますか？（ネットワーク上の場所のため、ごみ箱に入らず完全に消えます）" : "これらのファイルをごみ箱に移しますか？"),
                "未参照の添付", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            if (permanent)
            {
                answer = MessageBox.Show(this,
                    "添付 " + files.Count + " 件を削除します。" + Environment.NewLine +
                    "ネットワーク上の場所のため、ごみ箱に入らず、元に戻せません。" + Environment.NewLine +
                    Environment.NewLine + "本当に削除しますか？",
                    "未参照の添付の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            // 確かめている間に、外での書き換えの読み直し（Activated）や AI が、本文にその添付へのリンクを書いたかもしれない。
            // 消す直前に数え直し、今も参照されていないものだけを消す（2026-10-27）。
            // ファイルが外で書き換わっていて読み直していなければ（未保存の編集がある等）、ディスクの本文に参照があるかもしれないのでやめる。
            List<string> still;
            try
            {
                if (!_stack.Any(shown => ReferenceEquals(shown.Owner, document)))
                {
                    Status("確かめている間にファイルが閉じられたので、削除をやめました。");
                    return;
                }

                if (document.IsChangedOnDisk)
                {
                    MessageBox.Show(this,
                        "確かめている間に、ファイルが外部で書き換えられました。新しい本文から添付が参照されているかもしれないので、削除をやめました。" + Environment.NewLine +
                        "保存または読み直してから、もう一度やり直してください。",
                        "未参照の添付", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                still = Attachments.FindUnreferenced(document);
            }
            catch (Exception ex)
            {
                Status("添付フォルダを調べ直せなかったので、削除をやめました: " + ex.Message);
                return;
            }

            var skipped = files.Where(file => !still.Contains(file, StringComparer.OrdinalIgnoreCase)).ToList();
            if (skipped.Count > 0)
            {
                files = files.Where(file => still.Contains(file, StringComparer.OrdinalIgnoreCase)).ToList();

                MessageBox.Show(this,
                    "確かめている間に、次のファイルは本文から参照されるようになったため、削除しません。" + Environment.NewLine +
                    Environment.NewLine + string.Join(Environment.NewLine, skipped.Take(Shown).Select(file => Path.GetFileName(file)).ToArray()) +
                    (skipped.Count > Shown ? Environment.NewLine + "ほか " + (skipped.Count - Shown) + " 件" : string.Empty) +
                    (files.Count > 0 ? Environment.NewLine + Environment.NewLine + "残りの " + files.Count + " 件は削除します。" : string.Empty),
                    "未参照の添付", MessageBoxButton.OK, MessageBoxImage.Information);

                if (files.Count == 0) return;
            }

            var removed = 0;
            var failed = new List<string>();

            foreach (var file in files)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    removed++;
                }
                catch (OperationCanceledException)
                {
                    break;   // Windows の確認でやめた
                }
                catch (Exception ex)
                {
                    failed.Add(Path.GetFileName(file) + "（" + ex.Message + "）");
                }
            }

            if (failed.Count > 0)
            {
                MessageBox.Show(this,
                    removed + " 件を" + (permanent ? "削除" : "ごみ箱に移") + "しましたが、次のファイルはできませんでした。" + Environment.NewLine +
                    Environment.NewLine + string.Join(Environment.NewLine, failed),
                    "未参照の添付", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                Status("未参照の添付を " + removed + " 件" + (permanent ? "削除" : "ごみ箱に移") + "しました。");
            }
        }

        /// <summary>
        /// ページを削除する（保存はしない）。開いているページは先に閉じる。
        /// 閉じられなかったら（保存できず、捨てるのを断られたら）何も消さない。
        /// 確かめた一覧は古いかもしれないので、閉じる前と消す直前に数え直し、今も孤立している同じページだけを消す（2026-10-27。StillOrphaned）。
        /// </summary>
        private void DeletePages(PageDocument document, List<PageRegion> confirmed)
        {
            if (!_stack.Any(view => ReferenceEquals(view.Owner, document)))
            {
                Status("確かめている間にファイルが閉じられたので、削除をやめました。");
                return;
            }

            var pages = StillOrphaned(document, confirmed);
            if (pages.Count < confirmed.Count)
            {
                var changed = confirmed.Where(page => !pages.Contains(page))
                    .Select(page => "・" + page.Id + "　" + PageName(page.Text ?? string.Empty)).ToArray();

                MessageBox.Show(this,
                    "確かめている間に、次のページはリンクされた・読み直しで入れ替わったため、削除しません。" + Environment.NewLine +
                    Environment.NewLine + string.Join(Environment.NewLine, changed) +
                    (pages.Count > 0 ? Environment.NewLine + Environment.NewLine + "残りの " + pages.Count + " 件は削除します。" : string.Empty),
                    "孤立ページの削除", MessageBoxButton.OK, MessageBoxImage.Information);

                if (pages.Count == 0) return;
            }

            var ids = pages.Select(page => page.Id).ToList();

            var first = -1;
            for (var i = 0; i < _stack.Count; i++)
            {
                if (ids.Any(id => IsSamePage(_stack[i], document, id)))
                {
                    first = i;
                    break;
                }
            }

            if (first > 0)
            {
                PopTo(first - 1);
            }
            else if (first == 0)
            {
                // 一番左が消すページなら、ルートに差し替える。
                PopTo(0);
                if (_stack.Count == 1 && ids.Any(id => IsSamePage(_stack[0], document, id)))
                {
                    _stack[0].Bind(document, PageDocument.RootId);
                    RebuildStack(false);
                    AfterStackChanged();
                }
            }

            if (_stack.Any(view => ids.Any(id => IsSamePage(view, document, id))))
            {
                Status("ページを閉じられなかったので、削除をやめました。");
                return;
            }

            // 閉じるときの保存の確かめ（ダイアログ）の間にも読み直しが起こりうるので、消す直前にもう一度数え直す。
            var remaining = StillOrphaned(document, pages);
            if (remaining.Count < pages.Count)
            {
                MessageBox.Show(this,
                    "ページを閉じている間に、削除しようとしたページの一部がリンクされた・読み直しで入れ替わったため、そのページは削除しません。",
                    "孤立ページの削除", MessageBoxButton.OK, MessageBoxImage.Information);

                if (remaining.Count == 0) return;
                ids = remaining.Select(page => page.Id).ToList();
            }

            foreach (var id in ids) document.RemovePage(id);

            // 消したページに付けていた見出しのブックマークも外す。
            var marks = _headingBookmarks.RemoveAll(mark =>
                SamePath(mark.File, document.FilePath) && ids.Contains(mark.Page ?? string.Empty));
            if (marks > 0) HeadingBookmarksChanged();

            // 保存はしない（2026-10-25。自動保存をやめた）。保存すると確定し、保存しないで閉じれば元に戻る。
            UpdateChrome();
            Status("孤立ページを " + ids.Count + " 件削除しました。保存すると確定します。");
        }

        private MarkdownView FindView(PageDocument document, string pageId)
        {
            foreach (var view in _stack)
            {
                if (IsSamePage(view, document, pageId)) return view;
            }

            return null;
        }

        /// <summary>
        /// リンクに出す名前。先頭の行から作る。
        /// ファイル名ではなく単なる表示文字列なので、気に入らなければその場で書き換えられる。
        /// </summary>
        private static string PageName(string body)
        {
            foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                var line = Flatten(raw).TrimStart('#', '>', '-', '*', '+', ' ');
                line = Flatten(line);

                if (line.Length == 0) continue;

                if (line.Length <= 40) return line;

                // 絵文字などのサロゲートペアの間で切ると、片割れが保存で U+FFFD に化ける。
                var cut = char.IsHighSurrogate(line[39]) ? 39 : 40;
                return line.Substring(0, cut);
            }

            return "サブページ";
        }

        /// <summary>改行や連続した空白を1つの空白に畳む。リンク名は1行でなければならない。</summary>
        private static string Flatten(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var builder = new StringBuilder(text.Length);
            var space = false;

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = builder.Length > 0;
                    continue;
                }

                if (space) builder.Append(' ');
                space = false;

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>リンクの表示名に入ると記法を壊す文字を逃がす。</summary>
        private static string EscapeLabel(string label)
        {
            var builder = new StringBuilder(label.Length);

            foreach (var c in label)
            {
                if (c == '[' || c == ']' || c == '\\') builder.Append('\\');
                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 新しいファイルを作る。既にあれば中身に触れずに true を返し、呼び出し側はそれを開く。
        /// 確かめてから書くまでの間に誰かが作っても、上書きしない（CreateNew）。
        /// </summary>
        private bool TryCreatePage(string path, string title)
        {
            try
            {
                // 新規は LF（SPEC 10章）。Environment.NewLine だと CRLF が混ざる。
                var data = new UTF8Encoding(false).GetBytes("# " + title + "\n\n");

                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                }

                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "作成できませんでした。" + Environment.NewLine + ex.Message,
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>
        /// 記法を壊す文字を含むパスは CommonMark の書き方に合わせて &lt;...&gt; で囲む。
        /// </summary>
        private static string ToLinkTarget(string path)
        {
            var risky = path.IndexOf(' ') >= 0
                || path.IndexOf('(') >= 0
                || path.IndexOf(')') >= 0;

            return risky ? "<" + path + ">" : path;
        }

        // ---------------------------------------------------------------
        // 保存
        // ---------------------------------------------------------------

        /// <summary>
        /// 未保存の文書を全部保存する。全部保存できたら true。失敗を握り潰さず呼び出し側に返す。
        /// 保存するのは、利用者が決めたときだけ（ツールバーの「保存」・閉じる前に尋ねて「はい」・AI に渡す前に尋ねて「はい」）。
        /// 自動保存はやめた（2026-10-25。うっかり変えた文字まで黙って書き込まれるのが怖いため）。
        /// 保存はファイル単位なので、同じ文書を開いている複数のページはまとめて1回で済む。
        /// </summary>
        private bool SaveAll()
        {
            return SaveDocuments(_stack.Select(view => view.Owner).Where(owner => owner != null && owner.IsDirty).Distinct().ToList());
        }

        private bool SaveDocuments(List<PageDocument> documents)
        {
            var wrote = false;
            var ok = true;

            foreach (var document in documents)
            {
                if (SaveDocument(document)) wrote = true;
                else ok = false;
            }

            if (wrote) SavedText.Text = "最終保存 " + DateTime.Now.ToString("HH:mm:ss");

            // 見出しの書き換えに追いかけたブックマークも、本文と一緒に覚える。変わっていなければ書かない。
            StoreHeadingBookmarks();

            UpdateChrome();
            return ok;
        }

        /// <summary>
        /// 1つの文書を保存する。開いたあとに外で書き換わっていたら（AI など）、上書きするか尋ねる。
        /// 保存できなければ false（編集は画面に残る）。
        /// 保存できなかったとき・上書きを断ったときは、画面の内容を別名で書き出すか尋ねる（2026-10-27。OfferRecoveryCopy）。
        /// </summary>
        private bool SaveDocument(PageDocument document)
        {
            try
            {
                document.Save();
                RecordHistory(document);
                return true;
            }
            catch (ExternalChangeException)
            {
                // どれだけのものを上書きするのか分かるよう、ディスクと画面の文字数を出す（2026-10-27）。
                // 中身を空にしてから書き直すアプリの途中を読んだとき等、ディスクが極端に小さいことがある。
                var disk = document.DiskTextLength();
                var sizes = "ディスク " + (disk >= 0 ? disk.ToString("N0") + " 文字" : "（読めません）") +
                            " → 画面 " + document.TextLength.ToString("N0") + " 文字";

                var answer = MessageBox.Show(this,
                    "「" + Path.GetFileName(document.FilePath) + "」は、開いたあとに外部（AI など）で変更されています。" + Environment.NewLine +
                    Environment.NewLine + sizes + Environment.NewLine +
                    Environment.NewLine + "上書きすると、外部での変更は失われます。上書き保存しますか？" + Environment.NewLine +
                    "「いいえ」なら保存しません（編集は画面に残ります）。",
                    "外部で変更されています", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    OfferRecoveryCopy(document, "「" + Path.GetFileName(document.FilePath) + "」は保存しませんでした。");
                    return false;
                }

                try
                {
                    document.Save(true);
                    RecordHistory(document);
                    return true;
                }
                catch (Exception ex)
                {
                    Status("保存できませんでした: " + ex.Message);
                    OfferRecoveryCopy(document, "「" + Path.GetFileName(document.FilePath) + "」を保存できませんでした。" + Environment.NewLine + ex.Message);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Status("保存できませんでした: " + ex.Message);
                OfferRecoveryCopy(document, "「" + Path.GetFileName(document.FilePath) + "」を保存できませんでした。" + Environment.NewLine + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 保存できなかった文書の逃げ道（2026-10-27）。今の画面の内容を、元のファイルの隣に別名（名前.recovered-日時.md）で書き出すか尋ねる。
        /// 前は保存できないと、編集を画面に残したまま「保存しない」で捨てるしかなかった。元のファイルは書き換えない。
        /// 書き出したら場所を知らせる。書き出しても編集は画面に残り、未保存のまま。
        /// </summary>
        private void OfferRecoveryCopy(PageDocument document, string reason)
        {
            var answer = MessageBox.Show(this,
                reason + Environment.NewLine + Environment.NewLine +
                "今の画面の内容を、別名（" + Path.GetFileNameWithoutExtension(document.FilePath) + ".recovered-日時.md）で書き出しておきますか？" + Environment.NewLine +
                "元のファイルは書き換えません。編集は画面に残ります。",
                "別名で書き出す", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;

            string path;
            try
            {
                path = document.SaveRecoveryCopy();
            }
            catch (Exception)
            {
                path = null;
            }

            if (path == null)
            {
                MessageBox.Show(this, "別名でも書き出せませんでした。編集は画面に残っています。", "別名で書き出す",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this, "画面の内容を書き出しました。" + Environment.NewLine + Environment.NewLine + path,
                "別名で書き出す", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 保存したら、変更の履歴（.文書名/git）に記録する（2026-10-25）。AI に文書がどう変わってきたかを読ませるため。裏で動く。
        /// </summary>
        private static void RecordHistory(PageDocument document)
        {
            DocumentHistory.Record(document.FilePath, "保存 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        }

        /// <summary>
        /// ページを閉じる・差し替える前に呼ぶ。閉じたあとスタックに残らない文書に未保存の編集があれば、保存するか尋ねる
        /// （普通の Windows のソフトと同じ。2026-10-25）。同じファイルのページが残るなら、閉じても編集は残るので尋ねない。
        /// 進めてよければ true。「保存しない」を選んだときも true で、その編集は捨てる。「キャンセル」・保存できなかったときは false。
        /// </summary>
        /// <param name="staying">閉じたあとも残るページ。</param>
        /// <param name="opening">これから開く文書（あれば）。</param>
        private bool ConfirmLeaving(IEnumerable<MarkdownView> staying, PageDocument opening)
        {
            var kept = staying.Select(view => view.Owner).ToList();
            if (opening != null) kept.Add(opening);

            var all = _stack.Select(view => view.Owner).Where(owner => owner != null && !kept.Contains(owner)).Distinct().ToList();

            // WordPress の記事は、一時ファイルに保存するかではなく、アップロードするかを尋ねる（SPEC 20章。2026-10-30）。
            if (!ConfirmLeavingWordPress(all)) return false;

            var leaving = all.Where(owner => owner.IsDirty && WordPressLinkOf(owner) == null).ToList();
            if (leaving.Count == 0) return true;

            var names = string.Join(Environment.NewLine, leaving.Select(document => "・" + Path.GetFileName(document.FilePath)).ToArray());
            var answer = MessageBox.Show(this,
                "次のファイルに、保存していない変更があります。" + Environment.NewLine + Environment.NewLine + names +
                Environment.NewLine + Environment.NewLine + "保存しますか？",
                "保存していない変更", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return true;

            return SaveDocuments(leaving);
        }

        private void OnViewTextChanged(object sender, EventArgs e)
        {
            ScheduleOutlineRefresh();
        }

        private void UpdateChrome()
        {
            DirtyText.Visibility = _stack.Any(view => view.IsDirty) ? Visibility.Visible : Visibility.Collapsed;

            if (!HasDocument)
            {
                Title = "Tsuzuri";
                return;
            }

            var wordPress = WordPressLinkOf(Active);
            Title = Active.Title + (Active.IsDirty ? " *" : string.Empty)
                    + (wordPress != null ? " — WordPress " + wordPress.Site.DisplayName + "（" + StatusLabel(wordPress.Status)
                                           + (HasPendingUpload(Active.Owner) ? "・未アップロード" : string.Empty) + "）" : string.Empty)
                    + " — Tsuzuri";
        }

        // ---------------------------------------------------------------
        // 1行ずつ並べる一覧の行（見出しのブックマーク、SPEC 7章）
        //
        // ファイルのブックマークはランチャーに置き換えた（SPEC 4章）。
        // ---------------------------------------------------------------

        /// <summary>
        /// 一覧の1行。クリックで開き、ホバーで出る ✕ で外す。
        /// </summary>
        /// <param name="icon">行の頭に置く種類のアイコン（FileIcon）。無ければ null。</param>
        private Border CreateRemovableRow(TextBlock name, object tag, string tip, string removeTip,
            MouseButtonEventHandler open, RoutedEventHandler removed, FrameworkElement icon = null)
        {
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(icon != null ? 8 : 16, 0, 4, 0);
            name.Foreground = (Brush)FindResource("SidebarText");

            var remove = new Button();
            remove.Content = "✕";
            remove.ToolTip = removeTip;
            remove.FontSize = 11;
            remove.MinWidth = 22;
            remove.Padding = new Thickness(0);
            remove.Margin = new Thickness(0, 3, 6, 3);
            remove.Tag = tag;
            remove.Visibility = Visibility.Hidden;   // ホバーで出す
            remove.Click += removed;

            var style = Application.Current != null
                ? Application.Current.Resources["ToolButton"] as Style
                : null;
            if (style != null) remove.Style = style;

            DockPanel.SetDock(remove, Dock.Right);

            var row = new DockPanel();
            row.Children.Add(remove);

            if (icon != null)
            {
                icon.Margin = new Thickness(16, 0, 0, 0);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
            }

            row.Children.Add(name);

            var container = new Border();
            container.Height = 32;   // Mattermost のチャンネル一覧の1行と同じ高さ
            container.Child = row;
            container.Background = Brushes.Transparent;
            container.Cursor = Cursors.Hand;
            container.ToolTip = tip;
            container.Tag = tag;
            container.MouseLeftButtonUp += open;

            container.MouseEnter += delegate
            {
                remove.Visibility = Visibility.Visible;
                container.Background = (Brush)FindResource("HoverFill");
                name.Foreground = (Brush)FindResource("SidebarTextActive");
            };

            container.MouseLeave += delegate
            {
                name.Foreground = (Brush)FindResource("SidebarText");
                remove.Visibility = Visibility.Hidden;
                container.Background = Brushes.Transparent;
            };

            return container;
        }

        /// <summary>✕ を押したときは、行のクリックとして扱わない。</summary>
        private static bool IsButtonClick(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase) return true;
                if (source is Border) return false;

                var visual = source as Visual;
                if (visual == null) return false;

                source = VisualTreeHelper.GetParent(visual);
            }

            return false;
        }
    }
}

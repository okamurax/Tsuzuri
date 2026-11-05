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
    // MainWindow の一部: ランチャーとファイラー（本文の場所に重ねて出す）と、ページが消えたときの書き出し。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // ランチャーとファイラー（SPEC 4章・9章）
        //
        // 起動の起点。ランチャーはファイル・フォルダ・ショートカット・バッチを固定のマス目に登録する。
        // ファイラーと2つで1つの画面にし、「ランチャー」で一緒に出し入れする（2026-10-14。前はファイラーを右の列に常に出していた）。
        // 画面は本文の場所（StackHost）の上半分に重ねる（2026-10-31）。下のエディタはそのままで、下半分は見えている。閉じれば全部また見える。
        // 経緯: 本文の右側に滑らせて重ねていた → 独立した最前面のウィンドウ（2026-10-28）→ 今の形。
        // ファイルが開いていなくても使える。ファイラーは閉じても作ったまま持ち、見ていたフォルダを覚えている。
        // ---------------------------------------------------------------

        /// <summary>
        /// ツールバーの「ランチャー」。ランチャーとファイラーを一緒に出し入れする。
        /// 本文の場所（StackHost）の上半分に重ねて出す。下半分のエディタは見えたままで、ファイラーからドラッグできる。
        /// 閉じればエディタが全部また見える（2026-10-31。SPEC 4章）。
        /// エディタは閉じない・動かさない（ページの並び・カーソル・打ちかけはそのまま）。動きはつけない。
        /// </summary>
        private void ToggleLauncher()
        {
            if (_launcher != null)
            {
                CloseLauncher();
                return;
            }

            // ほかの画面のファイラーで別のフォルダへ移っていたら、そこから出す（最後に見たフォルダはアプリで1つ。2026-11-05）。
            var last = _settings.FilerFolder;
            if (_filer != null && !string.IsNullOrEmpty(last) && Directory.Exists(last)
                && !string.Equals(last, _filer.Folder, StringComparison.OrdinalIgnoreCase))
                _filer.Navigate(last);

            _launcher = new LauncherView(_settings, OpenFromLauncher, Status, folder =>
            {
                _filer.Navigate(folder);
                Status("ファイラーで開きました: " + folder);
            });
            LauncherSlot.Child = _launcher;
            LauncherHost.Visibility = Visibility.Visible;

            // 覆われたエディタにキー入力が届かないよう、フォーカスをランチャーの側へ移す（見えない本文に打ち込まないため）。Esc もここで受ける。
            // 出したばかりで、まだ見えていない（組み版の前）とフォーカスを受け取れないので、組み版のあとで移す。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_launcher != null && !LauncherHost.IsKeyboardFocusWithin) Keyboard.Focus(LauncherHost);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>ランチャーを出している間の Esc で閉じる。名前を書き換えている途中の Esc は、その欄が受け取る（Handled）ので、ここには来ない。</summary>
        private void LauncherHost_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _launcher == null) return;

            e.Handled = true;
            CloseLauncher();
        }

        /// <summary>
        /// ランチャーを出している間に、ランチャーの外（見えている下半分の本文・アウトライン・ツールバー等）を押したら閉じる。
        /// ランチャーとファイラーの中と、ツールバーの「ランチャー」は除く（そのボタン自身が開け閉めする。除かないと、押した瞬間に閉じて、続くクリックでまた開いてしまう）。
        /// ランチャーから出したダイアログは別のウィンドウなので、ここには来ない。
        /// </summary>
        private void CloseLauncherOnOutsideClick(object sender, MouseButtonEventArgs e)
        {
            if (_launcher == null) return;

            for (var node = e.OriginalSource as DependencyObject; node != null; node = ParentOf(node))
            {
                if (node == LauncherButton || node == LauncherHost) return;
            }

            CloseLauncher();
        }

        private static DependencyObject ParentOf(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);   // 文字の一部（Run など）は見た目の木に載らない
        }

        private void CloseLauncher()
        {
            if (_launcher == null) return;

            var hadFocus = LauncherHost.IsKeyboardFocusWithin;
            StoreFilerWidth();   // 隠すと幅が 0 になるので、その前に覚える
            _settings.Save();

            _launcher.Close();
            _launcher = null;
            LauncherSlot.Child = null;
            LauncherHost.Visibility = Visibility.Collapsed;

            // ランチャーにフォーカスがあったなら、下のエディタへ戻す（外を押して閉じたときは、押した先に任せる）。
            if (hadFocus && Active != null) Active.TextArea.Focus();
        }

        /// <summary>
        /// ランチャーのマスを押した。.md はこのエディタで開き、フォルダは隣のファイラーで開く。
        /// スクリプトは別の窓で動かす（RunScript。AI の欄で動かすのは 2026-10-30 にやめた）。それ以外（.exe・.lnk など）は Windows に任せて起動する。
        /// </summary>
        private void OpenFromLauncher(string path)
        {
            if (Directory.Exists(path))
            {
                _filer.Navigate(path);
                Status("ファイラーで開きました: " + path);
                return;
            }

            if (string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            {
                // 新しい画面（タブ）で開く（2026-11-03。前はこの画面の連鎖を捨てて開き直していた）。開いたものを見せるため、ランチャーは閉じる。
                // この画面が何も開いていなければ、この画面で開く（空のタブを残さない。「新規作成」と同じ）。
                // この画面・ほかの画面ですでに開いていれば、その画面のルートページを出す（リンクで別の文書を開くときと同じ。OpenLinkedFile）。
                CloseLauncher();
                if (!HasDocument) OpenRoot(path);
                else OpenLinkedFile(path, null);
                return;
            }

            if (FilerView.IsScript(path)) RunScript(path);
            else Launch(path);
        }

        /// <summary>
        /// ファイラーでファイルを開いた（.md かスクリプトだけが来る）。.md はこのエディタで開き、スクリプトは別の窓で動かす（RunScript）。
        /// スクリプトをファイラーが自分で開かずにここへ回すのは、Windows に任せると .ps1 はメモ帳が開くため。
        /// </summary>
        private void OpenInApp(string path)
        {
            if (FilerView.IsScript(path))
            {
                RunScript(path);
                return;
            }

            // ランチャーと同じく、新しい画面（タブ）で開く（2026-11-03。ランチャーとファイラーは1つの画面に並んでいて、片方だけ今のタブで開くと戸惑うため）。
            CloseLauncher();   // 開いたものを見せる
            if (!HasDocument) OpenRoot(path);
            else OpenLinkedFile(path, null);
        }

        /// <summary>
        /// 開いているページが、ファイルから消えていた（元に戻す・やり直すでページが消えた等）。
        /// エディタはそれ以上打てなくなるが、画面にある内容は保存先が無い。黙って捨てずに別名で書き出して知らせる。
        /// </summary>
        private void OnPageLost(MarkdownView view)
        {
            string path = null;
            try
            {
                path = PageDocument.SaveLostText(view.FilePath, view.Text);
            }
            catch (Exception)
            {
            }

            MessageBox.Show(this,
                "このページはファイルから消えています（元に戻す・やり直すなどで）。" + Environment.NewLine +
                "これ以上は打てません。画面の内容は保存されないので、" +
                (path != null
                    ? "別名で書き出しました。" + Environment.NewLine + Environment.NewLine + path
                    : "書き出せませんでした。必要なら選んでコピーしてください。"),
                "ページが見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

    }
}

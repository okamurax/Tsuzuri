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
        // 画面は本文の場所（StackHost）いっぱいに重ねる（2026-11-20。2026-10-31 からは上半分で、下半分の本文が見えていた）。下のエディタはそのままで、閉じれば全部また見える。
        // 経緯: 本文の右側に滑らせて重ねていた → 独立した最前面のウィンドウ（2026-10-28）→ 上半分（2026-10-31）→ 今の形。
        // 出し入れは本文の左下のつまみ（カレンダーのつまみの下）か Alt+L（2026-11-24。前は画面のタブの並びの一番右、その前はツールバーのボタン）。もう一度押すまで出したまま。
        // ファイルが開いていなくても使える。ファイラーは閉じても作ったまま持ち、見ていたフォルダを覚えている。
        // ---------------------------------------------------------------

        /// <summary>
        /// 本文の左下のつまみの「ランチャー」・Alt+L（2026-11-24。前は画面のタブの段の「ランチャー」）。ランチャーとファイラーを一緒に出し入れする。
        /// 本文の場所（StackHost）いっぱいに重ねて出す。閉じればエディタが全部また見える（SPEC 4章）。
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

            // カレンダーは閉じない（2026-11-21。利用者の考え）。ランチャーを上に重ね、ランチャーを閉じればカレンダーに戻る。

            _launcher = new LauncherView(_settings, OpenFromLauncher, Status, folder =>
            {
                _filer.Navigate(folder);
                Status("ファイラーで開きました: " + folder);
            });
            LauncherSlot.Child = _launcher;
            LauncherHost.Visibility = Visibility.Visible;
            PaintSideTabs();
            PaintFilerUseFolder();

            // 覆われたエディタにキー入力が届かないよう、フォーカスをランチャーの側へ移す（見えない本文に打ち込まないため）。
            // 出したばかりで、まだ見えていない（組み版の前）とフォーカスを受け取れないので、組み版のあとで移す。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_launcher != null && !LauncherHost.IsKeyboardFocusWithin) Keyboard.Focus(LauncherHost);
            }), DispatcherPriority.Loaded);
        }

        // 外を押しても Esc でも閉じない（2026-11-24。利用者の考え。カレンダーと同じ。閉じるのは本文の左下のつまみ・Alt+L、
        // ランチャーから文書を開いたとき、アウトラインの見出しのダブルクリック）。前は外を押す・Esc で閉じていた

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
            PaintSideTabs();

            // ランチャーにフォーカスがあったなら、下（図エディタ・カレンダーを出していればそれ、無ければエディタ）へ戻す。
            if (hadFocus && _drawing != null) _drawing.View.FocusCanvas();
            else if (hadFocus && _calendar != null) _calendar.Focus();
            else if (hadFocus && Active != null) Active.TextArea.Focus();
        }

        /// <summary>
        /// ファイラーの上の「このフォルダを会話のフォルダにする」（2026-11-24。AI の会話のフォルダはここから渡す。前は AI の欄の「…」で Windows のフォルダの選択の窓）。
        /// ファイラーで今見ているフォルダを、AI の欄で選んでいる会話のフォルダにする。
        /// </summary>
        private void FilerUseFolder_Click(object sender, RoutedEventArgs e)
        {
            var tab = _aiTab;
            var folder = _filer != null ? _filer.Folder : null;
            if (tab == null)
            {
                Status("AI の会話がありません（AI の欄を出して会話を始めてから）");
                return;
            }
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Status("フォルダが見つかりません");
                return;
            }
            tab.Project = folder;
            RefreshAiPinRows();
            StoreAiTabs();
            Status("AI の会話" + (string.IsNullOrEmpty(tab.Name) ? string.Empty : " " + tab.Name) + " のフォルダにしました: " + folder);
        }

        /// <summary>ランチャーを出すとき: どの会話に渡すかをツールチップに出す。会話が無ければ押せない。</summary>
        private void PaintFilerUseFolder()
        {
            var tab = _aiTab;
            FilerUseFolder.IsEnabled = tab != null;
            FilerUseFolder.ToolTip = tab == null
                ? "AI の会話が無いので渡せない（AI の欄を出して会話を始めてから）"
                : "ファイラーで今見ているフォルダを、AI の欄で選んでいる会話" + (string.IsNullOrEmpty(tab.Name) ? string.Empty : "（" + tab.Name + "）")
                  + "のフォルダにする。話しかけるたびに Claude に伝える";
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

            if (string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase) || Attachments.IsPlainText(path))   // .txt もこのエディタで（2026-11-21）
            {
                // 新しい画面（タブ）で開く（2026-11-03。前はこの画面の連鎖を捨てて開き直していた）。開いたものを見せるため、ランチャーは閉じる。
                // この画面が何も開いていなければ、この画面で開く（空のタブを残さない。「新規作成」と同じ）。
                // この画面・ほかの画面ですでに開いていれば、その画面のルートページを出す（リンクで別の文書を開くときと同じ。OpenLinkedFile）。
                CloseLauncher();
                CloseCalendar();   // 開いた文書を見せる（カレンダーの下に開くと見えない。見出しへ飛ぶときと同じ）
                if (!HasDocument) OpenRoot(path);
                else OpenLinkedFile(path, null);
                return;
            }

            if (FilerView.IsScript(path)) RunScript(path);
            else Launch(path);
        }

        /// <summary>
        /// ファイラーでファイルを開いた（.md・.txt かスクリプトだけが来る）。.md はこのエディタで開き、スクリプトは別の窓で動かす（RunScript）。
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
            CloseCalendar();
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

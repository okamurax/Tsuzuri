using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tsuzuri.Markdown;

namespace Tsuzuri
{
    // MainWindow の一部: 画面を増やす（2026-10-31。試作）。
    //
    // 1つのアプリの中で、この画面（MainWindow）を丸ごともう1つ作る。中の作りは変えず、画面ごとに今までと同じに動く。
    // 別の exe を起動しないのは、app.json を2つのアプリが別々に書いて、ランチャーの登録などを消し合わないため（設定はメモリの上で1つを共有する）。
    // ・1つの文書は1つの画面だけで開く（リンクで右に開いた .md・WordPress の記事も）。.文書名/state.json と変更の記録を、2つの画面から書かないため
    // ・終了したときに開いていた画面は、次の起動で全部戻す（ページの並び・AI の会話のタブ。WordPress の記事は読み込み直す）。
    //   最初の画面の分は app.json の Screen・Collapsed・AiTabs、増やした画面の分は Windows に覚える。タブの × で閉じたら、その分は消す
    // ・タブの × は、その画面だけを閉じる。ツールバーの「終了」は、アプリごと（全部の画面を）閉じる（2026-10-31）
    //   最初の画面を × で閉じたら、次の画面が最初の画面を引き継ぐ（前回のファイル・app.json の上のほうの項目）
    // ・画面は全部同じ位置・大きさに重ね、ツールバーの一段下のタブで前に出す
    public partial class MainWindow
    {
        /// <summary>開いている画面。作った順。先頭が最初の画面。</summary>
        private static readonly List<MainWindow> OpenWindows = new List<MainWindow>();

        private static int _windowsCreated;

        /// <summary>何番目に作った画面か（1 始まり）。1 が最初の画面。</summary>
        private int _windowNumber;

        /// <summary>増やした画面の、次の起動で戻すもの（app.json の Windows の1つ）。最初の画面は null（app.json の上のほうの項目を使う）。</summary>
        private WindowSession _session;

        /// <summary>「終了」で全部の画面を閉じている最中か。このときは、閉じた画面の覚え（Windows）を消さない（次の起動で戻す）。</summary>
        private static bool _closingAll;

        /// <summary>ツールバーの一覧を最後に組んだときの中身。変わったときだけ組み直す。</summary>
        private string _windowListSignature;

        /// <summary>
        /// 最初の画面か（覚えを app.json の上のほうの項目に置く画面）。前回のファイル（LastFile）を覚えるのと、起動のときにほかの画面を作るのは、この画面だけ。
        /// 最初の画面を × で閉じると、次の画面が引き継ぐ（PromoteToPrimary）。
        /// </summary>
        private bool IsPrimary
        {
            get { return _session == null; }
        }

        /// <summary>画面を見分ける名前。AI の欄のパイプ・設定のファイル、WordPress の一時フォルダに使う（同じアプリの中の画面どうしで重ねない）。</summary>
        private string WindowKey
        {
            get { return Process.GetCurrentProcess().Id + "-" + _windowNumber; }
        }

        /// <summary>
        /// コンストラクタから。一覧に載せ、閉じたら外す。増やした画面は、次の起動で戻すものを app.json の Windows に置く
        /// （session は、起動のときに戻す画面ならその覚え。新しく増やした画面なら null で、空の覚えを作る）。
        /// </summary>
        private void RegisterWindow(WindowSession session)
        {
            _windowNumber = ++_windowsCreated;
            OpenWindows.Add(this);

            if (OpenWindows.Count > 1)
            {
                _session = session ?? new WindowSession();
                if (!_settings.Windows.Contains(_session))
                {
                    // 「＋」で増やした空の画面も、すぐ覚える（2026-10-31）。落ちても、次の起動で空の画面として戻る。
                    _settings.Windows.Add(_session);
                    _settings.Save();
                }
            }

            Closed += delegate
            {
                var wasPrimary = IsPrimary;
                OpenWindows.Remove(this);

                // タブの × でこの画面だけを閉じたなら、次の起動では戻さない。「終了」で全部を閉じている最中なら残す。
                if (!_closingAll)
                {
                    if (_session != null) _settings.Windows.Remove(_session);
                    else if (wasPrimary && OpenWindows.Count > 0) OpenWindows[0].PromoteToPrimary();
                    _settings.Save();
                }

                RefreshWindowLists();
            };
        }

        /// <summary>
        /// 最初の画面が × で閉じられたので、この画面が引き継ぐ。この画面の覚えを app.json の上のほうの項目へ移す
        /// （閉じた画面の覚えは捨てる）。アプリの主の窓もこの画面にする。
        /// </summary>
        private void PromoteToPrimary()
        {
            _settings.Screen = _session.Screen;
            _settings.Collapsed = _session.Collapsed;
            _settings.AiTabs = _session.AiTabs;
            _settings.Windows.Remove(_session);
            _session = null;

            _settings.LastFile = _stack.Count > 0 ? _stack[0].FilePath : null;
            if (Application.Current != null) Application.Current.MainWindow = this;
        }

        /// <summary>ツールバーの「終了」。アプリごと（全部の画面を）閉じる。最初の画面を閉じれば、ほかの画面を先に閉じる（OnClosing）。</summary>
        private void CloseAllWindows()
        {
            _closingAll = true;
            var primary = OpenWindows.FirstOrDefault(window => window.IsPrimary) ?? this;
            primary.Close();
        }

        /// <summary>次の起動で戻す、終了したときの画面（最初の画面は app.json の Screen、増やした画面は Windows の1つ）。</summary>
        private ScreenState SavedScreen
        {
            get { return _session != null ? _session.Screen : _settings.Screen; }
            set
            {
                if (_session != null) _session.Screen = value;
                else _settings.Screen = value;
            }
        }

        private List<string> SavedCollapsed
        {
            get { return _session != null ? _session.Collapsed : _settings.Collapsed; }
            set
            {
                if (_session != null) _session.Collapsed = value;
                else _settings.Collapsed = value;
            }
        }

        /// <summary>次の起動で戻す、AI の会話のタブ。</summary>
        private List<AiTabState> SavedAiTabs
        {
            get { return _session != null ? _session.AiTabs : _settings.AiTabs; }
            set
            {
                if (_session != null) _session.AiTabs = value;
                else _settings.AiTabs = value;
            }
        }

        /// <summary>最初の画面が起動したとき、前回終了したときに開いていたほかの画面を作り直す（それぞれ自分の覚えで戻す）。最後に最初の画面を前に出す。</summary>
        private void RestoreOtherWindows()
        {
            var sessions = _settings.Windows.ToList();
            if (sessions.Count == 0) return;

            foreach (var session in sessions)
            {
                var window = new MainWindow(_settings, this, session);
                window.Show();
            }

            Dispatcher.BeginInvoke(new Action(delegate { Activate(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>増やした画面を、元の画面と同じ位置・大きさで出す（前回の位置は使わない）。</summary>
        private void PlaceLike(Window other)
        {
            var bounds = other.WindowState == WindowState.Normal ? new Rect(other.Left, other.Top, other.Width, other.Height) : other.RestoreBounds;

            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!bounds.IsEmpty)
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;
            }

            WindowState = other.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        /// <summary>ツールバーの「＋」。空の画面を1つ増やして前に出す。開くのは、その画面のランチャー・WordPress から。</summary>
        private void NewWindow_Click(object sender, RoutedEventArgs e)
        {
            var window = new MainWindow(_settings, this, null);
            window.Show();
            window.Activate();
        }

        /// <summary>この画面を、今の画面と同じ位置・大きさにして前に出す。</summary>
        private void BringForward(MainWindow from)
        {
            if (from != null && !ReferenceEquals(from, this)) PlaceLike(from);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            Show();
            Activate();
        }

        /// <summary>
        /// その .md を開いている、ほかの画面。無ければ null。
        /// 1つの文書を2つの画面で開くと、.文書名/state.json（ブックマーク・カーソル）を互いに上書きし、別々に直して食い違うため、開かずにその画面を前に出す。
        /// </summary>
        private MainWindow WindowHolding(string path)
        {
            return OpenWindows.FirstOrDefault(window => !ReferenceEquals(window, this)
                                                       && window._stack.Any(view => SamePath(view.FilePath, path)));
        }

        /// <summary>その WordPress の記事を開いている、ほかの画面。無ければ null。記事は一時ファイルの場所が画面ごとに違うので、サイトと番号で見る。</summary>
        private MainWindow WindowHoldingPost(string siteUrl, int id)
        {
            return OpenWindows.FirstOrDefault(window => !ReferenceEquals(window, this) && window._stack.Any(view =>
            {
                var link = window.WordPressLinkOf(view);
                return link != null && link.Id == id
                       && string.Equals(link.Site.Url, siteUrl, StringComparison.OrdinalIgnoreCase);
            }));
        }

        /// <summary>全部の画面の一覧（ツールバーの一段下）を、今の中身に合わせる（題・未保存の印が変わったとき、画面が増えた・減ったとき）。</summary>
        private static void RefreshWindowLists()
        {
            foreach (var window in OpenWindows.ToList()) window.BuildWindowList();
        }

        /// <summary>
        /// ツールバーの一段下の、画面のタブ。画面ごとのタブ（この画面は色を付ける）と、右に「＋」（新しい画面）。1つだけのときも出す（ブラウザのタブと同じ）。
        /// タブを押すとその画面を前に出し、タブの × でその画面だけを閉じる。画面が1つのときは × を出さない（アプリを閉じるのは「終了」）。
        /// 見た目は AI の欄の会話のタブにそろえる。
        /// </summary>
        private void BuildWindowList()
        {
            var labels = OpenWindows.Select(window => window.WindowLabel()).ToList();
            var signature = string.Join("\n", labels) + "\n" + OpenWindows.IndexOf(this);
            if (signature == _windowListSignature) return;

            _windowListSignature = signature;
            WindowList.Children.Clear();

            for (var i = 0; i < OpenWindows.Count; i++)
                WindowList.Children.Add(CreateWindowTab(OpenWindows[i], labels[i], OpenWindows.Count > 1));

            var add = new Button
            {
                Content = "＋",
                Style = (Style)FindResource("ToolButton"),
                ToolTip = "新しい画面を開く。同じ機能をもう1組使える（別の文書・別の WordPress の記事を並べて直すとき）。ここに並んだ名前を押すと切り替わる",
            };
            add.Click += NewWindow_Click;
            WindowList.Children.Add(add);
        }

        private Border CreateWindowTab(MainWindow window, string label, bool closable)
        {
            var self = ReferenceEquals(window, this);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(self ? "AccentText" : "TextMuted"),
            });

            if (closable)
            {
                var close = new Button
                {
                    Content = "\uE711",
                    ToolTip = "この画面を閉じる（ほかの画面はそのまま。未保存なら保存するか尋ねる）",
                    FontFamily = IconFont,
                    FontSize = 9,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("ToolButton"),
                };
                close.Click += delegate { window.Close(); };
                panel.Children.Add(close);
            }

            var tab = new Border
            {
                Child = panel,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 0, closable ? 3 : 10, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Background = self ? (Brush)FindResource("AccentFill") : Brushes.Transparent,
                ToolTip = window.WindowTip(),
            };

            // 押したときに切り替える。段の空いているところはウィンドウを動かすので、ここで受け止めて段に回さない（× のボタンは自分で受け取る）。
            tab.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                window.BringForward(this);
            };
            tab.MouseEnter += delegate { if (!self) tab.Background = (Brush)FindResource("HoverFill"); };
            tab.MouseLeave += delegate { if (!self) tab.Background = Brushes.Transparent; };
            return tab;
        }

        /// <summary>一覧に出す名前。開いているファイル（いちばん左のページ）の題。WordPress の記事なら頭に「WP:」、未保存なら後ろに「*」。</summary>
        private string WindowLabel()
        {
            if (_stack.Count == 0) return "（空）";

            var root = _stack[0];
            var label = root.Title;
            if (string.IsNullOrWhiteSpace(label)) label = System.IO.Path.GetFileNameWithoutExtension(root.FilePath);
            if (WordPressLinkOf(root) != null) label = "WP: " + label;
            if (_stack.Any(view => view.IsDirty)) label += " *";
            return label;
        }

        private string WindowTip()
        {
            if (_stack.Count == 0) return "何も開いていない画面";

            var link = WordPressLinkOf(_stack[0]);
            return link != null ? "WordPress " + link.Site.DisplayName + " の記事" : _stack[0].FilePath;
        }

        /// <summary>
        /// 「終了」で最初の画面を閉じるとき、先にほかの画面を閉じる（それぞれ未保存の確かめをする）。どれかが取りやめたら false（最初の画面も閉じない）。
        /// </summary>
        private bool CloseOtherWindows()
        {

            foreach (var window in OpenWindows.Where(other => !ReferenceEquals(other, this)).ToList())
            {
                window.Close();
                if (OpenWindows.Contains(window))
                {
                    CancelClosingAll();
                    window.BringForward(null);
                    return false;
                }
            }

            return true;
        }

        /// <summary>全部を閉じるのを途中でやめた。もう閉じた画面の覚えは消す（開いていない画面を次の起動で戻さない）。</summary>
        private void CancelClosingAll()
        {
            _closingAll = false;
            _settings.Windows.RemoveAll(session => !OpenWindows.Any(window => ReferenceEquals(window._session, session)));
            _settings.Save();
        }
    }
}

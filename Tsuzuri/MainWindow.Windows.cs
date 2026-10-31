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
    //   最初の画面の分は app.json の Screen・Collapsed・AiTabs、増やした画面の分は Windows に覚える。画面を「終了」で閉じたら、その分は消す
    // ・最初の画面を閉じたら、全部の画面を閉じる
    // ・画面は全部同じ位置・大きさに重ね、ツールバーの一覧で前に出す（見た目はタブの切り替え）
    public partial class MainWindow
    {
        /// <summary>開いている画面。作った順。先頭が最初の画面。</summary>
        private static readonly List<MainWindow> OpenWindows = new List<MainWindow>();

        private static int _windowsCreated;

        /// <summary>何番目に作った画面か（1 始まり）。1 が最初の画面。</summary>
        private int _windowNumber;

        /// <summary>増やした画面の、次の起動で戻すもの（app.json の Windows の1つ）。最初の画面は null（app.json の上のほうの項目を使う）。</summary>
        private WindowSession _session;

        /// <summary>最初の画面を閉じて、全部の画面を閉じている最中か。このときは、閉じた画面の覚え（Windows）を消さない（次の起動で戻す）。</summary>
        private static bool _closingAll;

        /// <summary>ツールバーの一覧を最後に組んだときの中身。変わったときだけ組み直す。</summary>
        private string _windowListSignature;

        /// <summary>最初の画面か。前回のファイル（LastFile）を覚えるのと、起動のときにほかの画面を作るのは、この画面だけ。</summary>
        private bool IsPrimary
        {
            get { return _windowNumber == 1; }
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

            if (!IsPrimary)
            {
                _session = session ?? new WindowSession();
                if (!_settings.Windows.Contains(_session)) _settings.Windows.Add(_session);
            }

            Closed += delegate
            {
                OpenWindows.Remove(this);

                // 「終了」でこの画面だけを閉じたなら、次の起動では戻さない。全部を閉じている最中なら残す。
                if (_session != null && !_closingAll)
                {
                    _settings.Windows.Remove(_session);
                    _settings.Save();
                }

                RefreshWindowLists();
            };
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
        /// ツールバーの一段下の、画面の一覧。画面ごとのボタン（この画面は色を付ける）と、右に「＋」（新しい画面）。1つだけのときも出す（ブラウザのタブと同じ）。
        /// </summary>
        private void BuildWindowList()
        {
            var labels = OpenWindows.Select(window => window.WindowLabel()).ToList();
            var signature = string.Join("\n", labels) + "\n" + OpenWindows.IndexOf(this);
            if (signature == _windowListSignature) return;

            _windowListSignature = signature;
            WindowList.Children.Clear();

            for (var i = 0; i < OpenWindows.Count; i++)
            {
                var window = OpenWindows[i];
                var self = ReferenceEquals(window, this);

                // 中身は TextBlock にする（文字のままだと、ファイル名の _ がアクセスキーとして消える）。
                var button = new Button
                {
                    Content = new TextBlock { Text = labels[i], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 },
                    Style = (Style)FindResource(self ? "AccentButton" : "ToolButton"),
                    ToolTip = window.WindowTip(),
                };
                button.Click += delegate { window.BringForward(this); };
                WindowList.Children.Add(button);
            }

            var add = new Button
            {
                Content = "＋",
                Style = (Style)FindResource("ToolButton"),
                ToolTip = "新しい画面を開く。同じ機能をもう1組使える（別の文書・別の WordPress の記事を並べて直すとき）。ここに並んだ名前を押すと切り替わる",
            };
            add.Click += NewWindow_Click;
            WindowList.Children.Add(add);
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
        /// 最初の画面を閉じるとき、先にほかの画面を閉じる（それぞれ未保存の確かめをする）。どれかが取りやめたら false（最初の画面も閉じない）。
        /// </summary>
        private bool CloseOtherWindows()
        {
            _closingAll = true;

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

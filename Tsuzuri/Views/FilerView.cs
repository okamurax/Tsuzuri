using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 添付を本文へドラッグするための、ファイラー（SPEC 9章）。
    ///
    /// ランチャーと1つの画面に並べ、その右側に置く（MainWindow の LauncherHost の FilerHost）。その画面はツールバーの「ランチャー」で
    /// 本文の上半分に重ねて出し入れする（2026-10-31。前は右のサイドパネルに常に置いていた）。下半分に見えている本文へファイルをドラッグする。
    /// フォルダの木でも、作業フォルダの登録でもない（SPEC 1章。フォルダの登録はランチャー）。
    /// できるのは探すこと、ドラッグ、開くこと（Markdown はこのエディタで、ほかは既定のアプリで）と、
    /// 右クリックでの作成・名前の変更・削除（2026-10-12）。コピーや移動など、それ以上はエクスプローラーで行う。
    /// </summary>
    public sealed class FilerView : DockPanel
    {
        /// <summary>検索で並べる件数の上限。</summary>
        private const int MaxResults = 300;

        /// <summary>検索で潜るフォルダの深さと数の上限。共有ドライブの深い木で固まらないように。</summary>
        private const int MaxDepth = 8;
        private const int MaxFolders = 5000;

        private sealed class Found
        {
            public string Path;
            public bool IsFolder;
        }

        private sealed class SearchResult
        {
            public readonly List<Found> Items = new List<Found>();
            public bool Truncated;
        }

        private readonly Action<string> _status;
        private readonly Action<string> _openDocument;
        private readonly Func<string, bool> _createDocument;
        private readonly Func<string, bool> _isInUse;
        private readonly Func<string, string, bool, string> _renamed;

        // 名前を打っている欄（新しく作る・名前を変える）。打っていなければ null。
        private FrameworkElement _naming;


        private readonly TextBox _searchBox = new TextBox();
        private readonly TextBox _address = new TextBox();
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly DispatcherTimer _searchDelay = new DispatcherTimer();

        private string _folder;
        private CancellationTokenSource _searching;

        // ドラッグの始まりを見分けるため。押しただけでは始めない。
        private Point _dragStart;
        private string _dragPath;

        /// <param name="status">ステータスバーへの知らせ。</param>
        /// <param name="openDocument">Markdown ファイルをこのエディタで開く。</param>
        /// <param name="createDocument">そのパスに Markdown ファイル（# 名前 の1行）を作って開く。作れたら true。</param>
        /// <param name="isInUse">
        /// そのファイル（フォルダならその中のどれか）をエディタで開いているか。開いていれば名前の変更・削除をさせない
        /// （外で変えると、エディタが保存先を失うため）。
        /// </param>
        /// <param name="renamed">
        /// 名前を変えたあとに呼ぶ（変える前のパス・変えたあとのパス・フォルダか）。エディタが覚えているパス（ランチャー・前回のファイル・画面の覚え）を直す。
        /// 知らせに足す文を返す（無ければ null）。
        /// </param>
        public FilerView(Action<string> status, Action<string> openDocument, Func<string, bool> createDocument,
            Func<string, bool> isInUse, Func<string, string, bool, string> renamed)
        {
            _status = status;
            _openDocument = openDocument;
            _createDocument = createDocument;
            _isInUse = isInUse;
            _renamed = renamed;

            LastChildFill = true;
            Background = Theme.Background;
            System.Windows.Documents.TextElement.SetForeground(this, Theme.Text);

            // フォルダの登録はランチャーで行う（SPEC 4章）。ここには持たない。
            // ファイルの作成・名前の変更・削除は、右クリックのメニューで行う（2026-10-12。SPEC 9章）。

            // --- アドレス。直接打って Enter で移る。↑ で親へ ---
            // 矢印は Windows のアイコン用フォント（上 E74A）。文字の ↑ は小さく描くと潰れて見えた（2026-10-16）。
            var up = CreateIconButton("\uE74A", "親のフォルダへ");
            up.Click += delegate { GoUp(); };
            DockPanel.SetDock(up, Dock.Left);

            // コピーや移動など、右クリックで足りないことはエクスプローラーで行う（SPEC 9章）。
            // エクスプローラーで開いて作業し、戻ったら「更新」で読み直す。アドレスの欄を狭めないよう、文字でなくアイコンにする。
            // 作成・名前の変更・削除はファイラーの右クリックでできるので、ツールチップではコピー・移動を挙げる。
            // 字は「新しいウィンドウで開く」（四角から矢印が出る形）。エクスプローラーの絵（EC50）は細かく、小さいとつぶれたため。
            var explorer = CreateIconButton("\uE8A7", "このフォルダをエクスプローラーで開く（コピーや移動などはそちらで）");
            explorer.Click += delegate { OpenInExplorer(); };
            DockPanel.SetDock(explorer, Dock.Right);

            var reload = CreateIconButton("\uE72C", "最新の状態に読み直す");
            reload.Click += delegate { Reload(); };
            DockPanel.SetDock(reload, Dock.Right);

            _address.BorderThickness = new Thickness(0);
            _address.Background = Brushes.Transparent;
            _address.Foreground = Theme.Text;
            _address.CaretBrush = Theme.Text;
            _address.SelectionBrush = Theme.Selection;
            _address.FontSize = 12;
            _address.VerticalContentAlignment = VerticalAlignment.Center;
            _address.Margin = new Thickness(6, 0, 0, 0);
            _address.ToolTip = "フォルダのパスを打って Enter で移る（ファイルのパスならそのフォルダへ）";
            _address.PreviewKeyDown += OnAddressKeyDown;

            // 移らずにほかへ行ったら、今のフォルダの表示に戻す。打ちかけのまま残すと、今どこにいるか分からなくなる。
            _address.LostKeyboardFocus += delegate { _address.Text = _folder ?? string.Empty; };

            var addressRow = new DockPanel();
            addressRow.Margin = new Thickness(8, 0, 8, 0);
            addressRow.VerticalAlignment = VerticalAlignment.Center;
            addressRow.Children.Add(up);
            addressRow.Children.Add(explorer);   // DockPanel は先に足したものが右端に付く
            addressRow.Children.Add(reload);
            addressRow.Children.Add(_address);

            // エディタの上端の段（検索欄）と同じ高さ。隣り合う段の下の線を一直線にそろえる（SPEC 16章）。
            var addressArea = new Border();
            addressArea.Height = 32;
            addressArea.BorderBrush = Theme.Divider;
            addressArea.BorderThickness = new Thickness(0, 0, 0, 1);
            addressArea.Child = addressRow;
            DockPanel.SetDock(addressArea, Dock.Top);
            Children.Add(addressArea);

            // --- 検索欄。今のフォルダとその下を、名前で探す ---
            // エディタの検索欄（MarkdownView）と同じ作り（2026-10-14）。高さ 32px の段の左に薄い「検索」、その右に枠の無い入力欄、
            // 下に区切り線。前は角の丸い枠に虫眼鏡と薄い案内を出していたが、エディタと見た目がそろわなかった。
            var searchLabel = new TextBlock();
            searchLabel.Text = "検索";
            searchLabel.FontSize = 12;
            searchLabel.Foreground = Theme.Muted;
            searchLabel.VerticalAlignment = VerticalAlignment.Center;
            searchLabel.Margin = new Thickness(4, 0, 10, 0);
            DockPanel.SetDock(searchLabel, Dock.Left);

            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.Background = Brushes.Transparent;
            _searchBox.Foreground = Theme.Text;
            _searchBox.CaretBrush = Theme.Text;
            _searchBox.SelectionBrush = Theme.Selection;
            _searchBox.FontSize = 13;
            _searchBox.Padding = new Thickness(0);
            _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            _searchBox.ToolTip = "今のフォルダとその下を、ファイル名で探す（空白で区切ると全部を含むもの）";
            _searchBox.TextChanged += delegate
            {
                _searchDelay.Stop();
                _searchDelay.Start();
            };

            var searchRow = new DockPanel();
            searchRow.Margin = new Thickness(8, 0, 8, 0);
            searchRow.Children.Add(searchLabel);
            searchRow.Children.Add(_searchBox);

            var searchArea = new Border();
            searchArea.Height = 32;
            searchArea.BorderBrush = Theme.Divider;
            searchArea.BorderThickness = new Thickness(0, 0, 0, 1);
            searchArea.Child = searchRow;
            searchArea.Background = Brushes.Transparent;
            searchArea.Cursor = Cursors.IBeam;
            searchArea.MouseLeftButtonDown += delegate { _searchBox.Focus(); };   // 「検索」の文字や余白を押しても打てるように

            DockPanel.SetDock(searchArea, Dock.Top);
            Children.Add(searchArea);

            _searchDelay.Interval = TimeSpan.FromMilliseconds(200);
            _searchDelay.Tick += delegate
            {
                _searchDelay.Stop();
                Refresh();
            };

            // --- 一覧 ---
            _scroll.Content = _list;

            // 何も無いところの右クリックで、作るメニューを出す（行の右クリックは行のメニュー）。
            _scroll.Background = Brushes.Transparent;
            _scroll.MouseRightButtonUp += (sender, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                ShowMenu(null, false, null, null);
            };
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Children.Add(_scroll);
        }

        /// <summary>見ているフォルダが変わった（2026-11-05。アプリで1つの「最後に見たフォルダ」として、すぐ app.json に覚える）。</summary>
        public event Action<string> FolderChanged;

        /// <summary>今表示しているフォルダ。</summary>
        public string Folder
        {
            get { return _folder; }
        }

        /// <summary>そのフォルダへ移る。検索欄は空にする。</summary>
        public void Navigate(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;

            if (!Directory.Exists(folder))
            {
                Notify("フォルダが見つかりません: " + folder);
                return;
            }

            _folder = folder;
            _address.Text = folder;

            _searchBox.Text = string.Empty;
            _searchDelay.Stop();   // 空にしたことによる検索のやり直しは要らない

            Refresh();

            var changed = FolderChanged;
            if (changed != null) changed(folder);
        }

        /// <summary>動いている検索を止める。アプリを終えるときに呼ぶ。</summary>
        public void Stop()
        {
            _searchDelay.Stop();
            CancelSearch();
        }

        // =====================================================================
        // アドレス
        // =====================================================================

        /// <summary>
        /// Enter で、打ったパスへ移る。入力欄そのものの振る舞いなので、
        /// アプリ全体のショートカットとは別（SPEC 11章。検索欄の Enter と同じ扱い）。
        /// </summary>
        private void OnAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            GoTo(_address.Text);
        }

        /// <summary>
        /// 打ったパスへ移る。前後の空白と引用符は取り、%USERPROFILE% などは展開する
        /// （エクスプローラーの「パスのコピー」は "…" で囲まれるため）。
        /// ファイルのパスなら、そのファイルがあるフォルダへ移る。見つからなければ打った文字は残す。
        /// </summary>
        private void GoTo(string typed)
        {
            var path = (typed ?? string.Empty).Trim().Trim('"').Trim();
            if (path.Length == 0) return;

            try
            {
                path = Environment.ExpandEnvironmentVariables(path);

                if (Directory.Exists(path))
                {
                    Navigate(Path.GetFullPath(path));
                    return;
                }

                if (File.Exists(path))
                {
                    Navigate(Path.GetDirectoryName(Path.GetFullPath(path)));
                    return;
                }
            }
            catch (Exception)
            {
                // パスとして読めない文字。下で見つからないと知らせる。
            }

            Notify("フォルダが見つかりません: " + path);
        }

        // =====================================================================
        // 一覧
        // =====================================================================

        private void GoUp()
        {
            if (string.IsNullOrEmpty(_folder)) return;

            try
            {
                var parent = Directory.GetParent(_folder);
                if (parent != null) Navigate(parent.FullName);
            }
            catch (Exception ex)
            {
                Notify("親のフォルダへ移れません: " + ex.Message);
            }
        }

        /// <summary>
        /// 「更新」。今のフォルダ（検索中なら検索）を読み直す。フォルダの一覧ならスクロール位置を残す
        /// （エクスプローラーでファイルを作って戻ったとき、見ていた場所が先頭に飛ばないように）。
        /// </summary>
        private void Reload()
        {
            if (string.IsNullOrEmpty(_folder)) return;

            var offset = _scroll.VerticalOffset;
            var listing = _searchBox.Text.Trim().Length == 0;

            Refresh();

            if (listing)
            {
                _scroll.UpdateLayout();
                _scroll.ScrollToVerticalOffset(offset);
            }
        }

        /// <summary>
        /// 今のフォルダを Windows のエクスプローラーで開く。関連付けを通さずに explorer.exe を名指しで起動する
        /// （通すと Directory Opus の「エクスプローラーの置き換え」に横取りされるため。ランチャーと同じ）。
        /// </summary>
        private void OpenInExplorer()
        {
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder))
            {
                Notify("フォルダが見つかりません: " + _folder);
                return;
            }

            try
            {
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var start = new ProcessStartInfo(exe, "\"" + _folder + "\"");
                start.UseShellExecute = false;
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Notify("エクスプローラーで開けませんでした: " + ex.Message);
            }
        }

        /// <summary>検索欄が空ならフォルダの中身を、何か入っていれば検索の結果を出す。</summary>
        private void Refresh()
        {
            if (string.IsNullOrEmpty(_folder)) return;

            var words = _searchBox.Text.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0) ShowFolder();
            else StartSearch(words);
        }

        /// <summary>フォルダを先に、それぞれ名前順で並べる。隠しファイルは出さない。</summary>
        private void ShowFolder()
        {
            CancelSearch();
            CancelRename();   // 名前を打っている途中なら、打ちかけで決めずにやめる
            _list.Children.Clear();

            List<DirectoryInfo> folders;
            List<FileInfo> files;

            try
            {
                var info = new DirectoryInfo(_folder);
                folders = info.EnumerateDirectories().Where(IsListed)
                    .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                files = info.EnumerateFiles().Where(IsListed)
                    .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                AddNote("開けませんでした: " + ex.Message);
                return;
            }

            foreach (var folder in folders) _list.Children.Add(CreateRow(folder.FullName, true, folder.Name, null));
            foreach (var file in files) _list.Children.Add(CreateRow(file.FullName, false, file.Name, null));

            if (folders.Count == 0 && files.Count == 0) AddNote("（空のフォルダ）");

            _scroll.ScrollToTop();
        }

        /// <summary>
        /// 下のフォルダまで潜って探す。時間がかかることがあるので別のスレッドで行い、
        /// 打ち直したら前の検索は止める。
        /// </summary>
        private void StartSearch(string[] words)
        {
            CancelSearch();

            CancelRename();   // 名前を打っている途中なら、打ちかけで決めずにやめる
            _list.Children.Clear();
            AddNote("検索中…");

            var root = _folder;
            var cancel = new CancellationTokenSource();
            _searching = cancel;

            var token = cancel.Token;

            Task.Run(() => Find(root, words, token), token).ContinueWith(task =>
            {
                if (token.IsCancellationRequested || task.Status != TaskStatus.RanToCompletion) return;
                ShowResults(task.Result, root);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void CancelSearch()
        {
            if (_searching == null) return;

            _searching.Cancel();
            _searching = null;
        }

        private static SearchResult Find(string root, string[] words, CancellationToken token)
        {
            var result = new SearchResult();

            var pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(root, 0));

            var visited = 0;

            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) return result;

                if (++visited > MaxFolders)
                {
                    result.Truncated = true;
                    break;
                }

                var entry = pending.Dequeue();

                try
                {
                    var info = new DirectoryInfo(entry.Key);

                    foreach (var folder in info.EnumerateDirectories())
                    {
                        if (!IsListed(folder)) continue;

                        if (Matches(folder.Name, words)) Add(result, folder.FullName, true);

                        // リンクで作られたフォルダは潜らない。輪になっていると終わらない。
                        var linked = (folder.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (!linked && !NoSearchInside.Contains(folder.Name) && entry.Value + 1 < MaxDepth)
                            pending.Enqueue(new KeyValuePair<string, int>(folder.FullName, entry.Value + 1));
                    }

                    foreach (var file in info.EnumerateFiles())
                    {
                        if (IsListed(file) && Matches(file.Name, words)) Add(result, file.FullName, false);
                    }
                }
                catch (Exception)
                {
                    // 読めないフォルダ（権限・切れた共有）は飛ばす。
                }

                if (result.Items.Count >= MaxResults)
                {
                    result.Truncated = true;
                    break;
                }
            }

            return result;
        }

        private static void Add(SearchResult result, string path, bool isFolder)
        {
            if (result.Items.Count >= MaxResults) return;

            var found = new Found();
            found.Path = path;
            found.IsFolder = isFolder;
            result.Items.Add(found);
        }

        /// <summary>空白で区切った語を全部含むか。大文字小文字は区別しない。</summary>
        private static bool Matches(string name, string[] words)
        {
            foreach (var word in words)
            {
                if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            return true;
        }

        private void ShowResults(SearchResult result, string root)
        {
            CancelRename();   // 名前を打っている途中なら、打ちかけで決めずにやめる
            _list.Children.Clear();

            foreach (var found in result.Items)
            {
                // どのフォルダにあるかを薄く添える。
                var parent = Path.GetDirectoryName(found.Path) ?? string.Empty;
                var where = parent.Length > root.Length ? parent.Substring(root.Length).TrimStart('\\', '/') : null;

                _list.Children.Add(CreateRow(found.Path, found.IsFolder, Path.GetFileName(found.Path), where));
            }

            if (result.Items.Count == 0) AddNote("見つかりません");
            if (result.Truncated) AddNote("多いため途中までです。検索語を足して絞り込んでください。");

            _scroll.ScrollToTop();
        }

        /// <summary>
        /// 行の頭のアイコン。エクスプローラーと同じ Windows のアイコン（16px）。取れなければ自作の線のアイコン。
        /// 拡張子ごとに一度だけ Windows に尋ねる（ShellIcons）ので、ファイルの多いフォルダでも重くならない。
        /// </summary>
        private static FrameworkElement RowIcon(string path, bool isFolder)
        {
            var source = ShellIcons.ForListing(path, isFolder);
            if (source == null) return FileIcon.Create(path, isFolder);

            var image = new Image();
            image.Source = source;
            image.Width = 16;
            image.Height = 16;
            image.VerticalAlignment = VerticalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        /// <summary>
        /// 一覧の1行。フォルダはクリックで中へ。ファイルはドラッグで本文へ、クリック（1回）で開く
        /// （Markdown はこのエディタで、スクリプトは別の窓で、ほかは既定のアプリで。Open）。2回目のクリックは無視する。
        /// </summary>
        private Border CreateRow(string path, bool isFolder, string name, string where)
        {
            var glyph = RowIcon(path, isFolder);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(glyph, Dock.Left);

            var text = new TextBlock();
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Inlines.Add(new System.Windows.Documents.Run(name));

            if (!string.IsNullOrEmpty(where))
            {
                var detail = new System.Windows.Documents.Run("   " + where);
                detail.Foreground = Theme.Muted;
                detail.FontSize = 12;
                text.Inlines.Add(detail);
            }

            var content = new DockPanel();
            content.Children.Add(glyph);
            content.Children.Add(text);

            var row = new Border();
            row.Child = content;
            row.Padding = new Thickness(12, 3, 8, 3);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.ToolTip = isFolder ? path + Environment.NewLine + "クリックで移る／ランチャーへドラッグで登録"
                : path + Environment.NewLine + (IsMarkdown(path)
                    ? "クリックでこのエディタで開く／本文へドラッグでリンクを入れる"
                    : "クリックで開く／本文へドラッグでリンクを入れる");

            row.MouseEnter += delegate { if (row != _menuRow) row.Background = ResourceBrush("HoverFill"); };
            row.MouseLeave += delegate { if (row != _menuRow) row.Background = Brushes.Transparent; };

            // クリックで開く（フォルダなら移る）。ドラッグと見分けるため、押した時点ではなく、動かさずに離したときに開く。
            // フォルダもドラッグできる。ランチャーへ落として登録するため（SPEC 4章）。本文へ落としても何も入らない。
            row.MouseLeftButtonDown += (sender, e) =>
            {
                // 2回目のクリックは無視する。癖でダブルクリックしても、2回開かないように。
                if (e.ClickCount >= 2)
                {
                    _dragPath = null;
                    return;
                }

                _dragStart = e.GetPosition(this);
                _dragPath = path;
            };

            row.MouseMove += (sender, e) =>
            {
                if (_dragPath != path || e.LeftButton != MouseButtonState.Pressed) return;

                var now = e.GetPosition(this);
                if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                _dragPath = null;

                // エクスプローラーからのドラッグと同じ形で渡す。本文側はいつものドロップとして受ける。
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(row, data, DragDropEffects.Copy);
            };

            row.MouseLeftButtonUp += delegate
            {
                var clicked = _dragPath == path;   // ドラッグを始めていれば null になっている
                _dragPath = null;

                if (!clicked) return;

                if (isFolder) Navigate(path);
                else Open(path);
            };

            row.MouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                ShowMenu(path, isFolder, text, row);
            };

            return row;
        }

        /// <summary>
        /// Markdown はこのエディタで、スクリプト（.bat・.cmd・.ps1）は別の窓で動かし（既定のアプリに任せると .ps1 はメモ帳が開く）、
        /// ほかは既定のアプリで開く。Markdown とスクリプトは _openDocument（MainWindow.OpenInApp）に渡す。
        /// </summary>
        private void Open(string path)
        {
            if ((IsMarkdown(path) || IsScript(path)) && _openDocument != null)
            {
                _openDocument(path);
                return;
            }

            try
            {
                Process.Start(path);
            }
            catch (Exception ex)
            {
                Notify("開けませんでした: " + ex.Message);
            }
        }

        // =====================================================================
        // 作成・名前の変更・削除（右クリック。2026-10-12）
        //
        // エディタで開いているファイル（と、それが入っているフォルダ）は、名前を変えさせず、消させない。
        // Markdown の名前を変えたら、添付フォルダ（.ファイル名）も一緒に変える（2026-11-03。前は利用者が手で揃えていた。Rename）。
        // =====================================================================

        /// <summary>右クリックのメニュー。path が null なら何も無いところ（作るメニューだけ）。</summary>
        /// <param name="row">右クリックした行。メニューを出している間、選んでいる印を付けておく。</param>
        private void ShowMenu(string path, bool isFolder, TextBlock label, Border row)
        {
            if (string.IsNullOrEmpty(_folder)) return;

            // 並びと名前はランチャーの右クリックとそろえる（SPEC 4章・9章）。
            // 開く（メモ帳・場所）→ 名前の変更 → 削除 → 作成
            var menu = new ContextMenu();

            if (path != null)
            {
                if (!isFolder && ShellActions.CanOpenInNotepad(path))
                {
                    var notepad = new MenuItem { Header = "メモ帳で開く" };
                    notepad.Click += delegate { ShellActions.OpenInNotepad(path, false, Notify); };
                    menu.Items.Add(notepad);

                    var elevated = new MenuItem { Header = "メモ帳で開く（管理者）" };
                    elevated.Click += delegate { ShellActions.OpenInNotepad(path, true, Notify); };
                    menu.Items.Add(elevated);
                }

                var reveal = new MenuItem { Header = "エクスプローラーで場所を開く" };
                reveal.Click += delegate { ShellActions.RevealInExplorer(path, Notify); };
                menu.Items.Add(reveal);

                menu.Items.Add(new Separator());

                var rename = new MenuItem { Header = "名前を変更…" };
                rename.Click += delegate { BeginRename(path, isFolder, label); };
                menu.Items.Add(rename);

                menu.Items.Add(new Separator());

                // ファイルを本当に消す（ランチャーの「ランチャーから外す」とは違う）。必ず確かめる。
                // 項目を押すとメニューが先に閉じて印も消えるので、確かめるダイアログの間はもう一度付けておく。
                var delete = new MenuItem { Header = "削除…" };
                delete.Click += delegate
                {
                    MarkMenuRow(row);
                    try
                    {
                        Delete(path, isFolder);
                    }
                    finally
                    {
                        MarkMenuRow(null);
                    }
                };
                menu.Items.Add(delete);

                menu.Items.Add(new Separator());
            }

            // 作る（ランチャーの「〜を追加…」は登録で、こちらは新しく作る）。
            var newFile = new MenuItem { Header = "新しいファイルを作成…" };
            newFile.Click += delegate { BeginCreate(false); };
            menu.Items.Add(newFile);

            var newFolder = new MenuItem { Header = "新しいフォルダを作成…" };
            newFolder.Click += delegate { BeginCreate(true); };
            menu.Items.Add(newFolder);

            // メニューを出している間は、どの行のメニューか分かるようにする。
            // 行には選んでいる印（アウトラインで選んでいる見出しと同じく、左端の青い帯と明るい地）を付け、
            // メニューの先頭にはその名前を押せない見出しとして出す（SPEC 9章）。
            if (path != null)
            {
                menu.Items.Insert(0, MenuTitle(Path.GetFileName(path.TrimEnd('\\', '/'))));
                menu.Items.Insert(1, new Separator());
            }

            MarkMenuRow(row);
            menu.Closed += delegate { MarkMenuRow(null); };

            menu.IsOpen = true;
        }

        // 名前を変えている途中なら、やめる処理。一覧を作り直すとき（更新・↑・フォルダの移動）に呼ぶ。
        // 作り直すと欄が外れてフォーカスを失い、「外を押したら決める」で打ちかけの名前に変わってしまうため。
        private Action _cancelRename;

        private void CancelRename()
        {
            var cancel = _cancelRename;
            _cancelRename = null;
            if (cancel != null) cancel();
        }

        // 右クリックのメニューを出している行。選んでいる印を付けている。
        private Border _menuRow;

        private void MarkMenuRow(Border row)
        {
            if (_menuRow != null)
            {
                _menuRow.Background = _menuRow.IsMouseOver ? ResourceBrush("HoverFill") : Brushes.Transparent;
                _menuRow.BorderThickness = new Thickness(0);
                _menuRow.Padding = new Thickness(12, 3, 8, 3);
            }

            _menuRow = row;
            if (row == null) return;

            // 左端に 3px の青い帯。帯の分だけ左の余白を減らし、文字を動かさない。
            row.Background = ResourceBrush("SelectionFill");
            row.BorderBrush = ResourceBrush("AccentBrush");
            row.BorderThickness = new Thickness(3, 0, 0, 0);
            row.Padding = new Thickness(9, 3, 8, 3);
        }

        /// <summary>メニューの先頭に出す、押せない見出し（対象の名前）。ランチャーと同じ形。</summary>
        internal static MenuItem MenuTitle(string name)
        {
            var title = new MenuItem();
            title.Header = (name ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            title.IsEnabled = false;
            title.FontWeight = FontWeights.SemiBold;
            return title;
        }

        /// <summary>
        /// 一覧の先頭に名前の欄を出して、今のフォルダに作る。Enter で作り、Esc か欄の外を押すとやめる。
        /// ファイルで拡張子を省くと .md を付ける。.md は「# 名前」の1行を入れて作り、エディタで開く。
        /// 検索中なら検索をやめて、作る先のフォルダの一覧に戻す。どこに作られるかが見えるように。
        /// </summary>
        private void BeginCreate(bool folder)
        {
            if (string.IsNullOrEmpty(_folder)) return;

            if (_searchBox.Text.Length > 0) Navigate(_folder);
            EndNaming();

            var initial = folder ? "新しいフォルダー" : "無題.md";
            var glyph = RowIcon(initial, folder);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(glyph, Dock.Left);

            var box = CreateNameBox(initial, folder ? "フォルダの名前を打って Enter で作る。Esc でやめる"
                : "ファイル名を打って Enter で作る（拡張子を省くと .md）。Esc でやめる");

            var content = new DockPanel();
            content.Children.Add(glyph);
            content.Children.Add(box);

            // 角の丸い青い枠で「打っている途中」と分かるようにする（検索欄と同じ）。
            var row = new Border();
            row.Child = content;
            row.Margin = new Thickness(8, 4, 8, 4);
            row.Padding = new Thickness(4, 3, 4, 3);
            row.CornerRadius = new CornerRadius(4);
            row.BorderBrush = ResourceBrush("AccentBrush");
            row.BorderThickness = new Thickness(2);

            box.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (Create(box.Text, folder)) EndNaming();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    EndNaming();
                }
            };
            box.LostKeyboardFocus += delegate { if (_naming == row) EndNaming(); };

            _naming = row;
            _list.Children.Insert(0, row);
            _scroll.ScrollToTop();

            box.Focus();
            box.Select(0, folder ? box.Text.Length : Path.GetFileNameWithoutExtension(box.Text).Length);   // 拡張子の前だけ選ぶ
        }

        /// <summary>作る。名前が使えなければ知らせて false（欄は残す）。作った・やめたなら true。</summary>
        private bool Create(string typed, bool folder)
        {
            var name = (typed ?? string.Empty).Trim();
            if (name.Length == 0) return true;   // 空ならやめる

            if (!ValidName(name)) return false;
            if (!folder && Path.GetExtension(name).Length == 0) name += ".md";

            var path = Path.Combine(_folder, name);
            if (File.Exists(path) || Directory.Exists(path))
            {
                Notify("同じ名前のものがあります: " + name);
                return false;
            }

            // 作るとエディタへフォーカスが移り、欄の LostKeyboardFocus でも閉じようとする。先に外しておく。
            EndNaming();

            try
            {
                if (folder)
                {
                    Directory.CreateDirectory(path);
                }
                else if (IsMarkdown(path))
                {
                    if (!_createDocument(path)) return true;   // 理由は向こうで出している
                }
                else
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Notify("作成できませんでした: " + ex.Message);
                return true;
            }

            Reload();
            Notify("作成しました: " + name);
            return true;
        }

        /// <summary>
        /// その行の名前をその場で書き換える。Enter か外を押すと決め、Esc でやめる。
        /// エディタで開いているもの（フォルダならその中に開いているものがあるとき）は変えさせない。
        /// </summary>
        private void BeginRename(string path, bool isFolder, TextBlock label)
        {
            if (_isInUse != null && _isInUse(path))
            {
                Notify(isFolder ? "中のファイルをエディタで開いているため、名前を変えられません。閉じてからにしてください。"
                    : "エディタで開いているため、名前を変えられません。閉じてからにしてください。");
                return;
            }

            if (label == null) return;
            var content = label.Parent as DockPanel;
            if (content == null) return;

            EndNaming();

            var oldName = Path.GetFileName(path.TrimEnd('\\', '/'));
            var box = CreateNameBox(oldName, "新しい名前を打って Enter か外を押すと決める。Esc でやめる");

            var done = false;
            Action<bool> finish = null;
            finish = commit =>
            {
                if (done) return;
                done = true;
                _cancelRename = null;
                if (_naming == box) _naming = null;

                if (commit && Rename(path, isFolder, box.Text)) return;   // 変えたら一覧を読み直している

                content.Children.Remove(box);
                content.Children.Add(label);
            };

            box.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; finish(false); }
            };
            box.LostKeyboardFocus += delegate { finish(true); };

            content.Children.Remove(label);
            content.Children.Add(box);
            _naming = box;
            _cancelRename = () => finish(false);

            box.Focus();
            box.Select(0, isFolder ? oldName.Length : Path.GetFileNameWithoutExtension(oldName).Length);
        }

        /// <summary>名前を変える。変えたら true（一覧を読み直す）。変えなかった・変えられなかったら false。</summary>
        private bool Rename(string path, bool isFolder, string typed)
        {
            var name = (typed ?? string.Empty).Trim();
            var oldName = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (name.Length == 0 || name == oldName) return false;
            if (!ValidName(name)) return false;

            // 選んでから決めるまでの間に開いたかもしれないので、もう一度確かめる。
            if (_isInUse != null && _isInUse(path))
            {
                Notify("エディタで開いているため、名前を変えられません。");
                return false;
            }

            var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            var target = Path.Combine(parent, name);
            var caseOnly = string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);

            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                Notify("同じ名前のものがあります: " + name);
                return false;
            }

            // Markdown の名前を変えたら、添付フォルダ（.ファイル名）も一緒に変える（2026-11-03。前は手で揃えてもらっていた）。
            // 新しい名前の添付フォルダがすでにあれば、どちらも変えない（別の文書の添付と混ざるため）。
            // 拡張子が .md でなくなる名前なら、添付フォルダは変えない（もう文書ではない）。
            var attachments = !isFolder && IsMarkdown(path) && IsMarkdown(target) ? Attachments.FolderOf(path) : null;
            if (attachments != null && !Directory.Exists(attachments)) attachments = null;
            var newAttachments = attachments != null ? Attachments.FolderOf(target) : null;
            var attachmentsCaseOnly = attachments != null && string.Equals(attachments, newAttachments, StringComparison.OrdinalIgnoreCase);
            if (attachments != null && !attachmentsCaseOnly && (Directory.Exists(newAttachments) || File.Exists(newAttachments)))
            {
                Notify("新しい名前の添付フォルダ（" + Path.GetFileName(newAttachments) + "）がすでにあるため、名前を変えませんでした。");
                return false;
            }

            try
            {
                if (caseOnly) MoveCaseOnly(path, target, parent, isFolder);
                else Move(path, target, isFolder);
            }
            catch (Exception ex)
            {
                Notify("名前を変えられませんでした: " + ex.Message);
                Reload();   // 途中まで動いていたら、一覧を今の姿に合わせる
                return false;
            }

            if (attachments != null)
            {
                try
                {
                    if (attachmentsCaseOnly) MoveCaseOnly(attachments, newAttachments, parent, true);
                    else Move(attachments, newAttachments, true);
                }
                catch (Exception ex)
                {
                    // 添付フォルダを変えられなければ、文書の名前も戻す（文書と添付が離れないように）。
                    string back = null;
                    try
                    {
                        if (caseOnly) MoveCaseOnly(target, path, parent, false);
                        else Move(target, path, false);
                    }
                    catch (Exception)
                    {
                        back = "（文書の名前も戻せませんでした。いまは「" + name + "」です。添付フォルダの名前を手で揃えてください）";
                    }

                    Notify("添付フォルダの名前を変えられなかったため、" + (back == null ? "文書の名前も元に戻しました" : "名前を変えませんでした" + back) + ": " + ex.Message);
                    Reload();
                    return false;
                }
            }

            Reload();

            var more = _renamed != null ? _renamed(path, target, isFolder) : null;
            Notify("名前を変えました: " + name
                   + (attachments != null ? "（添付フォルダも " + Path.GetFileName(newAttachments) + " に変えました）" : string.Empty)
                   + (string.IsNullOrEmpty(more) ? string.Empty : "。" + more));
            return true;
        }

        /// <summary>
        /// 大文字小文字だけの変更。Windows では同じ名前とみなされて動かせないので、一度別の名前（.で始まる一時名）を経る。
        /// 2回目で失敗したら元の名前に戻す。戻せなければ、どこにあるか分かるよう一時名を知らせる
        /// （.で始まる名前は Mac の Finder で隠れるので、黙っていると消えたように見える）。
        /// </summary>
        private void MoveCaseOnly(string path, string target, string parent, bool isFolder)
        {
            var step = Path.Combine(parent, "." + Guid.NewGuid().ToString("N"));
            Move(path, step, isFolder);

            try
            {
                Move(step, target, isFolder);
            }
            catch (Exception)
            {
                try
                {
                    Move(step, path, isFolder);
                }
                catch (Exception)
                {
                    throw new IOException("途中で失敗し、元の名前にも戻せませんでした。いまは「" + Path.GetFileName(step) +
                        "」という名前になっています。エクスプローラーで名前を直してください。");
                }

                throw;
            }
        }

        private static void Move(string from, string to, bool isFolder)
        {
            if (isFolder) Directory.Move(from, to);
            else File.Move(from, to);
        }

        /// <summary>
        /// 消す。必ず確かめる（既定は「いいえ」）。ごみ箱に送る。
        /// ネットワーク上の場所（\\Mac\ など）はごみ箱に入らず完全に消えるので、確かめるときにそう伝える。
        /// エディタで開いているもの（フォルダならその中に開いているものがあるとき）は消させない。
        /// </summary>
        private void Delete(string path, bool isFolder)
        {
            if (_isInUse != null && _isInUse(path))
            {
                Notify(isFolder ? "中のファイルをエディタで開いているため、削除できません。閉じてからにしてください。"
                    : "エディタで開いているため、削除できません。閉じてからにしてください。");
                return;
            }

            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var permanent = IsNetworkPath(path);

            var message = "「" + name + "」を削除しますか？" + (isFolder ? "（フォルダの中身ごと）" : string.Empty) +
                Environment.NewLine + Environment.NewLine +
                (permanent
                    ? "ネットワーク上の場所のため、ごみ箱に入らず完全に消えます。元に戻せません。"
                    : "ごみ箱に送ります。");

            var answer = MessageBox.Show(Window.GetWindow(this), message, "削除",
                MessageBoxButton.YesNo, permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
                var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;

                if (isFolder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, recycle);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, recycle);
            }
            catch (OperationCanceledException)
            {
                return;   // Windows の確認でやめた
            }
            catch (Exception ex)
            {
                Notify("削除できませんでした: " + ex.Message);
                return;
            }

            Reload();
            Notify("削除しました: " + name);
        }

        /// <summary>ネットワーク上の場所か（\\サーバー\… か、ネットワークドライブ）。ごみ箱が使えない。</summary>
        internal static bool IsNetworkPath(string path)
        {
            try
            {
                // 本当のパスで見る。C: の上のリンクを通してネットワーク上の場所を指していることもあるため。
                var real = ShellActions.CanonicalPath(path);
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || real.StartsWith(@"\\", StringComparison.Ordinal)) return true;

                var root = Path.GetPathRoot(real);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                return true;   // 分からなければ、元に戻せない側として扱う（強く確かめる）
            }
        }

        /// <summary>ファイル名に使えない文字を含んでいれば知らせて false。</summary>
        private bool ValidName(string name)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Notify("名前に使えない文字があります（\\ / : * ? \" < > |）。");
                return false;
            }

            if (name == "." || name == "..")
            {
                Notify("その名前は使えません。");
                return false;
            }

            return true;
        }

        private static TextBox CreateNameBox(string text, string tip)
        {
            var box = new TextBox();
            box.Text = text;
            box.FontSize = 13;
            box.Foreground = Theme.Text;
            box.CaretBrush = Theme.Text;
            box.SelectionBrush = Theme.Selection;
            box.Background = Brushes.Transparent;
            box.BorderThickness = new Thickness(0);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.ToolTip = tip;
            return box;
        }

        /// <summary>新しく作る欄を出していれば閉じる（名前を変える欄は、それぞれの finish が戻す）。</summary>
        private void EndNaming()
        {
            var naming = _naming as Border;
            if (naming == null) return;

            _naming = null;
            _list.Children.Remove(naming);
        }

        /// <summary>エディタが動かすスクリプトか（.bat・.cmd・.ps1。SPEC 4章）。ランチャーも同じ決め方を使う。</summary>
        internal static bool IsScript(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMarkdown(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
        }

        private void AddNote(string message)
        {
            var note = new TextBlock();
            note.Text = message;
            note.Foreground = Theme.Muted;
            note.FontSize = 12;
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(16, 4, 8, 4);
            _list.Children.Add(note);
        }

        // =====================================================================
        // 小物
        // =====================================================================

        private void Notify(string message)
        {
            if (_status != null) _status(message);
        }

        /// <summary>
        /// 一覧に出すか。隠し・システムの属性が付いたものは出さない。
        /// ただし「.」で始まる名前は、隠しの属性が付いていても出す（添付フォルダ .ファイル名 を見せるため。SPEC 9章）。
        /// \\Mac\ の共有では、Mac の決まりで「.」で始まる名前に隠しの属性が付いて見える。
        /// Mac が勝手に作る管理用のもの（.DS_Store、._ で始まるもの等）は出さない。
        /// </summary>
        private static bool IsListed(FileSystemInfo entry)
        {
            var name = entry.Name;

            if (name.StartsWith("._", StringComparison.Ordinal) || MacJunk.Contains(name)) return false;
            if ((entry.Attributes & FileAttributes.System) != 0) return false;
            if (name.StartsWith(".", StringComparison.Ordinal)) return true;

            return (entry.Attributes & FileAttributes.Hidden) == 0;
        }

        private static readonly HashSet<string> MacJunk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".DS_Store", ".localized", ".Spotlight-V100", ".Trashes", ".fseventsd", ".TemporaryItems", ".DocumentRevisions-V100",
            ".dropbox", ".dropbox.cache", ".dropbox.attr",   // Dropbox の管理用。消すと同期がおかしくなる
        };

        /// <summary>検索で中へ潜らないフォルダ。一覧には出す。中身が多い管理用のもので、潜ると検索が遅くなるため。</summary>
        private static readonly HashSet<string> NoSearchInside = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", ".hg", ".vs", ".idea",
        };

        /// <summary>アイコンだけの小さなボタン。字は Windows 標準のアイコン用フォント（検索欄の虫眼鏡と同じ）。</summary>
        private static Button CreateIconButton(string glyph, string tip)
        {
            var button = CreateButton(glyph, tip);
            button.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            button.FontSize = 12;
            button.Padding = new Thickness(6, 2, 6, 2);
            button.Margin = new Thickness(2, 0, 0, 0);
            return button;
        }

        /// <param name="styleKey">App.xaml のスタイル。ボタンは全部 ToolButton にそろえている。</param>
        private static Button CreateButton(string content, string tip, string styleKey = "ToolButton")
        {
            var button = new Button();
            button.Content = content;
            button.ToolTip = tip;
            button.FontSize = 12;
            button.Padding = new Thickness(6, 1, 6, 1);
            button.Focusable = false;

            var style = Application.Current != null
                ? Application.Current.Resources[styleKey] as Style
                : null;
            if (style != null) button.Style = style;

            return button;
        }

        private static Brush ResourceBrush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Transparent;
        }
    }
}

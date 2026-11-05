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
    // MainWindow の一部: スタック（ページの並び）・ファイルごとの状態・ファイラー・仕切り。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // スタック（SPEC 6章）
        // ---------------------------------------------------------------

        private MarkdownView CreateView()
        {
            var view = new MarkdownView();

            // ドロップはエディタ自身に受けさせる。
            //
            // 親側で横取りしようとしたが、エディタの上ではウィンドウまで
            // イベントが届かなかった。AvalonEdit の TextArea には確実に届いて
            // いる（だからファイルを拒否できている）ので、そこへ直接付ける。
            // handledEventsToo を立てて、AvalonEdit が処理済みにしても動くようにする。
            view.AllowDrop = true;
            view.TextArea.AllowDrop = true;

            AddDropHandlers(view.TextArea);
            AddPasteHandlers(view.TextArea);

            view.LinkActivated += OnLinkActivated;
            view.LinkCopyRequested += (sender, line) => CopyHeadingLink(view, line);
            view.BookmarkToggleRequested += (sender, line) => ToggleHeadingBookmark(view, line);
            view.ImageDisplayToggleRequested += delegate { ToggleImageDisplay(); };
            view.SetLinkTextOnly(_settings.ImageLinkTextOnly);

            // 検索語のお気に入りは、ファイルごとに state.json に覚える（2026-11-04。SPEC 12章）。
            view.SearchFavoritesSource = () =>
            {
                var state = view.FilePath == null ? null : StateOf(view.FilePath);
                if (state == null) return null;
                if (state.SearchFavorites == null) state.SearchFavorites = new List<string>();
                return state.SearchFavorites;
            };
            view.SearchFavoritesEdited += delegate { SaveSearchFavorites(view.FilePath); };
            view.PageLost += delegate { OnPageLost(view); };
            view.TextChanged += OnViewTextChanged;
            view.TextArea.Caret.PositionChanged += delegate { ScheduleOutlineRefresh(); };
            view.TextArea.GotKeyboardFocus += delegate { OnViewFocused(view); };

            // 見出しの履歴に積むきっかけ（2026-10-30）。AvalonEdit が処理済みにしても届くよう handledEventsToo。
            view.TextArea.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(delegate { NoteHeadingClick(view); }), true);
            view.TextArea.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((sender, e) => NoteHeadingKey(view, e)), true);

            return view;
        }

        /// <summary>検索語のお気に入りを変えた。そのファイルの state.json に書き、同じファイルのページの欄を並べ直す。</summary>
        private void SaveSearchFavorites(string path)
        {
            var state = path == null ? null : StateOf(path);
            if (state == null) return;

            var list = state.SearchFavorites;
            if (list != null && list.Count == 0) state.SearchFavorites = null;
            SaveState(path, state);
            if (state.SearchFavorites == null) state.SearchFavorites = list;

            foreach (var other in _stack)
            {
                if (SamePath(other.FilePath, path)) other.RefreshSearchFavorites();
            }
        }

        /// <summary>
        /// 画像の行を、画像だけ ⇔ リンクの文字だけ で切り替える（SPEC 9章）。アプリ全体で1つ。開いている全部のページに配り、覚える。
        /// </summary>
        private void ToggleImageDisplay()
        {
            _settings.ImageLinkTextOnly = !_settings.ImageLinkTextOnly;
            _settings.Save();

            foreach (var view in _stack) view.SetLinkTextOnly(_settings.ImageLinkTextOnly);
        }

        /// <summary>連鎖を捨てて、このファイルのルートページを全幅で開く。</summary>
        private void OpenRoot(string path)
        {
            path = ShellActions.CanonicalPath(path);   // 入口でパスの書き方を揃える。中では文字列で比べる

            // ほかの画面が開いている文書なら、その画面を出すだけ。保存するかを尋ねる前に見る（2026-11-02。前は尋ねてから開かなかった）。
            if (ShowHolderOf(path)) return;

            // 今のページは全部閉じる。同じファイルを開き直すときも、ディスクから読み直す。
            if (!ConfirmLeaving(new MarkdownView[0], null)) return;

            // 今開いているファイルの状態を先に書いておく。同じファイルを開き直すときも、ディスクのものが新しくなる。
            StoreHeadingBookmarks();
            RememberFileStates();

            PageDocument document;
            DocumentState state;
            if (!OpenFile(path, out document, out state)) return;

            _headingBookmarks.Clear();
            _states.Clear();
            AdoptState(path, state);

            _stack.Clear();
            _stack.Add(NewView(document, PageDocument.RootId));
            _focused = _stack[0];

            // 次の起動で開き直すファイル（SPEC 4章）。終了のときだけでなく、開いたときにも書く（落ちた・VM ごと止めたときも残る）。
            if (IsPrimary)
            {
                _settings.LastFile = path;
                _settings.Save();
            }

            RebuildStack(false);
            AfterStackChanged();

            // 開いたものを見せるため、ランチャーを出していれば閉じる（ファイラー・ランチャーのどこから開いても）。
            CloseLauncher();

            RestoreFileState(_stack[0]);
        }

        /// <summary>
        /// 起動時に、前回終了したときに開いていたファイルを開き直す（SPEC 4章）。カーソルの行は OpenRoot が state.json から戻す。
        /// ファイルが見つからなければ（移した・消した・同期がまだ等）黙って何も開かない。覚えた場所は終了時に書き換わる。
        /// </summary>
        private void OpenLastFile()
        {
            // 終了したときの画面（開いていたページの並び・カーソル・ランチャー・AI の欄）を覚えていれば、そのとおりに戻す（2026-10-25）。
            // 戻すときにファイルを開こうとして、フォルダを作るのを断られた・開けないと知らせたときも true（同じことを2度尋ねない。2026-10-27）。
            if (RestoreLastScreen()) return;

            var path = _settings.LastFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            OpenRoot(path);
        }

        /// <summary>
        /// ファイルを開く入口。開くための前提をここで全部確かめ、揃わなければ知らせて開かない（SPEC 4章）。
        /// ・UTF-8 で読めて、ページの区切りが決められること（PageDocument.Load）
        /// ・.文書名/ があること。無ければ作るか尋ね、「いいえ」なら開かない
        /// ・.文書名/state.json が読めること（まだ無ければ空の状態）
        /// ここを通ったファイルには必ずフォルダと状態があるので、中ではそれを確かめない。
        /// </summary>
        private bool OpenFile(string path, out PageDocument document, out DocumentState state)
        {
            document = null;
            state = null;

            // ほかの画面（休止している画面も）で開いている文書は開かず、その画面を前に出す（2026-10-31。state.json と変更の記録を2つの画面から書かないため）。
            if (ShowHolderOf(path)) return false;

            try
            {
                document = PageDocument.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "開けませんでした。" + Environment.NewLine + path + Environment.NewLine + ex.Message,
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var folder = Attachments.FolderOf(path);
            if (!Directory.Exists(folder))
            {
                var answer = MessageBox.Show(this,
                    "このファイルの " + Path.GetFileName(folder) + " フォルダがありません。" + Environment.NewLine +
                    "添付・カーソルの位置・見出しのブックマークを置くフォルダです。作らなければ、このファイルは開きません。" +
                    Environment.NewLine + Environment.NewLine + "作成して開きますか？" + Environment.NewLine + path,
                    "フォルダの作成", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes) return false;

                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "フォルダを作成できなかったため、開きませんでした。" + Environment.NewLine + ex.Message,
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (!LoadState(path, out state)) return false;

            // 開いたときの中身も履歴に残す。前の記録と同じなら何もしない。エディタの外（AI・ほかのソフト）での変更があれば、ここで1つの記録になる。
            // 最初の保存のときに「何が変わったか」が分かるよう、元の状態を先に残しておく意味もある。
            DocumentHistory.Record(path, "開いたときの内容（エディタの外での変更を含む）");

            return true;
        }

        // ---------------------------------------------------------------
        // ファイルごとの状態（SPEC 4章）
        //
        // 開くとき（OpenFile）に .文書名/state.json を読んで手元に持ち、変えたら書く。書く前に読み直さない
        // （アプリは1つしか動かず、1つの文書は1つの画面だけで開く（2026-10-31）ので、手元のものが正しい）。
        // ファイルを開き替える・アプリを閉じるときに、カーソルの行を覚え、
        // 次にそのファイルを開いたときに戻す。ファイルのルートのページの分だけ。
        // ---------------------------------------------------------------

        /// <summary>前の状態を戻す前の（開いた直後の）ビュー。覚え直さない。</summary>
        private readonly HashSet<MarkdownView> _restoring = new HashSet<MarkdownView>();

        /// <summary>開いたファイルの状態を手元に置き、見出しのブックマークを一覧に入れる。</summary>
        private void AdoptState(string path, DocumentState state)
        {
            // 前に開きかけて取りやめた分が残っていれば置き換える（重ねない）。
            _headingBookmarks.RemoveAll(mark => SamePath(mark.File, path));

            _states[path] = state;
            if (state.Headings == null) return;

            foreach (var saved in state.Headings)
            {
                var mark = new HeadingBookmark();
                mark.File = path;
                mark.Page = saved.Page;
                mark.Text = saved.Text;
                mark.Line = saved.Line;
                mark.Screen = saved.Screen;
                _headingBookmarks.Add(mark);
            }
        }

        /// <summary>スタックにある各ファイルのルートのページのカーソルの行を、それぞれの .文書名/state.json に覚える。</summary>
        private void RememberFileStates()
        {
            foreach (var view in _stack)
            {
                if (!string.IsNullOrEmpty(view.PageId)) continue;

                // 開いた直後で、まだ前の状態を戻していないもの（続けて別のファイルを開いた等）は覚え直さない。
                // 覚え直すと、戻すはずだった行を、開いたばかりの状態（先頭）で上書きしてしまう。
                if (_restoring.Contains(view)) continue;

                // スタックにあるファイルの状態は必ずあるはずだが、無くても落ちない（2026-10-27。前は KeyNotFoundException で落ちた）。
                var state = StateOf(view.FilePath);
                if (state == null) continue;

                state.Line = view.TextArea.Caret.Line;
                SaveState(view.FilePath, state);
            }
        }

        /// <summary>
        /// 手元にあるファイルの状態。無ければ null（2026-10-27）。_states[...] で直接引くと、無いときに落ちる。
        /// </summary>
        private DocumentState StateOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            DocumentState state;
            return _states.TryGetValue(path, out state) ? state : null;
        }

        /// <summary>
        /// state.json を読む。読めなければ OpenFile と同じく知らせて false（2026-10-27）。
        /// 手放した状態を読み直すとき（ShowPage）に使う。
        /// </summary>
        private bool LoadState(string path, out DocumentState state)
        {
            try
            {
                state = DocumentState.Load(path);
                return true;
            }
            catch (Exception ex)
            {
                state = null;
                MessageBox.Show(this,
                    "状態のファイル（state.json）を読めないため、開きませんでした。直すか消してから開いてください。" + Environment.NewLine +
                    DocumentState.PathOf(path) + Environment.NewLine + ex.Message,
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>書けなくても編集の邪魔はしない。知らせるだけ。</summary>
        private void SaveState(string path, DocumentState state)
        {
            try
            {
                state.Save(path);
            }
            catch (Exception ex)
            {
                Status("カーソルの位置・見出しのブックマークを保存できませんでした: " + ex.Message);
            }
        }

        /// <summary>開いたファイルの前の状態（カーソルの行）に戻す。行が無ければ先頭のまま。</summary>
        private void RestoreFileState(MarkdownView view)
        {
            var state = StateOf(view.FilePath);
            if (state == null || state.Line < 1) return;

            _restoring.Add(view);

            // 並べ終わってから置く。先に置くと、スクロールが効かない。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _restoring.Remove(view);
                if (!_stack.Contains(view)) return;

                view.ShowLine(state.Line);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// source の右隣に開く。source より右にあったものは閉じる（SPEC 6章）。
        ///
        /// すでに連鎖の中にあるページなら、新しく開かずそこへ戻る。
        /// 同じページを2つのエディタで開くと、それぞれが別のバッファを持ち、
        /// 後から書き戻したほうが相手の編集を消してしまうため。
        /// </summary>
        private void PushPage(MarkdownView source, PageDocument document, string pageId)
        {
            for (var i = 0; i < _stack.Count; i++)
            {
                if (IsSamePage(_stack[i], document, pageId))
                {
                    // 今いちばん右のページなら何も変わらない。組み直すと、保存とフォーカスの外れが無駄に走る。
                    if (i < _stack.Count - 1) PopTo(i);
                    return;
                }
            }

            var index = _stack.IndexOf(source);
            if (index < 0) index = _stack.Count - 1;

            PerfLog.BeginSteps("開く " + pageId);
            var started = PerfLog.Start();
            if (!ConfirmLeaving(_stack.Take(index + 1), document)) return;
            PerfLog.Step("保存の確認", started);

            started = PerfLog.Start();
            var view = NewView(document, pageId);
            PerfLog.Step("画面を作る", started);

            while (_stack.Count > index + 1)
                _stack.RemoveAt(_stack.Count - 1);

            _stack.Add(view);
            _focused = view;

            started = PerfLog.Start();
            RebuildStack(true);
            PerfLog.Step("組み直し", started);
            AfterStackChanged();
            FinishStepsLater();
        }

        /// <summary>帯をクリックしたとき。それより右のページは閉じる（SPEC 6章）。</summary>
        private void PopTo(int index)
        {
            PerfLog.BeginSteps("戻る " + (index >= 0 && index < _stack.Count ? _stack[index].PageId : string.Empty));
            var started = PerfLog.Start();
            if (!ConfirmLeaving(_stack.Take(index + 1), null)) return;
            PerfLog.Step("保存の確認", started);

            while (_stack.Count > index + 1)
                _stack.RemoveAt(_stack.Count - 1);

            _focused = _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

            started = PerfLog.Start();
            RebuildStack(false);
            PerfLog.Step("組み直し", started);
            AfterStackChanged();
            FinishStepsLater();
        }

        /// <summary>
        /// ページを移ったあと、配置・描画・落ち着くまでの経過を perf.log に書く（2026-11-05。app.json の perfLog が true のときだけ）。
        /// Loaded の優先度で動く処理（配置のあと）→ 次の描画 → 手が空いたとき、の順に印を付けて1行にする。
        /// </summary>
        private void FinishStepsLater()
        {
            if (!PerfLog.Stepping) return;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                PerfLog.StepMark("配置後");

                EventHandler rendering = null;
                rendering = delegate
                {
                    CompositionTarget.Rendering -= rendering;
                    PerfLog.StepMark("描画");
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        PerfLog.StepMark("落ち着き");
                        PerfLog.EndSteps();
                    }), DispatcherPriority.ApplicationIdle);
                };
                CompositionTarget.Rendering += rendering;
            }), DispatcherPriority.Loaded);
        }

        private MarkdownView NewView(PageDocument document, string pageId)
        {
            var view = CreateView();
            view.Bind(document, pageId);

            return view;
        }

        /// <summary>同じファイルを二重に読み込まないよう、開いている文書を探す。</summary>
        private PageDocument FindOpenDocument(string path)
        {
            foreach (var view in _stack)
            {
                if (view.Owner != null && SamePath(view.Owner.FilePath, path)) return view.Owner;
            }

            return null;
        }

        private static bool IsSamePage(MarkdownView view, PageDocument document, string pageId)
        {
            if (view.Owner == null) return false;

            var sameFile = ReferenceEquals(view.Owner, document)
                || SamePath(view.Owner.FilePath, document.FilePath);

            return sameFile && string.Equals(view.PageId, pageId ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// 画面に出すのは連鎖の末尾1つだけで、いつも全幅。それより前は左に縦帯としてたたむ（SPEC 6章）。
        /// 2026-10-27 に左右2画面（末尾2つを並べる）と、右のページだけを全幅にする切り替えをやめた。
        /// </summary>
        private void RebuildStack(bool animate)
        {
            var started = PerfLog.Start();

            // 外すと表示位置を失うので、外す前に覚えておく。
            // あわせて、枠を今の入れ物から確実に外す。外し忘れると次に入れられない。
            foreach (var shown in _visible)
            {
                shown.CaptureViewState();
                Detach(shown.Frame);
            }

            _visible.Clear();
            _pageTitles.Clear();
            StackPanelHost.Children.Clear();
            PerfLog.Step("外す", started);

            if (_stack.Count == 0)
            {
                StackPanelHost.Children.Add(CreatePlaceholder());
                return;
            }

            var last = _stack.Count - 1;

            // 帯は横に並べてスクロールできる入れ物に入れる（SPEC 6章。2026-10-27）。
            // 前は帯を直に並べていたので、深く辿ると帯（1本 34px）だけで幅を使い切り、ページの幅が 0 になった。
            // 帯の入れ物の幅は、ページに PageMinWidth を残すところまでにする（FitSpines）。
            _spineScroll = null;
            if (last > 0)
            {
                var spines = new StackPanel();
                spines.Orientation = Orientation.Horizontal;
                for (var i = 0; i < last; i++)
                    spines.Children.Add(CreateSpine(i));

                var scroll = new ScrollViewer();
                scroll.Content = spines;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Focusable = false;
                scroll.PreviewMouseWheel += SpineScroll_PreviewMouseWheel;
                DockPanel.SetDock(scroll, Dock.Left);

                _spineScroll = scroll;
                FitSpines();
                StackPanelHost.Children.Add(scroll);

                // 新しい帯（開いているページのすぐ親）が見えるよう、右端までスクロールしておく。
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (ReferenceEquals(_spineScroll, scroll)) scroll.ScrollToRightEnd();
                }), DispatcherPriority.Loaded);
            }

            var page = _stack[last];

            started = PerfLog.Start();
            StackPanelHost.Children.Add(CreatePageSurface(page));   // LastChildFill
            _visible.Add(page);
            page.RestoreViewState();
            PerfLog.Step("ページを入れる", started);
            PerfLog.StepNote("行数", page.Document.LineCount);

            if (animate) SlideIn(page.Frame);
        }

        /// <summary>
        /// ページ1枚。上端の段の左端（MarkdownView.TitleSlot）にタイトルを入れる（SPEC 6章）。
        /// 2026-10-16 に、別の段の帯をやめて検索の段と1段にまとめた。帯のアイコンもやめた。
        /// 前のページへ戻るのは左の縦帯で。
        /// </summary>
        private UIElement CreatePageSurface(MarkdownView view)
        {
            var title = new TextBlock();
            title.Text = view.Title;
            title.Foreground = (Brush)FindResource("TextPrimary");
            title.FontSize = 13;
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            _pageTitles.Add(title);

            var titleArea = new Border();
            titleArea.Child = title;
            titleArea.Padding = new Thickness(6, 0, 6, 0);
            titleArea.CornerRadius = new CornerRadius(4);
            titleArea.Height = 26;
            titleArea.VerticalAlignment = VerticalAlignment.Center;
            view.TitleSlot.Child = titleArea;

            return view.Frame;
        }

        // ---------------------------------------------------------------
        // ファイラー（SPEC 9章）
        //
        // ランチャーと一緒の画面に置き、「ランチャー」で一緒に出し入れする（SPEC 4章・9章。2026-10-14）。
        // 起動したときに1度だけ作り、閉じても持ったまま（見ていたフォルダ・スクロールが残る）。
        // 出ている間は、見えている本文へドラッグできる。
        // ---------------------------------------------------------------

        private void CreateFiler()
        {
            _filer = new FilerView(Status, OpenInApp, CreateFromFiler, IsInUse, OnFilerRenamed);

            // 前回最後に見ていたフォルダから。無ければ見ているページのフォルダ、それも無ければドキュメント。
            var folder = _settings.FilerFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = CurrentFolder();
            if (string.IsNullOrEmpty(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            _filer.Navigate(folder);

            FilerHost.Child = _filer;
            FilerColumn.Width = new GridLength(_settings.FilerWidth);
        }

        /// <summary>見ているページのファイルがあるフォルダ。開いていなければ null。</summary>
        private string CurrentFolder()
        {
            return HasDocument ? Path.GetDirectoryName(Active.FilePath) : null;
        }

        /// <summary>ランチャーとファイラーの画面を閉じている間は幅が 0 なので、覚え直さない。</summary>
        private void StoreFilerWidth()
        {
            if (FilerColumn.ActualWidth < 180) return;

            _settings.FilerWidth = FilerColumn.ActualWidth;
        }

        /// <summary>見出しを書き換えたら、帯のタイトルも追いかける。</summary>
        private void RefreshPageTitles()
        {
            var count = Math.Min(_pageTitles.Count, _visible.Count);

            for (var i = 0; i < count; i++)
                _pageTitles[i].Text = _visible[i].Title;
        }

        private void Hover(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null) border.Background = (Brush)FindResource("HoverFill");
        }

        /// <summary>作業面を角丸の形で切り抜く。外の枠（半径 8px、太さ 1px）の内側に沿わせて半径 7px。</summary>
        private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Workspace.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        }

        private void UnhoverSpine(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border != null) border.Background = (Brush)FindResource("SidebarBackground");
        }

        /// <summary>今いる入れ物から外す。外さないまま別の親に入れると例外になる。</summary>
        private static void Detach(FrameworkElement element)
        {
            var panel = element.Parent as Panel;
            if (panel != null)
            {
                panel.Children.Remove(element);
                return;
            }

            var decorator = element.Parent as Decorator;
            if (decorator != null) decorator.Child = null;
        }

        // ---------------------------------------------------------------
        // 仕切りの位置（SPEC 3章）
        // ---------------------------------------------------------------

        /// <summary>サイドバーとファイラーまわりの仕切り。位置を覚えておく。</summary>
        private void Layout_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            StoreLayout();
        }

        private void StoreLayout()
        {
            // 幅・ファイラーのフォルダ・窓の位置は全部の画面で共有する。書くのは前に出ている画面だけ（2026-11-02）。
            // 前は閉じる画面がどれも書いたので、「終了」では最後に閉じる最初の画面（隠れていた）の古い値が勝っていた。
            if (ReferenceEquals(FrontWindow(), this))
            {
                _settings.SidebarWidth = SidebarColumn.ActualWidth;
                StoreFilerWidth();
                if (_filer != null && !string.IsNullOrEmpty(_filer.Folder)) _settings.FilerFolder = _filer.Folder;
                StoreWindowBounds();
            }

            if (IsPrimary) _settings.LastFile = _stack.Count > 0 ? _stack[0].FilePath : null;   // 前回のファイルは最初の画面の分だけ
            _settings.Save();
        }

        /// <summary>アウトラインとタイトルは、フォーカスのあるページに切り替える。</summary>
        private void OnViewFocused(MarkdownView view)
        {
            if (ReferenceEquals(_focused, view)) return;

            _focused = view;
            _outlineSignature = null;
            RefreshOutline();
            UpdateChrome();
        }

        private void AfterStackChanged()
        {
            var started = PerfLog.Start();
            SyncHeadingBookmarks();
            PerfLog.Step("見出しのブックマーク", started);

            started = PerfLog.Start();
            _outlineSignature = null;   // 別のページなので見出しの対応付けを作り直す
            RefreshOutline();
            PerfLog.Step("アウトライン", started);

            started = PerfLog.Start();
            UpdateChrome();
            PerfLog.Step("ツールバーなど", started);

            ShowRest();
            ScheduleRememberScreens();   // ファイル・記事を開いた・ページを移ったら、画面の状態を覚える（2026-10-31）

            // 測っているときだけ、配置をここで済ませて、アウトラインと本文のどちらに時間がかかるかを分けて測る。
            if (PerfLog.Stepping)
            {
                started = PerfLog.Start();
                OutlineTree.UpdateLayout();
                PerfLog.Step("アウトラインの配置", started);

                started = PerfLog.Start();
                UpdateLayout();
                PerfLog.Step("残りの配置", started);
            }

            // 開いたページにはフォーカスを渡さない。渡すとキャレットの行が
            // 生ソースになり、読みに来ただけのページが崩れて見える。
            // 閉じたページがフォーカスを持ったまま消えることもあるので、外しておく。
            Keyboard.ClearFocus();
        }

        private Border CreateSpine(int index)
        {
            var title = _stack[index].Title;

            var label = new TextBlock();
            label.Text = title;
            label.MaxWidth = 320;
            label.FontSize = 13;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Foreground = (Brush)FindResource("TextMuted");
            label.VerticalAlignment = VerticalAlignment.Top;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 14, 0, 14);
            label.LayoutTransform = new RotateTransform(90);   // 縦書きにする

            var spine = new Border();
            spine.Width = 34;
            spine.Child = label;
            spine.Cursor = Cursors.Hand;
            spine.ToolTip = title + " まで戻る";
            spine.Tag = index;
            spine.Background = (Brush)FindResource("SidebarBackground");
            spine.BorderBrush = (Brush)FindResource("BorderBrushSoft");
            spine.BorderThickness = new Thickness(0, 0, 1, 0);
            spine.MouseLeftButtonUp += Spine_Click;
            spine.MouseEnter += Hover;
            spine.MouseLeave += UnhoverSpine;

            DockPanel.SetDock(spine, Dock.Left);
            return spine;
        }

        /// <summary>帯を並べたスクロールの入れ物。帯が無ければ null（2026-10-27）。</summary>
        private ScrollViewer _spineScroll;

        /// <summary>帯がいくつあっても、ページに残す幅（2026-10-27）。</summary>
        private const double PageMinWidth = 360;

        /// <summary>帯の入れ物の幅を、ページに PageMinWidth を残すところまでに抑える。入りきらない帯は横にスクロールする。</summary>
        private void FitSpines()
        {
            if (_spineScroll == null) return;

            var width = StackPanelHost.ActualWidth;
            _spineScroll.MaxWidth = width > 0 ? Math.Max(34, width - PageMinWidth) : double.PositiveInfinity;
        }

        private void StackPanelHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            FitSpines();
        }

        /// <summary>帯の上のホイールは横のスクロールにする（縦には動かないので）。</summary>
        private void SpineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scroll = sender as ScrollViewer;
            if (scroll == null) return;

            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        private void Spine_Click(object sender, MouseButtonEventArgs e)
        {
            var spine = sender as Border;
            if (spine == null || !(spine.Tag is int)) return;

            PopTo((int)spine.Tag);
        }

        private static void SlideIn(FrameworkElement element)
        {
            var transform = new TranslateTransform();
            element.RenderTransform = transform;

            var slide = new DoubleAnimation(320, 0, TimeSpan.FromMilliseconds(170));
            slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };

            transform.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        /// <summary>
        /// 何も開いていないときの画面。案内の文だけ。「ランチャー」はツールバーにあるので、文書があってもなくても同じ場所で押せる
        /// （2026-10-31。前はここにもエディタと同じ高さの段を置き、右端に「ランチャー」を出していた）。
        /// </summary>
        private UIElement CreatePlaceholder()
        {
            var text = new TextBlock();
            text.Text = "ツールバーの「ランチャー」からファイルを選ぶか、「新規作成」で作ってください";
            text.FontSize = 15;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextAlignment = TextAlignment.Center;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(24, 0, 24, 0);
            text.Foreground = (Brush)FindResource("TextMuted");

            var body = new Grid();
            body.Children.Add(text);
            return body;
        }

        /// <summary>
        /// 同じファイルか。開く入口（OpenRoot）で本当のパスに揃えてあるので、たいていは文字列で比べるだけで決まる。
        /// 外から来たパス（ファイラーなど）と比べるときは、先に ShellActions.CanonicalPath で揃える。
        ///
        /// 書き方が違えば、両方の本当のパスを尋ね直して比べる（2026-10-27）。開く入口で共有が一瞬見えず、
        /// 本当のパスに揃えられないまま（Z:\ のまま等）開いたものがあると、\\Mac\ の書き方と別のファイルに見え、
        /// 同じファイルを2つのエディタで開いて、後から保存したほうが相手の編集を消すため（SPEC 6章 規則2）。
        /// 尋ねられたときだけ覚えておき（CanonicalCache）、尋ねられなければ今までどおり別のファイルとみなす。
        /// 同じと決めるのは Windows が同じ実体だと答えたときだけなので、別のファイルを同じとみなすことは無い。
        /// </summary>
        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            var realA = CachedCanonicalPath(a);
            if (realA == null) return false;

            var realB = CachedCanonicalPath(b);
            return realB != null && string.Equals(realA, realB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>尋ねて分かった本当のパス。</summary>
        private static readonly Dictionary<string, string> CanonicalCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 尋ねられなかったパスと、その時刻。30 秒は尋ね直さない。共有が切れているときに、比べるたび（1文字打つたび等）
        /// Windows に尋ねて待たされ、画面が固まらないように。
        /// </summary>
        private static readonly Dictionary<string, DateTime> CanonicalMisses =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static string CachedCanonicalPath(string path)
        {
            lock (CanonicalCache)
            {
                string real;
                if (CanonicalCache.TryGetValue(path, out real)) return real;

                DateTime missed;
                if (CanonicalMisses.TryGetValue(path, out missed) && DateTime.UtcNow - missed < TimeSpan.FromSeconds(30)) return null;

                if (!ShellActions.TryCanonicalPath(path, out real))
                {
                    CanonicalMisses[path] = DateTime.UtcNow;
                    return null;
                }

                CanonicalMisses.Remove(path);
                CanonicalCache[path] = real;
                return real;
            }
        }
    }
}

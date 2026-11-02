using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Tsuzuri.Markdown;
using Tsuzuri.Views;
using Tsuzuri.WordPress;

namespace Tsuzuri
{
    // MainWindow の一部: WordPress の記事を開いて直す（2026-10-30）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // WordPress（SPEC 20章）
        //
        // 正は常に WordPress。ツールバーの「WordPress」で記事を選ぶと、本文を読み込んで（HTML → Markdown）このエディタで開く。
        // 保存は手元の一時ファイルまで。WordPress を更新するのは「WordPress」の「アップロード」を押したときだけ（2026-10-30。
        // 初めは保存で更新していたが、Claude に話しかけたときの保存でも更新され、参考に話しかけただけで公開中の記事を上書きしかねないため）。
        // 新しい記事は、開いている .md を写して作る（「この .md を新しい記事にする」）。初めてアップロードしたときに WordPress に作る。閉じるときに、アップロードしていない変更があれば知らせる（アップロードはしない）。
        // 手元には状態を残さない。読み込んだ記事は一時フォルダの .md として開くだけで、エディタを閉じたら（落ちても）それで終わり。
        // 一時フォルダは次に起動したときに消す。手元に残したければ「.md で保存…」で、ふつうの .md として別に書き出す（WordPress とは結び付かない）。
        // 接続先は exe の隣の wordpress.json（WordPressSettings。GitHub に出さない）。
        // ---------------------------------------------------------------

        /// <summary>開いている一時ファイルと、その記事。</summary>
        private sealed class WordPressLink
        {
            public WordPressSite Site;

            /// <summary>投稿の ID。まだ WordPress に作っていない新しい記事は 0。</summary>
            public int Id;

            public string Title;
            public string Status;
            public string Modified;
            public string Link;

            /// <summary>最後に読み込んだ・アップロードした本文（ComposeText）と状態。今と違えば、アップロードしていない変更がある。</summary>
            public string UploadedText;
            public string UploadedStatus;
        }

        /// <summary>一時ファイルのパス → 記事。</summary>
        private readonly Dictionary<string, WordPressLink> _wordPress = new Dictionary<string, WordPressLink>(StringComparer.OrdinalIgnoreCase);

        private static readonly string WordPressTempRoot = Path.Combine(Path.GetTempPath(), "Tsuzuri-WordPress");

        /// <summary>この起動の一時フォルダ（初めて記事を開くときに作る）。</summary>
        private string _wordPressSession;

        private int _wordPressCount;

        /// <summary>一覧の読み込みの番号。打ち直して前の読み込みが後から返ってきたら捨てる。</summary>
        private int _wordPressQuery;

        private static readonly string[] WordPressStatuses = { "draft", "publish", "private", "pending", "future" };

        private static string StatusLabel(string status)
        {
            switch (status)
            {
                case "publish": return "公開";
                case "draft": return "下書き";
                case "private": return "非公開";
                case "pending": return "レビュー待ち";
                case "future": return "予約";
                default: return status ?? string.Empty;
            }
        }

        /// <summary>前の起動の一時フォルダを消す（最初の画面が起動したとき。増やした画面は、ほかの画面の記事を消してしまうので呼ばない）。読み込んだ記事は、エディタを閉じたらそれで終わり。</summary>
        private static void CleanWordPressTemp()
        {
            try
            {
                if (Directory.Exists(WordPressTempRoot)) Directory.Delete(WordPressTempRoot, true);
            }
            catch (Exception)
            {
                // 使用中などで消せなくても、次の起動で消す。
            }

            // フォルダそのものは作っておく。記事の画面の AI の会話は、ここで動いていたので、ここで続きとして開き直す（2026-10-31）。
            try
            {
                Directory.CreateDirectory(WordPressTempRoot);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 起動したとき、終了したときに開いていた記事を WordPress から読み込み直す（2026-10-31）。
        /// サイトは wordpress.json から URL で探す。読めない・見つからなければ知らせて開かない。
        /// </summary>
        private async void ReopenWordPressPost(string url, int id)
        {
            try
            {
                await ReopenWordPressPostCore(url, id);
            }
            finally
            {
                // ここからは今の画面を覚える。ただし読み込めなかったら（回線が無いなど）、何かを開くまでは記事の覚えを残す
                // （2026-11-02。前は空の画面で上書きし、次の起動・休止からの再開で記事が戻らなかった）。知らせは LoadWordPressPost が出す。
                _wordPressRestoring = false;
                _keepSavedScreen = !HasDocument;
                _screenReady = true;
            }
        }

        private async Task ReopenWordPressPostCore(string url, int id)
        {
            WordPressSite site = null;
            try
            {
                var settings = WordPressSettings.Load();
                if (settings != null)
                    site = settings.Sites.FirstOrDefault(each => string.Equals((each.Url ?? string.Empty).TrimEnd('/'), (url ?? string.Empty).TrimEnd('/'),
                                                                               StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
            }

            if (site == null)
            {
                Status("前に開いていた WordPress の記事を開き直せませんでした（wordpress.json にそのサイトがありません）。#" + id);
                return;
            }

            await LoadWordPressPost(site, id);
        }

        /// <summary>この起動の一時フォルダを消す（エディタを閉じるとき）。消せなければ（AI の欄の claude が掴んでいる等）、次の起動で消す。</summary>
        private void DeleteWordPressSession()
        {
            if (_wordPressSession == null) return;

            try
            {
                if (Directory.Exists(_wordPressSession)) Directory.Delete(_wordPressSession, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>開いているページが WordPress の記事なら、その記事。</summary>
        private WordPressLink WordPressLinkOf(MarkdownView view)
        {
            return view == null ? null : WordPressLinkOf(view.Owner);
        }

        private WordPressLink WordPressLinkOf(PageDocument document)
        {
            WordPressLink link;
            return document != null && document.FilePath != null && _wordPress.TryGetValue(document.FilePath, out link) ? link : null;
        }

        /// <summary>WordPress の記事で、アップロードしていない変更（本文・状態）があるか。保存していない編集も含めて比べる。</summary>
        private bool HasPendingUpload(PageDocument document)
        {
            var link = WordPressLinkOf(document);
            if (link == null) return false;

            return !string.Equals(document.ComposeText(), link.UploadedText, StringComparison.Ordinal)
                || !string.Equals(link.Status, link.UploadedStatus, StringComparison.Ordinal);
        }

        /// <summary>
        /// 画面から外れる文書のうち、アップロードしていない変更がある WordPress の記事があれば、捨ててよいか確かめる（ConfirmLeaving から）。
        /// ここではアップロードしない。アップロードは「アップロード」のボタンだけ（2026-10-30。閉じるときに尋ねてアップロードするのもやめた）。
        /// 「OK」なら変更は捨てて進む。「キャンセル」なら false（閉じない。戻ってアップロードできる）。
        /// 一時ファイルに保存するかは尋ねない（手元には残らないので、保存しても意味がない）。
        /// </summary>
        private bool ConfirmLeavingWordPress(List<PageDocument> leaving)
        {
            var pending = leaving.Where(HasPendingUpload).ToList();
            if (pending.Count == 0) return true;

            var names = string.Join(Environment.NewLine, pending.Select(document =>
            {
                var link = WordPressLinkOf(document);
                return "・" + (link.Id > 0 ? "#" + link.Id + " " : "（新しい記事）") + TitleOf(document, link);
            }).ToArray());

            ShowBeforeAsking();
            var answer = MessageBox.Show(this,
                "次の WordPress の記事に、アップロードしていない変更があります。" + Environment.NewLine + Environment.NewLine + names +
                Environment.NewLine + Environment.NewLine + "このまま閉じると、変更は捨てます（手元には残りません）。" + Environment.NewLine +
                "アップロードするなら「キャンセル」で戻り、「WordPress」→「アップロード」を押してください。",
                "アップロードしていない変更", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            return answer == MessageBoxResult.OK;
        }

        private static string TitleOf(PageDocument document, WordPressLink link)
        {
            string title, body;
            SplitTitle(document.ComposeText(), out title, out body);
            return string.IsNullOrWhiteSpace(title) ? System.Net.WebUtility.HtmlDecode(link.Title ?? string.Empty) : title;
        }

        // ---------------------------------------------------------------
        // ツールバーの「WordPress」
        // ---------------------------------------------------------------

        private void WordPress_Click(object sender, RoutedEventArgs e)
        {
            WordPressSettings settings;
            try
            {
                settings = WordPressSettings.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "wordpress.json を読めませんでした。書き方を確かめてください。" + Environment.NewLine +
                    WordPressSettings.StorePath + Environment.NewLine + ex.Message, "WordPress", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (settings == null || settings.Sites.Count == 0)
            {
                OfferWordPressSettings(settings == null);
                return;
            }

            ShowWordPressPopup((UIElement)sender, settings.Sites);
        }

        /// <summary>接続先がまだ無い。ひな形を書いて、メモ帳で開くか尋ねる。</summary>
        private void OfferWordPressSettings(bool missing)
        {
            try
            {
                WordPressSettings.WriteTemplate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "wordpress.json を作れませんでした。" + Environment.NewLine + ex.Message, "WordPress",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(this,
                (missing ? "WordPress の接続先がまだありません。ひな形を作りました。" : "wordpress.json に、使える接続先がありません（ひな形のまま等）。") +
                Environment.NewLine + Environment.NewLine +
                "url・user・appPassword を書いてください。appPassword は WordPress の「ユーザー → プロフィール → アプリケーションパスワード」で作るものです（ログインのパスワードではありません）。" +
                Environment.NewLine + Environment.NewLine + WordPressSettings.StorePath + Environment.NewLine + Environment.NewLine +
                "メモ帳で開きますか？",
                "WordPress", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                Process.Start("notepad.exe", "\"" + WordPressSettings.StorePath + "\"");
            }
            catch (Exception ex)
            {
                Status("メモ帳を開けませんでした: " + ex.Message);
            }
        }

        /// <summary>
        /// 記事を選ぶ画面（ツールバーのボタンの下に出す、枠の無い小さな窓）。
        /// Popup ではなく窓にしている（2026-10-30）。Popup は自分ではアクティブにならない窓で、検索欄で日本語を打つと、
        /// IME の変換がエディタの窓の左上に出た（透明をやめても直らなかった）。ほかを押して窓から離れたら閉じる。
        /// 上から: サイト（2つ以上あるとき）・検索・記事の一覧・「この .md を新しい記事にする」。開いているのが WordPress の記事なら、その記事の状態と操作も出す。
        /// 検索欄には、言葉のほか、投稿の ID や URL（?p=123）も入れられる。
        /// </summary>
        private void ShowWordPressPopup(UIElement anchor, List<WordPressSite> sites)
        {
            var panel = new StackPanel { Width = 540 };

            Window window = null;
            var closing = false;
            Action close = () =>
            {
                if (closing || window == null) return;
                closing = true;
                window.Close();
            };

            panel.Children.Add(PopupHeader("WordPress の記事を開く"));

            ComboBox siteBox = null;
            if (sites.Count > 1)
            {
                siteBox = ExportChoice(sites.Select(site => site.DisplayName).ToArray(), sites.Cast<object>().ToArray());
                siteBox.Margin = new Thickness(0, 0, 0, 8);
                panel.Children.Add(siteBox);
            }

            Func<WordPressSite> currentSite = () => siteBox == null ? sites[0] : (WordPressSite)((ComboBoxItem)siteBox.SelectedItem).Tag;

            var search = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("TextPrimary"),
                CaretBrush = (Brush)FindResource("TextPrimary"),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "タイトル・本文の言葉で探す。投稿の ID や URL（?p=123）も入れられる（数字だけなら、その ID の記事と、その数字で検索した結果の両方を出す）。Enter で探す",
            };
            var searchFrame = new Border
            {
                BorderBrush = (Brush)FindResource("InputBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                Child = search,
            };
            panel.Children.Add(searchFrame);

            var note = new TextBlock
            {
                FontSize = 12,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(2, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(note);

            var list = new StackPanel();
            panel.Children.Add(new ScrollViewer
            {
                Content = list,
                Height = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

            Action<WordPressPost> open = post =>
            {
                close();
                OpenWordPressPost(currentSite(), post.Id);
            };

            Action load = () => LoadWordPressList(currentSite(), search.Text, list, note, open, 1);

            // 開いている .md（WordPress の記事でないもの）を、新しい記事にする（2026-10-30。空の「新しい記事」の代わり）。
            var original = HasDocument && WordPressLinkOf(Active) == null ? Active : null;
            var create = new Button { Content = "この .md を新しい記事にする", Style = (Style)FindResource("ToolButton") };
            create.IsEnabled = original != null;
            create.ToolTip = original != null
                ? "「" + Path.GetFileName(original.FilePath) + "」の今の内容を写した新しい記事を開く。元の .md はそのまま。初めてアップロードしたときに、WordPress に作る（既定は下書き）"
                : "ふつうの .md を開いているときに使える（その内容を写した新しい記事を開く）";
            create.Click += delegate
            {
                close();
                if (original != null) NewWordPressPostFrom(currentSite(), original);
            };

            var closeButton = new Button { Content = "閉じる", Style = (Style)FindResource("ToolButton"), Margin = new Thickness(6, 0, 0, 0) };
            closeButton.Click += delegate { close(); };

            var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
            DockPanel.SetDock(create, Dock.Left);
            DockPanel.SetDock(closeButton, Dock.Right);
            buttons.Children.Add(create);
            buttons.Children.Add(closeButton);
            panel.Children.Add(buttons);

            // 開いているのが WordPress の記事なら、その記事の状態・ブラウザ・手元への保存。
            var view = Active;
            var current = HasDocument ? WordPressLinkOf(view) : null;
            if (current != null) AddCurrentPostSection(panel, close, view, current);

            search.KeyDown += (s, args) =>
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                load();
            };

            if (siteBox != null) siteBox.SelectionChanged += delegate { load(); };

            panel.PreviewKeyDown += (s, args) =>
            {
                if (args.Key != Key.Escape || (siteBox != null && siteBox.IsDropDownOpen)) return;
                args.Handled = true;
                close();
            };

            // 透明にはしない（影と角丸は付けない）。透明な窓（レイヤードウィンドウ）でも IME の位置が狂うことがあるため。
            var frame = PopupFrame(panel);
            frame.Effect = null;
            frame.Margin = new Thickness(0);
            frame.CornerRadius = new CornerRadius(0);

            window = new Window
            {
                Owner = this,
                Content = frame,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = (Brush)FindResource("SidebarBackground"),
            };

            // ボタンの左下に置く（画面の座標は物理ピクセルなので、WPF の単位に直す）。
            var corner = anchor.PointToScreen(new Point(0, anchor.RenderSize.Height));
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null) corner = source.CompositionTarget.TransformFromDevice.Transform(corner);
            window.Left = corner.X;
            window.Top = corner.Y + 2;

            window.Deactivated += delegate { close(); };
            window.Closed += delegate { closing = true; };
            window.Loaded += delegate { search.Focus(); };
            window.Show();

            load();
        }

        /// <summary>今開いている記事の欄。状態（下書き・公開…）は、次に保存したときに WordPress に送る。</summary>
        private void AddCurrentPostSection(StackPanel panel, Action close, MarkdownView view, WordPressLink link)
        {
            var divider = new Border { Height = 1, Background = (Brush)FindResource("BorderBrushSoft"), Margin = new Thickness(0, 12, 0, 12) };
            panel.Children.Add(divider);

            panel.Children.Add(PopupHeader(link.Id > 0 ? "開いている記事（#" + link.Id + "）" : "開いている記事（まだ WordPress にありません）"));

            var statuses = WordPressStatuses.Where(status => status != "future" || link.Status == "future").ToArray();
            var statusBox = ExportChoice(statuses.Select(StatusLabel).ToArray(), statuses.Cast<object>().ToArray());
            statusBox.Width = 150;
            statusBox.SelectedIndex = Math.Max(0, Array.IndexOf(statuses, link.Status));
            statusBox.ToolTip = "次にアップロードしたときに、WordPress の状態もこれにする";
            statusBox.SelectionChanged += delegate
            {
                var status = (string)((ComboBoxItem)statusBox.SelectedItem).Tag;
                if (status == link.Status) return;

                link.Status = status;
                UpdateChrome();
                Status("アップロードすると、WordPress の状態を「" + StatusLabel(status) + "」にします。");
            };

            var label = new TextBlock
            {
                Text = "状態",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var upload = new Button { Content = link.Id > 0 ? "アップロード" : "WordPress に作る", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(12, 0, 0, 0) };
            upload.ToolTip = "今の画面の内容（保存していない編集も含む）で、WordPress の記事を更新する。新しい記事なら作る";
            upload.Click += delegate
            {
                close();
                if (view.Owner != null && UploadWordPress(view.Owner)) Status("WordPress に反映しました（" + StatusLabel(link.Status) + "）: #" + link.Id + " " + link.Title);
            };

            var browse = new Button { Content = "ブラウザで見る", Style = (Style)FindResource("ToolButton"), Margin = new Thickness(2, 0, 0, 0) };
            browse.IsEnabled = link.Id > 0 && !string.IsNullOrEmpty(link.Link);
            browse.ToolTip = "WordPress に反映済みの内容を見る（アップロードしていない変更は入っていない）";
            browse.Click += delegate
            {
                close();
                try
                {
                    // Launch は使わない（URL の ? を Path.GetDirectoryName が受け付けない）。
                    Process.Start(new ProcessStartInfo(link.Link) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Status("開けませんでした: " + ex.Message);
                }
            };

            var local = new Button { Content = ".md で保存…", Style = (Style)FindResource("ToolButton"), Margin = new Thickness(2, 0, 0, 0) };
            local.ToolTip = "今の画面の内容を、ふつうの .md として手元に書き出す。WordPress とは結び付かない";
            local.Click += delegate
            {
                close();
                SaveWordPressLocally(view);
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(label);
            row.Children.Add(statusBox);
            row.Children.Add(upload);
            row.Children.Add(browse);
            row.Children.Add(local);
            panel.Children.Add(row);
        }

        private TextBlock PopupHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("SidebarHeaderText"),
                Margin = new Thickness(0, 0, 0, 10),
            };
        }

        /// <summary>エクスポートの画面と同じ枠（サイドバーの地・角丸・影）。</summary>
        private Border PopupFrame(UIElement child)
        {
            var frame = new Border
            {
                Background = (Brush)FindResource("SidebarBackground"),
                BorderBrush = (Brush)FindResource("FrameBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(10, 4, 10, 14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.45,
                },
                Child = child,
            };

            System.Windows.Documents.TextElement.SetFontFamily(frame, FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(frame, 13);
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);
            return frame;
        }

        /// <summary>
        /// 一覧を読み込む（裏のスレッドで）。検索欄が URL なら、その1件。数字だけなら、その ID の1件と、その数字で検索した結果。
        /// 1回に 100 件。まだ残りがあれば、一覧の一番下に「さらに読む」を置き、押すと次の 100 件を下に足す（2026-10-31。前は 100 件で打ち切り）。
        /// 検索は WordPress の側で全記事を探すので、古い記事は検索でも見つかる。
        /// </summary>
        private async void LoadWordPressList(WordPressSite site, string query, StackPanel list, TextBlock note, Action<WordPressPost> open, int page)
        {
            var ticket = ++_wordPressQuery;
            if (page == 1) list.Children.Clear();
            note.Text = "読み込んでいます… " + site.DisplayName;

            var id = PostIdOf(query);
            var searchToo = id > 0 && IsNumberOnly(query);   // 数字だけなら ID でも文字でも探す。URL は ID だけ
            var words = searchToo ? query.Trim().TrimStart('#') : query;   // 「#050」は「050」で探す
            if (page == 1) list.Tag = null;
            var byIdOnly = false;   // ID で見つけた記事が、文字の検索の結果に無い（件数に数えない）
            List<WordPressPost> posts;
            var total = -1;
            string error = null;

            try
            {
                posts = await Task.Run(() =>
                {
                    var client = new WordPressClient(site);
                    if (id <= 0) return client.List(query, page, out total);
                    if (!searchToo)
                    {
                        var one = client.Find(id);
                        return one == null ? new List<WordPressPost>() : new List<WordPressPost> { one };
                    }

                    // 数字だけなら、その ID の記事を先頭に置き、続けて同じ文字で検索した結果を並べる（2026-11-01）。
                    // 前は ID としてだけ探したので、「050」のように題名にある数字で探すと見つからなかった。
                    var found = new List<WordPressPost>();
                    if (page == 1)
                    {
                        try
                        {
                            var byId = client.Find(id);
                            if (byId != null) found.Add(byId);
                        }
                        catch (Exception)
                        {
                            // その ID の記事が無い。検索の結果だけを出す
                        }
                    }

                    var searched = client.List(words, page, out total);
                    byIdOnly = found.Count > 0 && searched.All(post => post.Id != found[0].Id);
                    found.AddRange(searched);
                    return found;
                });
            }
            catch (Exception ex)
            {
                posts = null;
                error = ex.Message;
            }

            if (ticket != _wordPressQuery) return;

            if (posts == null)
            {
                note.Text = error;
                return;
            }

            // 読んでいる間に記事が更新されると、更新順の並びがずれて、前の枚と同じ記事が来ることがある。並んでいるものは足さない。
            var shown = new HashSet<int>(list.Children.OfType<FrameworkElement>().Select(row => row.Tag).OfType<int>());
            foreach (var post in posts)
            {
                if (shown.Add(post.Id)) list.Children.Add(PostRow(post, open));
            }

            // 件数・残りは文字の検索の総数と比べるので、ID で見つけただけの1件は数えない（2026-11-02。前は「101 / 100 件」になりえた）。
            if (page == 1 && byIdOnly) list.Tag = posts[0].Id;
            var extra = list.Tag as int?;
            if (extra != null && page > 1 && posts.Any(post => post.Id == extra.Value)) list.Tag = extra = null;   // 後の枚で検索にも出た

            var count = shown.Count;
            var searchedCount = count - (extra != null ? 1 : 0);
            if (count == 0)
            {
                note.Text = "見つかりませんでした。";
                return;
            }

            var of = total > searchedCount ? searchedCount + " / " + total + " 件" + (extra != null ? "（ほかに ID の1件）" : string.Empty) : count + " 件";
            note.Text = (string.IsNullOrWhiteSpace(query) ? "最近更新した記事（" + of + "）。" : of + "。") + "押すと開きます。";

            // 残りがあり、この枚が満杯だったときだけ（総数が分からなくても、満杯なら続きがあるかもしれない）。
            if ((id <= 0 || searchToo) && posts.Count - (page == 1 && byIdOnly ? 1 : 0) >= WordPressClient.PageSize && (total < 0 || total > searchedCount))
            {
                var more = new Button
                {
                    Content = total > searchedCount ? "さらに読む（残り " + (total - searchedCount) + " 件）" : "さらに読む",
                    Style = (Style)FindResource("ToolButton"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 6),
                };
                more.Click += delegate
                {
                    list.Children.Remove(more);
                    LoadWordPressList(site, query, list, note, open, page + 1);
                };
                list.Children.Add(more);
            }
        }

        private static readonly Regex PostIdInUrl = new Regex(@"[?&](?:p|post|page_id)=(\d+)", RegexOptions.Compiled);

        /// <summary>検索欄の ID（数字だけ）・URL（?p=123・post.php?post=123）から投稿の ID を取る。無ければ 0。</summary>
        private static int PostIdOf(string query)
        {
            query = (query ?? string.Empty).Trim().TrimStart('#');
            int id;
            if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return id;

            var match = PostIdInUrl.Match(query);
            return match.Success && int.TryParse(match.Groups[1].Value, out id) ? id : 0;
        }

        /// <summary>検索欄が数字だけ（先頭の # は除く）か。</summary>
        private static bool IsNumberOnly(string query)
        {
            query = (query ?? string.Empty).Trim().TrimStart('#');
            return query.Length > 0 && query.All(c => c >= '0' && c <= '9');
        }

        private Border PostRow(WordPressPost post, Action<WordPressPost> open)
        {
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(post.Title) ? "（無題）" : System.Net.WebUtility.HtmlDecode(post.Title),
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var meta = new TextBlock
            {
                Text = StatusLabel(post.Status) + "  " + DateLabel(post.Modified),
                FontSize = 12,
                Foreground = (Brush)FindResource("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(meta, 1);
            grid.Children.Add(title);
            grid.Children.Add(meta);

            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = grid,
                ToolTip = "#" + post.Id + "  " + post.Link,
                Tag = post.Id,   // 「さらに読む」で同じ記事を二度並べないため
            };
            row.MouseEnter += delegate { row.Background = (Brush)FindResource("HoverFill"); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += delegate { open(post); };
            return row;
        }

        private static string DateLabel(string modifiedGmt)
        {
            DateTime time;
            if (!DateTime.TryParse(modifiedGmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
                return string.Empty;
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        // ---------------------------------------------------------------
        // 開く・新しく作る
        // ---------------------------------------------------------------

        /// <summary>記事を読み込んで開く。タイトルは1行目の見出し（# タイトル）にする。</summary>
        private async void OpenWordPressPost(WordPressSite site, int id)
        {
            await LoadWordPressPost(site, id);
        }

        /// <summary>OpenWordPressPost の中身。起動のときに読み込み直すときは、終わるのを待つ（ReopenWordPressPost）。</summary>
        private async Task LoadWordPressPost(WordPressSite site, int id)
        {
            // ほかの画面で開いている記事は読み込まず、その画面を前に出す（2026-10-31。同じ記事を2か所で直して食い違わないように）。
            if (ShowHolderOfPost(site.Url, id)) return;

            Status("WordPress から読み込んでいます… #" + id);

            WordPressPost post;
            string markdown;
            try
            {
                post = await Task.Run(() => new WordPressClient(site).Get(id));
                markdown = "# " + HtmlToMarkdown.EscapeLine(post.Title) + "\n\n" + HtmlToMarkdown.Convert(post.Content) + "\n";
            }
            catch (Exception ex)
            {
                if (!OpenWindows.Contains(this)) return;   // 読み込んでいる間に画面を閉じた

                ShowBeforeAsking();
                MessageBox.Show(this, "WordPress から読み込めませんでした。" + Environment.NewLine + ex.Message, "WordPress",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Status("読み込めませんでした。");
                return;
            }

            // 読み込んでいる間に画面を閉じた・休止したら、開かない（2026-11-02。前は閉じた画面で開き、消した一時フォルダを作り直していた）。
            if (!OpenWindows.Contains(this)) return;

            var link = new WordPressLink
            {
                Site = site,
                Id = post.Id,
                Title = post.Title,
                Status = post.Status,
                Modified = post.Modified,
                Link = post.Link,
            };

            if (OpenWordPressText(link, markdown))
                Status("WordPress の記事を開きました（" + StatusLabel(post.Status) + "）。WordPress を更新するのは「WordPress」→「アップロード」です。");
        }

        /// <summary>
        /// 開いている .md の内容を写した新しい記事を開く（2026-10-30）。ふだんは手元で .md を書き、それを WordPress に上げるため。
        /// ・ファイル全体（サブページを含む。保存していない編集も含む）を写す。元の .md は変えず、WordPress とも結び付けない
        /// ・1行目（空行を除く）が「# 」の見出しでなければ、ファイル名をタイトルとして先頭に足す
        /// ・手元の画像のリンクは、元の .md の場所を基準に解いた絶対パスに直す（一時フォルダからは相対パスで辿れないため）。
        ///   アップロードのとき、その画像もメディアに上がる
        /// </summary>
        private void NewWordPressPostFrom(WordPressSite site, MarkdownView source)
        {
            if (source.Owner == null) return;

            var text = source.Owner.ComposeText().Replace("\r\n", "\n");
            text = AbsolutizeImageLinks(text, source.FilePath);

            string title, body;
            SplitTitle(text, out title, out body);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(source.FilePath);
                text = "# " + HtmlToMarkdown.EscapeLine(title) + "\n\n" + text.TrimStart('\n');
            }

            var link = new WordPressLink { Site = site, Id = 0, Title = title, Status = "draft" };
            if (OpenWordPressText(link, text))
                Status("「" + Path.GetFileName(source.FilePath) + "」を写した新しい記事です。「WordPress」→「WordPress に作る」で、WordPress に作ります（既定は下書き）。");
        }

        /// <summary>手元の画像のリンク先を、元の文書の場所を基準にした絶対パスに書き換える。URL・見つからないものはそのまま。</summary>
        private static string AbsolutizeImageLinks(string text, string documentPath)
        {
            var parsed = WordPressHtml.Parse(text);
            var edits = new List<KeyValuePair<global::Markdig.Syntax.SourceSpan, string>>();

            foreach (var image in parsed.Descendants<Markdig.Syntax.Inlines.LinkInline>().Where(each => each.IsImage))
            {
                var span = image.UrlSpan;
                if (span.IsEmpty || span.Start < 0 || span.End >= text.Length) continue;   // 参照形式などで場所が分からない

                var path = LocalImagePath(documentPath, image.Url);
                if (path == null || Path.IsPathRooted(image.Url ?? string.Empty)) continue;

                // 区切りは / にする。Markdown のリンクの中の \ は逃がしの記号で、\.doc の \ などが消えてしまうため（Windows は / でも読める）。
                edits.Add(new KeyValuePair<global::Markdig.Syntax.SourceSpan, string>(span, ToLinkTarget(path.Replace('\\', '/'))));
            }

            // 後ろから置き換える（前を変えると後ろの場所がずれる）。UrlSpan は <…> の囲みを含むので、囲みごと置き換わる。
            var builder = new StringBuilder(text);
            foreach (var edit in edits.OrderByDescending(each => each.Key.Start))
            {
                var start = edit.Key.Start;
                var end = edit.Key.End;
                builder.Remove(start, end - start + 1).Insert(start, edit.Value);
            }

            return builder.ToString();
        }

        /// <summary>一時フォルダに .md を書いて開く。開けたら true（保存するか尋ねてキャンセルされたら false）。</summary>
        private bool OpenWordPressText(WordPressLink link, string markdown)
        {
            string path;
            try
            {
                if (_wordPressSession == null)
                    _wordPressSession = Path.Combine(WordPressTempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + WindowKey);   // 画面ごとに分ける

                var folder = Path.Combine(_wordPressSession, (link.Id > 0 ? link.Id.ToString(CultureInfo.InvariantCulture) : "new") + "-" + (++_wordPressCount));
                Directory.CreateDirectory(folder);

                var name = string.IsNullOrWhiteSpace(link.Title) ? "記事" : SafeFileName(System.Net.WebUtility.HtmlDecode(link.Title));
                if (name.Length > 40) name = name.Substring(0, 40).Trim();
                path = Path.Combine(folder, name + ".md");

                File.WriteAllText(path, markdown, new UTF8Encoding(false));
                Directory.CreateDirectory(Attachments.FolderOf(path));   // 開くときに「フォルダを作りますか」と尋ねさせない
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "一時ファイルを作れませんでした。" + Environment.NewLine + ex.Message, "WordPress",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            path = ShellActions.CanonicalPath(path);
            _wordPress[path] = link;
            OpenRoot(path);

            if (_stack.Count > 0 && SamePath(_stack[0].FilePath, path))
            {
                // 読み込んだ中身を「アップロード済み」として覚える（読み直した形で比べる）。
                link.UploadedText = _stack[0].Owner.ComposeText();
                link.UploadedStatus = link.Status;
                UpdateChrome();
                return true;
            }

            _wordPress.Remove(path);
            return false;
        }

        // ---------------------------------------------------------------
        // アップロード（「WordPress」の「アップロード」だけ）
        // ---------------------------------------------------------------

        private static readonly Regex TitleLine = new Regex(@"^#[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);

        /// <summary>
        /// WordPress の記事を更新する（新しい記事なら作る）。保存していない編集があれば、先に一時ファイルに保存する。反映できたら true。
        /// 1行目（空行を除く）が「# 」の見出しならタイトル、残りが本文。見出しが無ければ、タイトルは前のまま。
        /// 読み込んだあとに WordPress 側で直されていたら、上書きするか尋ねる。
        /// 送れたら、WordPress から読み込み直したときと同じ中身にする（ShowAsDownloaded）。
        /// 待つ（同期。砂時計）。
        /// </summary>
        private bool UploadWordPress(PageDocument document)
        {
            var link = WordPressLinkOf(document);
            if (link == null) return true;

            if (document.IsDirty && !SaveDocuments(new List<PageDocument> { document })) return false;

            var text = document.ComposeText();
            string title, body;
            SplitTitle(text, out title, out body);
            if (string.IsNullOrWhiteSpace(title)) title = System.Net.WebUtility.HtmlDecode(link.Title ?? string.Empty);

            var client = new WordPressClient(link.Site);
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var html = BuildWordPressHtml(body, document.FilePath, link.Site, client);

                if (link.Id > 0)
                {
                    var modified = client.ModifiedOf(link.Id);
                    if (!string.IsNullOrEmpty(link.Modified) && !string.Equals(modified, link.Modified, StringComparison.Ordinal))
                    {
                        Mouse.OverrideCursor = null;
                        var answer = MessageBox.Show(this,
                            "この記事は、読み込んだあとに WordPress 側で更新されています（" + DateLabel(modified) + "）。" + Environment.NewLine +
                            Environment.NewLine + "上書きすると、WordPress 側での変更は失われます。上書きしますか？" + Environment.NewLine +
                            "「いいえ」ならアップロードしません（編集は画面に残ります）。",
                            "WordPress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                        if (answer != MessageBoxResult.Yes)
                        {
                            Status("アップロードしませんでした（編集は画面に残っています）。");
                            return false;
                        }

                        Mouse.OverrideCursor = Cursors.Wait;
                    }
                }

                var saved = client.Save(link.Id, title, html, link.Status);
                link.Id = saved.Id;
                link.Title = saved.Title;
                link.Status = saved.Status;
                link.Modified = saved.Modified;
                link.Link = saved.Link;
                link.UploadedText = text;
                link.UploadedStatus = saved.Status;

                // アップロードしたら、WordPress から読み込んだときと同じ状態にする（2026-10-30）。
                // 手元の画像のリンクはメディアの URL に、本文は WordPress に入った HTML を戻した Markdown になる。
                ShowAsDownloaded(document, link, saved);
                return true;
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, "WordPress に反映できませんでした。" + Environment.NewLine + ex.Message + Environment.NewLine + Environment.NewLine +
                    "編集は画面に残っています。もう一度アップロードしてください。", "WordPress",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                UpdateChrome();
            }
        }

        /// <summary>アップロードした画像。サイトと中身（SHA-256）→ URL。同じ画像を2度送らない（この起動の間だけ覚える）。</summary>
        private readonly Dictionary<string, string> _wordPressMedia = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 本文を HTML にする。手元の画像（貼り付け・ドロップ・図）は、先にメディアにアップロードし、送る HTML ではその URL を指す（2026-10-30）。
        /// エディタの本文は手元のリンクのまま（画面では画像が見えたまま）。中身の同じ画像は2度送らない。描き直した図は中身が変わるので送り直す。
        /// URL の画像（https://…）はそのまま。開いたときの記事の画像は URL のリンクなので、送り直さない。
        /// 画像を替えたいときは、リンクを消して手元の画像を貼り直す。
        /// </summary>
        private string BuildWordPressHtml(string body, string documentPath, WordPressSite site, WordPressClient client)
        {
            var parsed = WordPressHtml.Parse(body);

            foreach (var image in parsed.Descendants<Markdig.Syntax.Inlines.LinkInline>().Where(each => each.IsImage).ToList())
            {
                var path = LocalImagePath(documentPath, image.Url);
                if (path == null) continue;

                byte[] data;
                try
                {
                    data = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    throw new WordPressException("画像を読めませんでした: " + path + Environment.NewLine + ex.Message);
                }

                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty);

                var key = site.Url + "|" + hash;
                string url;
                if (!_wordPressMedia.TryGetValue(key, out url))
                {
                    Status("画像をアップロードしています: " + Path.GetFileName(path));
                    try
                    {
                        url = client.UploadMedia(Path.GetFileName(path), data);
                    }
                    catch (WordPressException ex)
                    {
                        throw new WordPressException("画像をアップロードできませんでした: " + Path.GetFileName(path) + Environment.NewLine + ex.Message);
                    }

                    _wordPressMedia[key] = url;
                }

                image.Url = url;
            }

            return WordPressHtml.Render(parsed);
        }

        /// <summary>画像のリンク先が手元のファイルなら、そのパス。URL・見つからないものは null（エディタのリンクの解き方と同じ）。</summary>
        private static string LocalImagePath(string documentPath, string target)
        {
            if (string.IsNullOrEmpty(target) || target.IndexOf("://", StringComparison.Ordinal) >= 0
                || target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

            var anchor = target.IndexOf('#');
            if (anchor >= 0) target = target.Substring(0, anchor);
            if (target.Length == 0) return null;

            try
            {
                var decoded = Attachments.Combine(documentPath, Uri.UnescapeDataString(target));
                if (decoded != null && File.Exists(decoded)) return decoded;
            }
            catch (ArgumentException)
            {
            }

            try
            {
                var raw = Attachments.Combine(documentPath, target);
                return raw != null && File.Exists(raw) ? raw : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// アップロードした記事を、WordPress から読み込み直したときと同じ中身にする（返ってきた本文を HTML → Markdown）。
        /// 一時ファイルに書いて読み直す（外での書き換えを読み直すときと同じ。元に戻すの履歴は捨てる）。
        /// 戻せなければ（返事に本文が無い等）、画面はアップロードしたときのまま。
        /// </summary>
        private void ShowAsDownloaded(PageDocument document, WordPressLink link, WordPressPost saved)
        {
            if (saved.Content == null) return;

            try
            {
                var markdown = "# " + HtmlToMarkdown.EscapeLine(saved.Title) + "\n\n" + HtmlToMarkdown.Convert(saved.Content) + "\n";
                File.WriteAllText(document.FilePath, markdown, new UTF8Encoding(false));
                document.Reload();
            }
            catch (Exception ex)
            {
                Status("アップロードしましたが、読み込み直した形に戻せませんでした: " + ex.Message);
                return;
            }

            // 無くなったページ（サブページの印が変わった等）から右は閉じる。ルートはいつもあるので、先頭は残る。
            var lost = _stack.FindIndex(view => view.Owner == document && !document.Contains(view.PageId));
            if (lost > 0 && KeepClosingDocuments(lost))
            {
                while (_stack.Count > lost) _stack.RemoveAt(_stack.Count - 1);
                if (!_stack.Contains(_focused)) _focused = _stack[_stack.Count - 1];
            }
            else
            {
                lost = -1;
            }

            foreach (var view in _stack)
            {
                if (view.Owner == document) view.ReloadFromOwner();
            }

            if (lost > 0) RebuildStack(false);

            _outlineSignature = null;
            RefreshOutline();
            RefreshPageTitles();

            link.UploadedText = document.ComposeText();
            UpdateChrome();
        }

        private static void SplitTitle(string text, out string title, out string body)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            var i = 0;
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;

            var match = i < lines.Length ? TitleLine.Match(lines[i]) : Match.Empty;
            if (!match.Success)
            {
                title = null;
                body = string.Join("\n", lines);
                return;
            }

            // 見出しの中の \* などの逃がしを戻す。タイトルは文字のまま送る。
            title = Regex.Replace(match.Groups[1].Value.Trim(), @"\\([!-/:-@\[-`{-~])", "$1");
            body = string.Join("\n", lines.Skip(i + 1)).TrimStart('\n');
        }

        /// <summary>今の画面の内容を、ふつうの .md として手元に書き出す。WordPress とは結び付けない（開いている記事はそのまま）。</summary>
        private void SaveWordPressLocally(MarkdownView view)
        {
            if (view == null || view.Owner == null) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md",
                DefaultExt = ".md",
                FileName = (string.IsNullOrWhiteSpace(view.Title) ? "記事" : SafeFileName(view.Title)) + ".md",
            };
            if (_filer != null && !string.IsNullOrEmpty(_filer.Folder) && Directory.Exists(_filer.Folder))
                dialog.InitialDirectory = _filer.Folder;

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, view.Owner.ComposeText(), new UTF8Encoding(false));
                Status("手元に保存しました（WordPress とは結び付きません）: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できませんでした。" + Environment.NewLine + ex.Message, "WordPress",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

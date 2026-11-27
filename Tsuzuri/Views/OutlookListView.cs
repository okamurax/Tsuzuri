using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tsuzuri.Office;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 開いている Outlook（classic）のメールのフォルダの一覧（2026-11-26。利用者の考え。Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする）。
    /// Excel の一覧と同じ所に、タブで切り替えて出す（OfficeTabs）。ストア（メールボックス・PST など）を見出しに、その下にメールのフォルダを字下げして並べる。
    /// フォルダの行の頭の開閉の矢印を押すと、その下にフォルダのメールを件名だけで並べる箱を開く（初めは閉じている。利用者の考え）。箱は高さに上限があって自分のスクロールバーを持ち、
    /// 一番下までスクロールすると次の分を読む（読むのは OutlookLink）。
    /// 選び方はエクスプローラーと同じ: 押すとそれだけを選び（ほかは外れる）、Ctrl を押しながらだと足す・外す、何もない所を押すと全部外す。ストアの行は押せない。
    /// 選んだフォルダ（パス）とメール（フォルダ・件名・日時・ID）が AI に渡る（OutlookLink）。Outlook の側は何も切り替えない。
    /// 選び直したときは色だけを塗り直す（作り直すと、開いた箱のスクロールの位置が戻るため）。
    /// </summary>
    public sealed class OutlookListView : DockPanel
    {
        /// <summary>メールの箱の高さの上限。</summary>
        private const double MailBoxHeight = 240;

        /// <summary>本文を開いている間の、箱の高さの上限。</summary>
        private const double MailBoxTallHeight = 440;

        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();

        /// <summary>選べる行（フォルダとメール）。キーはフォルダならパス、メールなら MailKey。色を塗り直すのに使う。</summary>
        private readonly List<KeyValuePair<string, Border>> _rows = new List<KeyValuePair<string, Border>>();

        /// <summary>開いているフォルダの箱（パス → 箱）。</summary>
        private readonly Dictionary<string, MailBox> _boxes = new Dictionary<string, MailBox>(StringComparer.OrdinalIgnoreCase);

        /// <summary>本文を開いているメールのキー（無ければ null）と、出している文・読んでいる途中か。一覧を作り直しても、その行の下に出し直す。</summary>
        private string _bodyKey;
        private string _bodyText;
        private bool _bodyLoading;
        private Border _bodyPanel;

        private Func<string, bool> _chosen = key => false;

        /// <summary>フォルダの行を押した: フォルダのパス（\\ストア\受信トレイ\…）、Ctrl を押していたか。</summary>
        public event Action<string, bool> RowClicked;

        /// <summary>メールの行を押した: メール、Ctrl を押していたか。</summary>
        public event Action<OutlookBridge.Mail, bool> MailClicked;

        /// <summary>開いた本文の欄を押した（閉じる）。</summary>
        public event Action BodyClicked;

        /// <summary>フォルダの開閉の矢印を押した（開く・閉じる）。</summary>
        public event Action<string> ToggleRequested;

        /// <summary>ストアの開閉の矢印を押した（フォルダを隠す・出す）。引数はストアの名前。</summary>
        public event Action<string> StoreToggleRequested;

        /// <summary>開いた箱を一番下までスクロールした（次の分を読む）。</summary>
        public event Action<string> MoreRequested;

        /// <summary>一覧の何もない所を押した（選んでいるものを全部外す）。</summary>
        public event Action EmptyClicked;

        /// <summary>「更新」を押した。</summary>
        public event Action RefreshRequested;

        /// <summary>一番下の「Outlook で AI にいつも伝えること」を押した（メモ帳で開く。OfficeNotes）。</summary>
        public event Action NotesRequested;

        public OutlookListView()
        {
            var title = new TextBlock
            {
                Text = "Outlook",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("SidebarText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var refresh = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Outlook のフォルダの一覧を読み直す（開いているフォルダのメールも）",
            };
            var style = Application.Current != null ? Application.Current.TryFindResource("ToolButton") as Style : null;
            if (style != null) refresh.Style = style;
            refresh.Click += delegate
            {
                var requested = RefreshRequested;
                if (requested != null) requested();
            };
            DockPanel.SetDock(refresh, Dock.Right);

            var head = new DockPanel { Margin = new Thickness(12, 0, 6, 0), Height = 28 };
            _head = head;
            _refresh = refresh;
            head.Children.Add(refresh);
            head.Children.Add(title);
            DockPanel.SetDock(head, Dock.Top);
            Children.Add(head);

            // 一番下に1行: Outlook を扱うときに AI へいつも伝える前提（ExcelListView と同じ形）
            var notes = new TextBlock
            {
                Text = "Outlook で AI にいつも伝えること",
                FontSize = 13,
                Foreground = Brush("AccentBrush"),
                Margin = new Thickness(12, 4, 8, 6),
                Cursor = Cursors.Hand,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "メールを探す・読む・下書きを作るときに AI へいつも伝える前提（言葉の意味・下書きの書き方の好み・宛名や結び・署名・社内の決まりなど）を、メモ帳で書く。\n次に開く会話から AI に渡る（今の会話には渡らない）",
            };
            notes.MouseEnter += delegate { notes.TextDecorations = TextDecorations.Underline; };
            notes.MouseLeave += delegate { notes.TextDecorations = null; };
            notes.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                var requested = NotesRequested;
                if (requested != null) requested();
            };
            DockPanel.SetDock(notes, Dock.Bottom);
            Children.Add(notes);

            _scroll.Content = _list;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // 何もない所でも押せるように地を塗り、フォーカスを取らせない（ExcelListView と同じ。取れると押されたことを自分で止めてしまう）
            _scroll.Background = Brushes.Transparent;
            _scroll.Focusable = false;
            _scroll.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount > 1) return;
                var empty = EmptyClicked;
                if (empty != null) empty();
            };
            Children.Add(_scroll);

            ShowNote("押すと読みます（「更新」）");
        }

        private DockPanel _head;
        private Button _refresh;

        /// <summary>
        /// 見出しの段（題と「更新」）を外し、「更新」のボタンを返す（OfficeTabs がタブの段に置く。2026-11-26。Excel と Outlook をタブで切り替えるため）。
        /// </summary>
        public Button DetachHead()
        {
            Children.Remove(_head);
            _head.Children.Remove(_refresh);
            DockPanel.SetDock(_refresh, Dock.Right);
            return _refresh;
        }

        /// <summary>メールの行のキー（フォルダのパスと ID）。</summary>
        public static string MailKey(string folder, string entryId)
        {
            return folder + "\u0001" + entryId;
        }

        /// <summary>
        /// 一覧を出し直す。null なら読めなかった（problem にわけ）。chosen はフォルダのパスかメールのキーを選んでいるか、
        /// expanded は開いているフォルダか、pageOf は開いたフォルダの読んだ分（無ければ null）。
        /// </summary>
        public void Show(List<OutlookBridge.Store> stores, Func<string, bool> chosen, Func<string, bool> expanded, Func<string, OutlookLink.MailPage> pageOf, Func<string, bool> storeClosed, string problem)
        {
            _chosen = chosen ?? (key => false);
            _list.Children.Clear();
            _rows.Clear();
            _boxes.Clear();
            _bodyPanel = null;   // 前の一覧に入っていた本文の欄は捨てる（開いたままなら、作り直す箱の中に出し直す）
            if (stores == null)
            {
                ShowNote(string.IsNullOrEmpty(problem) ? "Outlook を読めませんでした" : problem);
                return;
            }
            if (stores.Count == 0)
            {
                ShowNote("Outlook にメールのフォルダがありません");
                return;
            }

            foreach (var store in stores)
            {
                var closed = storeClosed != null && storeClosed(store.Name);
                _list.Children.Add(StoreRow(store, closed));
                if (closed) continue;   // 閉じたストアのフォルダは出さない
                if (!string.IsNullOrEmpty(store.Problem) && store.Folders.Count == 0)
                {
                    _list.Children.Add(Row(null, null, store.Problem, 34, false, false, store.Problem));
                    continue;
                }
                foreach (var folder in store.Folders)
                {
                    var open = expanded != null && expanded(folder.Path);
                    _list.Children.Add(FolderRow(folder, open));
                    if (!open) continue;
                    var box = new MailBox(this, folder.Path, 34 + 14 * folder.Depth + 18);
                    _boxes[folder.Path] = box;
                    _list.Children.Add(box.Root);
                    box.Fill(pageOf != null ? pageOf(folder.Path) : null);
                }
            }
        }

        /// <summary>開いたフォルダの箱を、読んだ分で出し直す（新しく読んだ行を足す。スクロールの位置はそのまま）。</summary>
        public void SetMails(string folder, OutlookLink.MailPage page)
        {
            MailBox box;
            if (_boxes.TryGetValue(folder, out box)) box.Fill(page);
        }

        /// <summary>
        /// メールの本文を、そのメールの行のすぐ下に開く（2026-11-27。利用者の考え）。key が null なら閉じる。loading なら「読んでいます」の形で出す。
        /// 本文は自分のスクロールバーを持たず、メールの箱のスクロールで読む（箱の中にもう一段スクロールする場所を作らないように）。
        /// </summary>
        public void ShowBody(string key, string text, bool loading)
        {
            _bodyKey = key;
            _bodyText = text;
            _bodyLoading = loading;
            if (_bodyPanel != null)
            {
                var parent = _bodyPanel.Parent as Panel;
                if (parent != null) parent.Children.Remove(_bodyPanel);
                _bodyPanel = null;
            }
            foreach (var box in _boxes.Values) box.Tall(false);
            if (key == null) return;
            foreach (var box in _boxes.Values)
                if (box.PlaceBody(key)) return;
        }

        /// <summary>本文の欄を作る。</summary>
        private Border BodyPanel(double indent)
        {
            var text = new TextBlock
            {
                Text = _bodyLoading ? _bodyText : Clean(_bodyText),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _bodyLoading ? Brush("TextMuted") : Brush("TextPrimary"),
            };
            var panel = new Border
            {
                Child = text,
                Padding = new Thickness(indent, 6, 10, 8),
                Background = Brush("HoverFill"),
                Cursor = Cursors.Hand,
                ToolTip = _bodyLoading ? "押すと閉じる" : "利用者が見るためだけの本文（AI には渡さない）。本文のどこを押しても閉じる（メールをもう一度押しても閉じる）",
            };
            // 本文のどこを押しても閉じる（2026-11-27。利用者の判断）。何もない所を押したこと（全部外す）にはしない
            panel.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                if (e.ClickCount > 1) return;
                var clicked = BodyClicked;
                if (clicked != null) clicked();
            };
            _bodyPanel = panel;
            return panel;
        }

        /// <summary>本文を見せる形に: 制御文字を空白にし、3つ以上続く空の行を1つにし、長ければ切る。</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "（本文は空）";
            var lines = new List<string>();
            var blank = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = new string(Array.ConvertAll(raw.ToCharArray(), c => char.IsControl(c) && c != '\t' ? ' ' : c)).TrimEnd();
                blank = line.Trim().Length == 0 ? blank + 1 : 0;
                if (blank <= 1) lines.Add(line);
            }
            var joined = string.Join("\n", lines).Trim('\n');
            if (joined.Length <= OutlookBridge.MaxShownBody) return joined;
            var cut = OutlookBridge.MaxShownBody;
            if (char.IsHighSurrogate(joined[cut - 1])) cut--;
            return joined.Substring(0, cut) + "\n\n…（長いので先頭の " + OutlookBridge.MaxShownBody + " 字だけ。続きは Outlook で）";
        }

        /// <summary>選んでいる行の色を塗り直す（一覧は作り直さない）。</summary>
        public void RefreshSelection(Func<string, bool> chosen)
        {
            _chosen = chosen ?? (key => false);
            foreach (var row in _rows) Paint(row.Value, _chosen(row.Key));
        }

        private void ShowNote(string message)
        {
            _list.Children.Clear();
            _rows.Clear();
            _boxes.Clear();
            _bodyPanel = null;   // 前の一覧に入っていた本文の欄は捨てる（開いたままなら、作り直す箱の中に出し直す）
            _list.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 4, 8, 4),
            });
        }

        /// <summary>ストアの見出しの行: 頭に開閉の矢印（開いていれば下向き）。行のどこを押しても開く・閉じる（ストアは選べない）。</summary>
        private UIElement StoreRow(OutlookBridge.Store store, bool closed)
        {
            var arrow = Arrow(!closed);
            var text = new TextBlock
            {
                Text = store.Name,
                FontSize = 14,
                Foreground = Brush("SidebarText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new DockPanel();
            DockPanel.SetDock(arrow, Dock.Left);
            content.Children.Add(arrow);
            content.Children.Add(text);
            var row = new Border
            {
                Child = content,
                Padding = new Thickness(6, 3, 8, 3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = store.Name + (string.IsNullOrEmpty(store.Problem) ? string.Empty : "\n" + store.Problem)
                          + (closed ? "\n押すと、このメールボックスのフォルダを出す" : "\n押すと、このメールボックスのフォルダを隠す（中で選んでいたフォルダ・メールは外れる）"),
            };
            row.MouseEnter += delegate { row.Background = Brush("HoverFill"); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;   // 何もない所を押したこと（全部外す）にしない
                if (e.ClickCount > 1) return;
                var toggle = StoreToggleRequested;
                if (toggle != null) toggle(store.Name);
            };
            return row;
        }

        /// <summary>フォルダの行: 頭に開閉の矢印、その右に名前（押すと選ぶ）。</summary>
        private UIElement FolderRow(OutlookBridge.Folder folder, bool open)
        {
            var row = Row(folder.Path, null, folder.Name, 34 + 14 * folder.Depth, false, true,
                          folder.Path + "\n押すとこのフォルダだけを選んで、パスを AI に渡す（「このフォルダ」になる。メールは渡さない）。Ctrl を押しながらだと足す・外す。何もない所を押すと全部外す");
            var arrow = Arrow(open);
            arrow.Cursor = Cursors.Hand;
            arrow.ToolTip = open ? "メールの一覧を閉じる" : "このフォルダのメールを件名で並べる（新しい順。スクロールすると続きを読む）";
            arrow.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;   // 行を押したこと（選ぶ）にしない
                if (e.ClickCount > 1) return;   // 2度押しで開いてすぐ閉じないように
                var toggle = ToggleRequested;
                if (toggle != null) toggle(folder.Path);
            };
            var content = (DockPanel)row.Child;
            DockPanel.SetDock(arrow, Dock.Left);
            content.Children.Insert(0, arrow);
            row.Padding = new Thickness(Math.Max(0, row.Padding.Left - 18), row.Padding.Top, row.Padding.Right, row.Padding.Bottom);
            return row;
        }

        /// <summary>
        /// 開閉の矢印（Tsuzuri のアウトラインの開閉 App.xaml の ExpandToggle と同じ形。2026-11-27。利用者の判断）: 右向きの「›」を線で描き、開いていれば 90 度回して下向きにする。
        /// AiLink にはその Style が無いので、ここで同じ形を描く。
        /// </summary>
        private static Border Arrow(bool open)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 5,3 L 10,8 L 5,13"),
                Stroke = Brush("TextMuted"),
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = open ? new RotateTransform(90) : null,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return new Border { Child = path, Width = 18, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
        }

        /// <summary>
        /// 行。key が null なら押せない（ストアの見出し・知らせ）。mail があればメールの行。selectable なら選んだ色を塗る（_rows に覚える）。
        /// </summary>
        private Border Row(string key, OutlookBridge.Mail mail, string label, double indent, bool isStore, bool selectable, string tip)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = isStore ? 14 : 13,
                Foreground = isStore || key != null ? Brush("SidebarText") : Brush("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new DockPanel { LastChildFill = true };
            content.Children.Add(text);
            var row = new Border
            {
                Child = content,
                Padding = new Thickness(indent, 3, 8, 3),
                Background = Brushes.Transparent,
                ToolTip = tip,
            };
            if (key == null)
            {
                // 押せない行（ストアの見出し・知らせ）を押しても、何もない所を押したことにしない（選びを外さない）
                row.MouseLeftButtonDown += (sender, e) => e.Handled = true;
                return row;
            }

            if (selectable)
            {
                _rows.Add(new KeyValuePair<string, Border>(key, row));
                Paint(row, _chosen(key));
            }
            row.Cursor = Cursors.Hand;
            row.MouseEnter += delegate { if (!_chosen(key)) row.Background = Brush("HoverFill"); };
            row.MouseLeave += delegate { Paint(row, _chosen(key)); };
            row.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                if (e.ClickCount > 1) return;
                var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (mail != null)
                {
                    var clicked = MailClicked;
                    if (clicked != null) clicked(mail, ctrl);
                }
                else
                {
                    var clicked = RowClicked;
                    if (clicked != null) clicked(key, ctrl);
                }
            };
            return row;
        }

        private void Paint(Border row, bool chosen)
        {
            row.Background = chosen ? Brush("SelectionFill") : Brushes.Transparent;
            var content = row.Child as DockPanel;
            if (content == null) return;
            foreach (var child in content.Children)
            {
                var text = child as TextBlock;
                if (text == null) continue;   // 開閉の矢印（Border）は塗り替えない
                text.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
                text.Foreground = chosen ? Brush("TextPrimary") : Brush("SidebarText");
            }
        }

        /// <summary>開いたフォルダのメールの箱（高さに上限があり、自分でスクロールする。一番下まで来たら次の分を頼む）。</summary>
        private sealed class MailBox
        {
            private readonly OutlookListView _owner;
            private readonly string _folder;
            private readonly double _indent;
            private readonly StackPanel _mails = new StackPanel();
            private readonly TextBlock _foot;
            private readonly ScrollViewer _scroll;
            private readonly HashSet<string> _shown = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, Border> _mailRows = new Dictionary<string, Border>(StringComparer.Ordinal);
            private bool _more;
            private string _first;
            private bool _retry;

            public Border Root { get; private set; }

            public MailBox(OutlookListView owner, string folder, double indent)
            {
                _owner = owner;
                _folder = folder;
                _indent = indent;
                _foot = new TextBlock { FontSize = 12, Foreground = Brush("TextMuted"), Margin = new Thickness(indent, 3, 8, 4), TextWrapping = TextWrapping.Wrap };
                var inner = new StackPanel();
                inner.Children.Add(_mails);
                inner.Children.Add(_foot);
                _scroll = new ScrollViewer
                {
                    Content = inner,
                    MaxHeight = MailBoxHeight,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                    Background = Brushes.Transparent,
                };
                // 失敗のあとのやり直しは、利用者がスクロールしたときだけ（高さが変わっただけ・見えただけでは、やり直さない。自動で回り続けないように）
                _scroll.ScrollChanged += (sender, e) => AskMoreIfAtEnd(e.VerticalChange != 0);
                // 隠れていた（Excel のタブを出していた）箱が見えたら、見直す
                _scroll.IsVisibleChanged += (sender, e) => AskMoreIfAtEnd(false);
                // 箱の端でさらにホイールを回したら、外の一覧を動かす（ScrollViewer は動けなくても回したことを止めてしまうため）
                _scroll.PreviewMouseWheel += (sender, e) =>
                {
                    var atTop = _scroll.VerticalOffset <= 0;
                    var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight;
                    if (e.Delta < 0 && atBottom) AskMoreIfAtEnd(true);   // 下端では ScrollChanged が来ないので、ここで失敗のあとのやり直しを頼む
                    if (!((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))) return;
                    e.Handled = true;
                    var outer = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = _scroll };
                    _owner._scroll.RaiseEvent(outer);
                };
                // 箱の中の何もない所を押しても、全部外したことにしない
                _scroll.MouseLeftButtonDown += (sender, e) => e.Handled = true;
                Root = new Border
                {
                    Child = _scroll,
                    BorderBrush = Brush("BorderBrushSoft"),
                    BorderThickness = new Thickness(0, 1, 0, 1),
                    Margin = new Thickness(0, 1, 0, 3),
                };
            }

            /// <summary>読んだ分で出し直す。まだ出していないメールだけを足す。</summary>
            public void Fill(OutlookLink.MailPage page)
            {
                if (page == null)
                {
                    _foot.Text = "読んでいます…";
                    _more = false;
                    return;
                }
                // 先頭が変わった（新しいメールが届いた・消えた）なら作り直す（足すだけだと新しい順でなくなるため）
                if (page.Mails.Count > 0 && _first != null && page.Mails[0].EntryId != _first)
                {
                    foreach (UIElement child in _mails.Children) _owner._rows.RemoveAll(row => ReferenceEquals(row.Value, child));
                    _mails.Children.Clear();
                    _shown.Clear();
                    _mailRows.Clear();
                }
                if (page.Mails.Count > 0) _first = page.Mails[0].EntryId;
                foreach (var mail in page.Mails)
                {
                    if (!_shown.Add(mail.EntryId)) continue;
                    var when = mail.Received == DateTime.MinValue ? string.Empty : mail.Received.ToString("M/d HH:mm", CultureInfo.InvariantCulture) + "  ";
                    var subject = mail.Subject.Length == 0 ? "（件名なし）" : mail.Subject;
                    var key = MailKey(_folder, mail.EntryId);
                    var row = _owner.Row(key, mail, when + subject, _indent, false, true, null);
                    row.ToolTip = MailTip(mail, subject);
                    _mails.Children.Add(row);
                    _mailRows[key] = row;
                    if (key == _owner._bodyKey && (_owner._bodyPanel == null || _owner._bodyPanel.Parent == null))
                    {
                        _mails.Children.Add(_owner.BodyPanel(_indent));
                        Tall(true);
                    }
                }
                var shown = _shown.Count;
                // 続きがあれば、一番下まで来たら読む。読めなかったときは、利用者が一番下までスクロールしたときだけやり直す
                _more = !page.Loading && page.Problem == null && !page.Exhausted && shown < page.Total && shown < OutlookBridge.MaxListedMails;
                _retry = !page.Loading && page.Problem != null;
                _foot.Text = page.Loading ? "読んでいます…"
                             : page.Problem != null ? page.Problem + "（下までスクロールすると、やり直します）"
                             : page.Total == 0 ? "メールはありません"
                             : shown < page.Total ? (shown >= OutlookBridge.MaxListedMails ? "ここまで（" + page.Total + " 通のうち新しい " + shown + " 通。古いものは AI に探させる）" : "全 " + page.Total + " 通のうち " + shown + " 通（下までスクロールすると続きを読む）")
                             : "全 " + page.Total + " 通";
                _owner.Dispatcher.BeginInvoke(new Action(() => AskMoreIfAtEnd(false)), System.Windows.Threading.DispatcherPriority.Background);
            }

            /// <summary>メールの行のポップアップ: 件名・日時と、押したときのこと。</summary>
            private static object MailTip(OutlookBridge.Mail mail, string subject)
            {
                var panel = new StackPanel { MaxWidth = 420 };
                panel.Children.Add(new TextBlock { Text = subject, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (mail.Received != DateTime.MinValue)
                    panel.Children.Add(new TextBlock { Text = mail.Received.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0) });
                panel.Children.Add(new TextBlock
                {
                    Text = "押すと、このメールだけを選んで件名・日時・ID を AI に渡し、すぐ下に本文を開く（本文は AI に渡さない。もう一度押すか、本文を押すと閉じる）。Ctrl を押しながらだと、選びに足す・外すだけ",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 6, 0, 0),
                });
                return panel;
            }

            /// <summary>key のメールがこの箱にあれば、その行のすぐ下に本文の欄を入れる（見える所までスクロールする）。</summary>
            public bool PlaceBody(string key)
            {
                Border row;
                if (!_mailRows.TryGetValue(key, out row)) return false;
                var index = _mails.Children.IndexOf(row);
                if (index < 0) return false;
                var panel = _owner.BodyPanel(_indent);
                _mails.Children.Insert(index + 1, panel);
                Tall(true);
                // 本文の頭が見える所まで（長い本文の終わりまで飛ばないように、頭の分だけ）
                panel.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (panel.IsVisible) panel.BringIntoView(new Rect(0, 0, panel.ActualWidth, Math.Min(panel.ActualHeight, 80)));
                }), System.Windows.Threading.DispatcherPriority.Background);
                return true;
            }

            /// <summary>本文を開いている間は箱を高くする（狭い箱の中で本文を読まなくて済むように）。</summary>
            public void Tall(bool tall)
            {
                _scroll.MaxHeight = tall ? MailBoxTallHeight : MailBoxHeight;
            }

            /// <summary>一番下まで来ていて、続きがあれば頼む（中身が箱より短くて、スクロールできないときも）。byUser は利用者がスクロールしたとき（失敗のあとのやり直しはそのときだけ）。</summary>
            private void AskMoreIfAtEnd(bool byUser)
            {
                if (!_more && !(byUser && _retry)) return;
                if (!_scroll.IsVisible || _scroll.ViewportHeight <= 0) return;   // 隠れている箱は並べ直されず、いつも「一番下」に見えるため
                if (_scroll.ScrollableHeight - _scroll.VerticalOffset > 8) return;
                _more = false;   // 次の分が届くまで、何度も頼まない
                _retry = false;
                var more = _owner.MoreRequested;
                if (more != null) more(_folder);
            }
        }

        private static Brush Brush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Gray;
        }
    }
}

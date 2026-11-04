using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    public sealed class LinkActivatedEventArgs : EventArgs
    {
        public readonly string Label;
        public readonly string Target;
        public readonly bool IsImage;

        public LinkActivatedEventArgs(LinkSpan link)
        {
            Label = link.Label;
            Target = link.Target;
            IsImage = link.IsImage;
        }
    }

    /// <summary>画面から消す行内の範囲。Replacement が空でなければ、その文字に置き換える。</summary>
    public sealed class HiddenRange
    {
        public readonly int Start;
        public readonly int Length;
        public readonly string Replacement;

        /// <summary>
        /// 画像の題（2026-10-27）。Replacement を、本文と同じ大きさの薄い字で描く（MarkerHideGenerator）。
        /// 画像を出している行で、リンクの文字列の代わりに名前を出す。
        /// </summary>
        public readonly bool Caption;

        public HiddenRange(int start, int length, string replacement, bool caption = false)
        {
            Start = start;
            Length = length;
            Replacement = replacement;
            Caption = caption;
        }
    }

    /// <summary>行の下に出す画像と、それを指すリンク。</summary>
    public sealed class LineImage
    {
        public readonly LinkSpan Link;
        public readonly BitmapSource Image;

        public LineImage(LinkSpan link, BitmapSource image)
        {
            Link = link;
            Image = image;
        }
    }

    public sealed class OutlineSnapshot
    {
        public readonly List<OutlineHeading> Entries;

        /// <summary>キャレットがいる見出しの Entries 内での位置。無ければ -1。</summary>
        public readonly int CurrentIndex;

        public OutlineSnapshot(List<OutlineHeading> entries, int currentIndex)
        {
            Entries = entries;
            CurrentIndex = currentIndex;
        }
    }

    /// <summary>
    /// Markdown の編集面（SPEC 8章）。
    ///
    /// 文書は AvalonEdit がプレーンテキストとして持ち、装飾は MarkdownColorizer が
    /// 表示のときだけ載せる。文書モデルを二重に持たないので、描画がどう間違っても
    /// ファイルの内容は壊れない。RichTextBox / FlowDocument を使わない理由がこれ。
    /// </summary>
    public partial class MarkdownView : TextEditor
    {
        private DocumentModel _model;
        private bool _modelValid;
        private PageDocument _owner;
        private string _pageId = PageDocument.RootId;

        private List<OutlineHeading> _outline;

        // 生のソースを見せている行。無ければ -1。
        private int _liveLine = -1;

        // キーボードで触っている間は、生のソースを見せる行がキャレットについて回る。
        // マウスでクリックしただけでは記号を出さない（左の隙間をクリックしたときだけ出す）。
        private bool _followCaret;
        private int _hiddenLine = -1;
        private int _imageLine = -1;
        private List<LineImage> _lineImages;
        private List<HiddenRange> _hiddenRanges;
        private readonly Border _frame;

        // 読み込んだ画像。描き直しのたびにファイルを読まないよう、更新時刻と組で覚える。
        private readonly Dictionary<string, CachedImage> _images =
            new Dictionary<string, CachedImage>(StringComparer.OrdinalIgnoreCase);

        // リンク先（本文に書いた文字）ごとの、最後にファイルを確かめた時刻と結果（2026-11-03）。
        // スクロールで画像の行が画面に入るたびに、ファイルがあるか・更新時刻を確かめていた。\\Mac\ の上では1回ごとに時間がかかり、スクロールがカクついた。
        // 確かめてから少しの間は確かめ直さない。描き直したとき（RefreshImages）は捨てて確かめ直す。
        private readonly Dictionary<string, CheckedImage> _imageChecks =
            new Dictionary<string, CheckedImage>(StringComparer.Ordinal);

        /// <summary>ファイルを確かめ直すまでの間。</summary>
        private static readonly TimeSpan ImageRecheck = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 画像を本文に出すときの、横の画素数の上限。これより大きい画像は縮めて覚える（2026-11-03）。
        /// 写真をそのままの大きさで覚えると、描くたびに高画質で縮め直すので重く、スクロールがカクついた。
        /// 本文の幅（拡大率 2 の画面でも 1200 前後）より大きければ、どのみち縮めて出すので、見た目は変わらない。
        /// </summary>
        private const int MaxImagePixelWidth = 2400;

        private readonly TextBox _searchBox;
        private readonly TextBlock _searchCount;
        private readonly DispatcherTimer _searchDelay = new DispatcherTimer();

        // 検索結果の一覧（SPEC 12章）。当たった行だけを並べる。
        private readonly Border _resultArea;
        private readonly StackPanel _resultList;
        private Border _selectedResult;

        // 当たりの位置。移動ボタンで飛ぶために、行ではなく文書内の位置で持つ。
        private readonly List<int> _matchOffsets = new List<int>();
        private readonly List<int> _matchLengths = new List<int>();

        private Regex _search;

        private int _savedCaret = -1;
        private double _savedScroll;

        public event EventHandler<LinkActivatedEventArgs> LinkActivated;

        /// <summary>
        /// 打った内容の行き先が無い（このページがファイルから消えていた）。ここで読み取り専用になる。
        /// 受け手は画面の内容を別名で残し、知らせる。黙って捨てると、打った分が消える。
        /// </summary>
        public event EventHandler PageLost;

        /// <summary>
        /// 検索欄の「画像」「リンク」を押した。画像の行の見せ方を切り替える（SPEC 9章）。
        /// アプリ全体で1つの状態なので、受け手が全部のページに配り、覚える。
        /// </summary>
        public event EventHandler ImageDisplayToggleRequested;

        /// <summary>
        /// 行番号の右クリックの「リンクをコピー」を押した（2026-11-02。SPEC 5.5）。値は押した行の行番号。
        /// 受け手がその行の見出し（無ければページ）へのリンクをクリップボードに入れる。
        /// </summary>
        public event EventHandler<int> LinkCopyRequested;

        /// <summary>
        /// 行番号を右ボタンでダブルクリックした（2026-11-03。SPEC 7章）。値は押した行の行番号。
        /// 受け手がその行の見出し（本文の行なら、その行が属する見出し）のブックマークを付け外しする。
        /// </summary>
        public event EventHandler<int> BookmarkToggleRequested;

        // 画像の行を、リンクの文字だけで見せるか（2026-10-15）。false なら画像だけ（リンクの文字列は隠す）。
        private bool _linkTextOnly;
        private readonly Button _imageDisplayButton;

        /// <summary>
        /// 上端の段の左端の、ページのタイトルを置く場所（2026-10-16。前は別の段の帯だった）。
        /// 中身（タイトル）は MainWindow が並べ直すたびに入れる。段の幅の4割まで。
        /// </summary>
        public readonly Border TitleSlot = new Border();

        /// <summary>
        /// 上端の段の右端に置く切り替えのボタン（「画像」）の幅。
        /// </summary>
        public const double SwitchButtonWidth = 72;

        /// <summary>上端の段の切り替えのボタン。</summary>
        public static Button CreateSwitchButton(string text, string tip)
        {
            var button = new Button();
            button.Content = text;
            button.ToolTip = tip;
            button.FontSize = 12;
            button.Width = SwitchButtonWidth;
            button.Height = 26;
            button.Padding = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Center;

            var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
            if (style != null) button.Style = style;

            return button;
        }

        /// <summary>切り替えのボタンの脇の、縦の区切り線。</summary>
        public static Border CreateSwitchGap(Thickness margin)
        {
            var gap = new Border();
            gap.Width = 1;
            gap.Height = 16;
            gap.VerticalAlignment = VerticalAlignment.Center;
            gap.Margin = margin;
            gap.Background = Theme.Divider;
            return gap;
        }

        public MarkdownView()
        {
            WordWrap = true;
            ShowLineNumbers = true;
            LineNumbersForeground = Theme.LineNumber;
            SetUpLeftMargins();

            // 選択とカーソルも暗い背景に合わせる。既定のままだと眩しい。
            TextArea.SelectionBrush = Theme.Selection;
            TextArea.SelectionBorder = null;
            TextArea.SelectionForeground = null;
            TextArea.Caret.CaretBrush = Theme.Text;
            BorderThickness = new Thickness(0);
            Background = Theme.Background;
            Foreground = Theme.Text;
            FontFamily = Theme.BodyFont;
            FontSize = Theme.BodySize;
            // 行番号の桁ぶん本文が右にずれるので、左の余白は詰めておく。
            Padding = new Thickness(16, 30, 40, 32);

            // 本文は Ideal で描く。ウィンドウ全体は Display（小さな UI 文字が
            // にじまない）だが、見出しのように大きさの違う字が混ざる本文では
            // Display が字形と字間を崩すため、ここだけ切り替える。
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            Options.EnableHyperlinks = false;        // Ctrl+クリックを要求する標準のリンクは使わない
            Options.EnableEmailHyperlinks = false;
            Options.AllowScrollBelowDocument = true;
            Options.CutCopyWholeLine = false;
            Options.ConvertTabsToSpaces = false;

            // 折り返した2行目以降を、1行目の字下げ（見出しの深さ）に揃える（SPEC 8.4）。
            Options.InheritWordWrapIndentation = true;

            // 記号を消すのは装飾より前。消えた範囲に色を塗っても意味がないため。
            TextArea.TextView.ElementGenerators.Add(new MarkerHideGenerator(this));

            // 見出しの深さを示す縦線。字下げの中に描く。
            TextArea.TextView.BackgroundRenderers.Add(new IndentGuideRenderer(this));

            // 画像は本文の幅に合わせて縮める。幅は組み版のあとで確定するので、
            // 変わったら組み直す。
            TextArea.TextView.SizeChanged += (sender, e) =>
            {
                if (e.WidthChanged) TextArea.TextView.Redraw(DispatcherPriority.Normal);
            };

            TextArea.TextView.BackgroundRenderers.Add(new RuleRenderer(this));
            TextArea.TextView.BackgroundRenderers.Add(new HeadingRuleRenderer(this));   // 見出し1・2の下の線（2026-11-03）
            TextArea.TextView.BackgroundRenderers.Add(new CodeBlockRenderer(this));   // コードブロックの地（2026-10-29）
            TextArea.TextView.LineTransformers.Add(new MarkdownColorizer(this));
            TextArea.TextView.LineTransformers.Add(new SearchColorizer(this));   // 後から上に塗る

            // コードブロックの「コピー」ボタン（2026-10-29）。文字とカーソルより上の層に重ねる。
            TextArea.TextView.InsertLayer(new CodeCopyLayer(this, TextArea.TextView), KnownLayer.Caret, LayerInsertionPosition.Above);

            // --- 検索欄（SPEC 12章）。ヘッダの下に常設する ---
            _searchBox = new TextBox();
            _searchBox.BorderThickness = new Thickness(0);
            _searchBox.Background = Brushes.Transparent;
            _searchBox.Foreground = Theme.Text;
            _searchBox.CaretBrush = Theme.Text;
            _searchBox.SelectionBrush = Theme.Selection;
            _searchBox.FontSize = 13;
            _searchBox.Padding = new Thickness(0);
            _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            _searchBox.ToolTip = "正規表現で検索（大文字小文字は区別しない）";
            _searchBox.TextChanged += OnSearchTextChanged;
            _searchBox.PreviewKeyDown += OnSearchKeyDown;

            _searchCount = new TextBlock();
            _searchCount.FontSize = 12;
            _searchCount.Foreground = Theme.Muted;
            _searchCount.VerticalAlignment = VerticalAlignment.Center;
            _searchCount.Margin = new Thickness(8, 0, 6, 0);

            var searchLabel = new TextBlock();
            searchLabel.Text = "検索";
            searchLabel.FontSize = 12;
            searchLabel.Foreground = Theme.Muted;
            searchLabel.VerticalAlignment = VerticalAlignment.Center;
            searchLabel.Margin = new Thickness(4, 0, 10, 0);
            DockPanel.SetDock(searchLabel, Dock.Left);

            // 矢印は Windows のアイコン用フォント（上 E74A／下 E74B）。文字の ↑ ↓ は小さく描くと潰れて見えた（2026-10-16）。
            var previous = CreateStepButton("\uE74A", "前の該当箇所へ");
            previous.Click += delegate { MoveToMatch(-1, false); };

            var next = CreateStepButton("\uE74B", "次の該当箇所へ");
            next.Click += delegate { MoveToMatch(1, false); };

            // DockPanel は先に足したものが右端に付く。並びは 件数 ↑ ↓ にしたい。
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(previous, Dock.Right);
            DockPanel.SetDock(_searchCount, Dock.Right);

            // 「ランチャー」「AI」はウィンドウ全体の機能なので、ツールバーの右端に移した（2026-10-31）。この段には、このページに効くものだけを置く。

            var imageGap = CreateSwitchGap(new Thickness(8, 0, 8, 0));
            DockPanel.SetDock(imageGap, Dock.Right);

            // 画像の行の見せ方の切り替え。段の右端に置く（SPEC 9章）。押すと、もう一方に替わる。
            _imageDisplayButton = CreateSwitchButton("画像", null);
            _imageDisplayButton.Click += delegate
            {
                var handler = ImageDisplayToggleRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            DockPanel.SetDock(_imageDisplayButton, Dock.Right);
            UpdateImageDisplayButton();

            // 段の左端はページのタイトル。検索とは縦の区切り線で分ける。
            DockPanel.SetDock(TitleSlot, Dock.Left);
            var titleGap = CreateSwitchGap(new Thickness(8, 0, 8, 0));
            DockPanel.SetDock(titleGap, Dock.Left);

            var searchRow = new DockPanel();
            searchRow.Margin = new Thickness(8, 0, 8, 0);
            searchRow.Children.Add(_imageDisplayButton);   // DockPanel は先に足したものが右端に付く
            searchRow.Children.Add(imageGap);
            searchRow.Children.Add(TitleSlot);     // 左端から タイトル｜検索
            searchRow.Children.Add(titleGap);
            searchRow.Children.Add(searchLabel);

            // タイトルが長くても検索欄を押し出さないよう、段の幅の4割までにする。
            searchRow.SizeChanged += delegate { TitleSlot.MaxWidth = Math.Max(80, searchRow.ActualWidth * 0.4); };
            searchRow.Children.Add(next);
            searchRow.Children.Add(previous);
            searchRow.Children.Add(_searchCount);
            searchRow.Children.Add(_searchBox);

            var searchArea = new Border();
            searchArea.Height = 32;
            searchArea.Background = Theme.Background;
            searchArea.BorderBrush = Theme.Divider;
            searchArea.BorderThickness = new Thickness(0, 0, 0, 1);
            searchArea.Child = searchRow;
            DockPanel.SetDock(searchArea, Dock.Top);

            // 検索結果の一覧。検索欄の下に広がり、本文はその分だけ下がる。
            // 本文の行は隠さない（SPEC 12章「本文は絞り込まない」）。
            _resultList = new StackPanel();

            var resultScroll = new ScrollViewer();
            resultScroll.Content = _resultList;
            resultScroll.MaxHeight = 220;
            resultScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            resultScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            _resultArea = new Border();
            _resultArea.Background = Theme.Background;
            _resultArea.BorderBrush = Theme.Divider;
            _resultArea.BorderThickness = new Thickness(0, 0, 0, 1);
            _resultArea.Padding = new Thickness(0, 3, 0, 3);
            _resultArea.Child = resultScroll;
            _resultArea.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_resultArea, Dock.Top);

            _searchDelay.Interval = TimeSpan.FromMilliseconds(150);
            _searchDelay.Tick += delegate
            {
                _searchDelay.Stop();
                ApplySearch();
            };

            var body = new DockPanel();
            body.LastChildFill = true;
            body.Children.Add(searchArea);
            body.Children.Add(_resultArea);
            body.Children.Add(this);

            // エディタ自身ではなくこの枠を画面に出し入れするので、
            // エディタの親は変わらない。検索欄も枠の中にあるので、
            // 画面を組み直しても入力内容が消えない。
            _frame = new Border();
            _frame.Child = body;

            Document.Changed += OnDocumentChanged;   // 打った場所を覚える（打った段落だけを解析し直すため。2026-11-04）
            TextChanged += OnTextChanged;

            // リンクは自前で判定する。座標からオフセットを求めて範囲に当てる。
            PreviewMouseLeftButtonDown += OnRevealMouseDown;           // リンクの判定より先に走らせる
            PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            PreviewMouseLeftButtonDown += OnParagraphDoubleClick;
            PreviewMouseRightButtonDown += OnGutterRightButtonDown;
            PreviewMouseRightButtonUp += OnGutterRightButtonUp;
            TextArea.TextView.PreviewMouseMove += OnTextViewMouseMove;
            TextArea.Caret.PositionChanged += OnCaretMoved;
            TextArea.Caret.PositionChanged += KeepCaretOutOfIndent;   // OnCaretMoved のあとに走らせる
            TextArea.PreviewKeyDown += OnIndentKeyDown;
            TextArea.SelectionChanged += OnSelectionChanged;

            TextArea.PreviewKeyDown += OnRevealKeyDown;
            TextArea.PreviewKeyDown += OnSiblingHeadingKeyDown;   // OnRevealKeyDown のあと（Enter で記号を出す処理を先に通す）
            TextArea.TextEntering += delegate { FollowCaret(); };
        }

        /// <summary>
        /// 左側の並びを整える。[行番号][余白][本文]。
        /// 見出しの深さは本文の字下げと、その中の縦線で見せる（SPEC 8.4）。
        /// AvalonEdit は既定で行番号のすぐ右に点線の区切りを置くが、
        /// 余白だけで十分に分かれるので外す。
        /// </summary>
        private void SetUpLeftMargins()
        {
            for (var i = TextArea.LeftMargins.Count - 1; i >= 0; i--)
            {
                var margin = TextArea.LeftMargins[i];

                if (margin is LineNumberMargin)
                {
                    ((FrameworkElement)margin).Margin = new Thickness(0, 0, 14, 0);
                    continue;
                }

                if (margin is System.Windows.Shapes.Line)
                    TextArea.LeftMargins.RemoveAt(i);
            }
        }

        // =====================================================================
        // 読み書き
        // =====================================================================

        /// <summary>画面に出し入れするのはこの枠。左端が未保存の印になる。</summary>
        public Border Frame
        {
            get { return _frame; }
        }

        /// <summary>このページが属するファイル。</summary>
        public PageDocument Owner
        {
            get { return _owner; }
        }

        /// <summary>ファイルの中でのページの識別子。空ならルート。</summary>
        public string PageId
        {
            get { return _pageId; }
        }

        public string FilePath
        {
            get { return _owner == null ? null : _owner.FilePath; }
        }

        /// <summary>
        /// 未保存の編集があるか。保存はファイル単位なので、判断も文書が持つ。
        /// </summary>
        public bool IsDirty
        {
            get { return _owner != null && _owner.IsDirty; }
        }

        /// <summary>本文先頭の見出し。無ければファイル名（SPEC 6章）。</summary>
        public string Title
        {
            get
            {
                foreach (var info in Model.Lines)
                {
                    if (info.Kind != LineKind.Heading) continue;

                    var text = HeadingText.Display(info.Content.Trim());
                    if (text.Length > 0) return text;
                }

                if (_pageId == PageDocument.RootId && FilePath != null)
                    return Path.GetFileNameWithoutExtension(FilePath);

                return "無題";
            }
        }

        /// <summary>文書の改行。差し込む文字列を作るときに合わせる。</summary>
        public string NewLine
        {
            get
            {
                foreach (var line in Document.Lines)
                {
                    if (line.DelimiterLength == 2) return "\r\n";
                    if (line.DelimiterLength == 1) return "\n";
                }

                return "\n";
            }
        }

        /// <summary>文書の中の1ページを表示する。</summary>
        public void Bind(PageDocument owner, string pageId)
        {
            _owner = owner;
            _pageId = pageId ?? PageDocument.RootId;

            IsReadOnly = false;
            _lineMarks = null;   // 別のページなので、前の行の目印とは比べない（全部を描き直す）
            Text = owner.TextOf(_pageId) ?? string.Empty;

            CaretOffset = 0;
            ScrollToHome();

            // キャレットは1行目にあるが、その行を生ソースにはしない。
            // 1行目がリンクだと、クリックしてもページが開くだけでキャレットを
            // 動かせず、記号の出たままの行から抜けられなくなる。
            _followCaret = false;
            _liveLine = -1;
            _hiddenLine = -1;
        }

        /// <summary>検索欄の文字。画面の状態として覚え、戻す（2026-10-25）。</summary>
        public string SearchText
        {
            get { return _searchBox.Text; }
            set { _searchBox.Text = value ?? string.Empty; }
        }

        /// <summary>
        /// カーソルの行と桁（1 始まり）、縦のスクロールの位置を戻す（2026-10-25。画面の状態を戻すとき）。
        /// 行や桁が今の本文に無ければ、収まるところまで寄せる。並べ終わってから呼ぶ（先だとスクロールが効かない）。
        /// </summary>
        public void RestorePosition(int line, int column, double scroll)
        {
            if (Document.LineCount == 0) return;

            var target = Document.GetLineByNumber(Math.Max(1, Math.Min(line, Document.LineCount)));
            CaretOffset = target.Offset + Math.Max(0, Math.Min(column - 1, target.Length));
            ScrollToVerticalOffset(Math.Max(0, scroll));
        }

        /// <summary>
        /// 文書を読み直したあと、このページの中身を入れ直す（2026-10-24）。カーソルの行とスクロールの位置は残す。
        /// 元に戻すの履歴は捨てる（Text を入れると消える）。前の中身の位置やページを指していて、戻すと本文が壊れるため。
        /// 中身が変わっていなくても、元に戻すの履歴は捨てる（2026-10-24）。履歴にはページを消す・戻す操作（ExternalChange）が
        /// 読み直す前のページを抱えたまま残っていて、戻すと、読み直したファイルの同じ番号の別のページとぶつかって中身が失われうる。
        /// </summary>
        public void ReloadFromOwner()
        {
            if (_owner == null) return;

            var text = _owner.TextOf(_pageId);

            // ページが無くなっていれば中身は入れ替えない（打てば行き先の無いページの扱いになる）。履歴だけ捨てる。
            if (text == null || text == Text)
            {
                Document.UndoStack.ClearAll();
                return;
            }

            var line = TextArea.Caret.Line;
            var scroll = VerticalOffset;

            _lineMarks = null;
            Text = text;

            CaretOffset = Document.GetLineByNumber(Math.Min(line, Document.LineCount)).Offset;
            ScrollToVerticalOffset(scroll);
        }

        /// <summary>
        /// 解析結果。本文が変わるまで使い回す。
        /// 記法の解釈は Markdig に任せ、ここは行ごとに並べ替えた結果を持つだけ。
        /// </summary>
        private DocumentModel Model
        {
            get
            {
                if (_modelValid && _model != null) return _model;

                var document = Document;
                var texts = new List<string>(document.LineCount);
                var offsets = new List<int>(document.LineCount);

                foreach (var line in document.Lines)
                {
                    texts.Add(document.GetText(line.Offset, line.Length));
                    offsets.Add(line.Offset);
                }

                _model = MarkdownParser.Parse(document.Text, texts, offsets);
                _modelValid = true;
                _outline = null;

                return _model;
            }
        }

        /// <summary>行番号は 1 始まり。</summary>
        public LineInfo GetLineInfo(int lineNumber)
        {
            return Model.LineAt(lineNumber);
        }

        /// <summary>その行の装飾範囲とリンク。</summary>
        public InlineScan GetInline(int lineNumber)
        {
            return Model.InlineAt(lineNumber);
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            // 保存はファイル単位なので、打つたびに文書へ書き戻しておく。
            // こうしておけば、このページを閉じたあとでも編集が残る。
            // 文書にこのページがもう無ければ、打った内容はどこにも保存されない。
            // 黙って受け付け続けると消えるので、それ以上打てないようにする。
            if (_owner != null && !IsReadOnly && !_owner.SetText(_pageId, Text))
            {
                IsReadOnly = true;

                var lost = PageLost;
                if (lost != null) lost(this, EventArgs.Empty);
            }

            // 打った段落だけを解析し直せれば、文書全体は解析し直さない（2026-11-04）。
            int patchedFrom, patchedTo;
            var patched = TryPatchModel(out patchedFrom, out patchedTo);
            _editCount = 0;
            _editSpansLines = false;

            if (!patched) _modelValid = false;
            _outline = null;
            _hiddenLine = -1;
            _imageLine = -1;

            // 本文が変われば当たりの位置も変わる。数え直しは打ち終わってからでよいが、
            // 古い位置を残しておくと ↑↓ が別の場所を選んでしまい、
            // 利用者が当たりだと思って打ち替えて本文を壊す。先に捨てる。
            if (_search != null)
            {
                _matchOffsets.Clear();
                _matchLengths.Clear();
                _searchCount.Text = "…";

                _searchDelay.Stop();
                _searchDelay.Start();
            }

            if (patched)
            {
                // 行の種類は変わっていないので、表が増えたり減ったりはしない。表の中を打ったときだけ、読めるかが変わりうるので畳み直す。
                var redrawAll = RangeHasTable(patchedFrom, patchedTo) && UpdateTableFolds();
                RedrawPatchedLines(patchedFrom, patchedTo, redrawAll);
                return;
            }

            // 表の畳み方も、描き直す前に揃え直す（表が増えた・減った・行が変わった）。
            var foldsChanged = UpdateTableFolds();

            // 打った行のほかに、見た目が変わった行だけを描き直す（2026-10-18。前は打つたびに表示中の全行を描き直していた）。
            // コードフェンスの開閉のように後ろの行の意味がまとめて変わるときは、その行がみな変わったと分かるので、それも描き直される。
            RedrawChangedLines(foldsChanged);
        }

        // =====================================================================
        // 打った段落だけを解析し直す（2026-11-04）
        //
        // 前は1文字打つたびに文書全体を Markdig で解析し直していた。2万行で 40ms（Mac）、Windows の仮想マシンではその数倍かかり、
        // 日本語の変換を確定してから画面に出るまでに目に見える遅れがあった。
        // 1行の中だけを書き換えたときは、その行を含む「空行で区切られたかたまり」だけを解析し直し、結果を差し替える。
        // 差し替えてよいのは、かたまりの行の種類（見出し・箇条書き・表…）とレベルが前と全部同じときだけ。
        // 種類が変わる（# を打って見出しにした、``` を打った等）と、後ろの行の意味まで変わりうるので、今までどおり全体を解析し直す。
        // 空行をまたいで効く書き方（HTML のかたまり・リンクや脚注の定義）がある文書では使わない（DocumentModel.Fragile）。
        // =====================================================================

        /// <summary>前の TextChanged から後の、文書の書き換えの数。</summary>
        private int _editCount;

        /// <summary>最後の書き換えの場所（文書の位置）。</summary>
        private int _editOffset;

        /// <summary>書き換えが行をまたいだ（改行を足した・消した）か、大きかったか。</summary>
        private bool _editSpansLines;

        /// <summary>最後の書き換えで消した文字と足した文字（行をまたがない小さな書き換えのときだけ。書き換える前の行を組み立てるため）。</summary>
        private string _editRemoved = string.Empty;
        private string _editInserted = string.Empty;

        /// <summary>解析し直すかたまりの行数の上限。これより長いかたまり（空行の無い長い表など）は全体を解析し直す。</summary>
        private const int PatchLineLimit = 400;

        private void OnDocumentChanged(object sender, DocumentChangeEventArgs e)
        {
            _editCount++;
            _editOffset = e.Offset;

            if (e.RemovalLength > 1000 || e.InsertionLength > 1000
                || (e.RemovalLength > 0 && e.RemovedText.IndexOfAny(LineBreaks, 0, e.RemovalLength) >= 0)
                || (e.InsertionLength > 0 && e.InsertedText.IndexOfAny(LineBreaks, 0, e.InsertionLength) >= 0))
            {
                _editSpansLines = true;
                return;
            }

            _editRemoved = e.RemovedText.Text;
            _editInserted = e.InsertedText.Text;
        }

        private static readonly char[] LineBreaks = { '\r', '\n' };

        /// <summary>
        /// 打った行を含むかたまりだけを解析し直して、今の解析結果に差し替える。できたら true と、差し替えた行（1 始まり）。
        /// できなければ何も変えずに false（全体を解析し直す）。
        /// </summary>
        private bool TryPatchModel(out int from, out int to)
        {
            from = to = -1;

            if (_editCount != 1 || _editSpansLines) return false;
            if (!_modelValid || _model == null || _model.Fragile) return false;

            var document = Document;
            var count = document.LineCount;
            if (_model.Lines.Length != count) return false;

            var edited = document.GetLineByOffset(Math.Min(_editOffset, document.TextLength));
            var line = edited.LineNumber;

            // 空行（空白とタブだけ）だった行・空行になった行は、かたまりの区切りが変わるので使わない。
            // 全角の空白（U+3000）だけの行は Markdown では空行ではなく、空行に打つと前後の段落がつながる（2026-11-04）。
            var editedText = document.GetText(edited.Offset, edited.Length);
            if (IsBlank(editedText) || IsBlank(TextBeforeEdit(editedText, _editOffset - edited.Offset))) return false;

            from = line;
            while (from > 1 && !IsBlankText(document, from - 1))
            {
                from--;
                if (line - from > PatchLineLimit) return false;
            }

            to = line;
            while (to < count && !IsBlankText(document, to + 1))
            {
                to++;
                if (to - line > PatchLineLimit) return false;
            }

            var texts = new List<string>(to - from + 1);
            var offsets = new List<int>(to - from + 1);
            var text = new StringBuilder();

            for (var number = from; number <= to; number++)
            {
                var current = document.GetLineByNumber(number);
                var source = document.GetText(current.Offset, current.Length);

                offsets.Add(text.Length);
                texts.Add(source);
                text.Append(source);
                if (number < to) text.Append('\n');
            }

            // かたまりの1行目が字下げで始まるなら、前のリストの項目の続きかもしれない。項目の中での解釈は、取り出すと変わるので使わない（2026-11-04 のレビュー）。
            // 打った行が1行目なら、書き換える前の行も見る（字下げを消してリストの外に出した、など）。
            if (StartsIndented(texts[0])) return false;
            if (line == from && StartsIndented(TextBeforeEdit(texts[0], _editOffset - edited.Offset))) return false;

            var patch = MarkdownParser.Parse(text.ToString(), texts, offsets);
            if (patch.Fragile || patch.Lines.Length != texts.Count) return false;

            for (var i = 0; i < patch.Lines.Length; i++)
            {
                var before = _model.Lines[from - 1 + i];
                var after = patch.Lines[i];

                if (before.Kind != after.Kind || before.Level != after.Level || before.ContinuesHeading != after.ContinuesHeading) return false;
            }

            // 打った行の行頭（字下げ・- や 1. の印・その後の空白）が変わったら使わない。リストの印の幅（1. と 10.）や字下げが変わると、
            // 空行の向こうの行（項目の続き・字下げしたコード）の解釈まで変わり、取り出した段落を比べても分からない（2026-11-04 のレビュー）。
            if (!string.Equals(_model.Lines[line - 1].Marker, patch.Lines[line - from].Marker, StringComparison.Ordinal)) return false;

            for (var i = 0; i < patch.Lines.Length; i++)
            {
                // 段差は前の見出しで決まる。かたまりの外の見出しは変わっていないので、前の値を引き継ぐ。
                patch.Lines[i].Depth = _model.Lines[from - 1 + i].Depth;
                _model.Lines[from - 1 + i] = patch.Lines[i];
                _model.Inlines[from - 1 + i] = patch.Inlines[i];
            }

            return true;
        }

        /// <summary>
        /// Markdown の空行か（空白とタブだけの行）。全角の空白（U+3000）だけの行は空行ではなく段落の続きなので、区切りにしない（2026-11-04 のレビュー）。
        /// </summary>
        private static bool IsBlankText(TextDocument document, int lineNumber)
        {
            var line = document.GetLineByNumber(lineNumber);
            return IsBlank(document.GetText(line.Offset, line.Length));
        }

        private static bool IsBlank(string text)
        {
            foreach (var c in text)
            {
                if (c != ' ' && c != '\t') return false;
            }

            return true;
        }

        private static bool StartsIndented(string text)
        {
            return text.Length > 0 && (text[0] == ' ' || text[0] == '\t');
        }

        /// <summary>書き換える前の行。今の行の column から、足した文字を消した文字に戻す。</summary>
        private string TextBeforeEdit(string current, int column)
        {
            if (column < 0 || column + _editInserted.Length > current.Length) return " ";   // 組み立てられなければ、字下げありとして扱う（使わない側）
            return current.Substring(0, column) + _editRemoved + current.Substring(column + _editInserted.Length);
        }

        private bool RangeHasTable(int from, int to)
        {
            for (var line = from; line <= to; line++)
            {
                if (_model.Lines[line - 1].Kind == LineKind.Table) return true;
            }

            return false;
        }

        /// <summary>差し替えた行のうち、見た目が変わった行だけを描き直す（RedrawChangedLines の、差し替えた行だけを見る版）。</summary>
        private void RedrawPatchedLines(int from, int to, bool redrawAll)
        {
            if (redrawAll || _lineMarks == null || _lineMarks.Length != _model.Lines.Length)
            {
                RedrawChangedLines(redrawAll);
                return;
            }

            var first = -1;
            var last = -1;

            for (var line = from; line <= to; line++)
            {
                var mark = LineMark(_model.Lines[line - 1], _model.Inlines[line - 1]);
                if (mark == _lineMarks[line - 1]) continue;

                _lineMarks[line - 1] = mark;
                if (first < 0) first = line;
                last = line;
            }

            if (first > 0) RedrawLines(first, last);
        }

        // =====================================================================
        // 描き直す行を絞る（2026-10-18）
        //
        // 行ごとに「見た目を決めるもの」（種類・深さ・記号・装飾の範囲・リンク・行の文字）から目印の数を作って覚えておき、
        // 本文が変わったら作り直して比べる。違う行だけを描き直す。行が増えた・減ったときは、前後の同じ並びを除いた間だけ。
        // 違う行が多いとき・表の畳み方が変わったときは、前と同じく全部を描き直す。
        // =====================================================================

        private int[] _lineMarks;

        /// <summary>まとめて全部を描き直す境目。これより多くの行が変わったら、1行ずつ頼まずに全部を描き直す。</summary>
        private const int RedrawAllThreshold = 200;

        private void RedrawChangedLines(bool redrawAll)
        {
            var model = Model;
            var marks = new int[model.Lines.Length];
            for (var i = 0; i < marks.Length; i++) marks[i] = LineMark(model.Lines[i], model.Inlines[i]);

            var previous = _lineMarks;
            _lineMarks = marks;

            if (redrawAll || previous == null)
            {
                TextArea.TextView.Redraw();
                return;
            }

            // 前後の同じ並びを除く。行を足した・消したときも、その後ろの行はずれて比べられる。
            var head = 0;
            while (head < marks.Length && head < previous.Length && marks[head] == previous[head]) head++;

            var tail = 0;
            while (tail < marks.Length - head && tail < previous.Length - head
                   && marks[marks.Length - 1 - tail] == previous[previous.Length - 1 - tail]) tail++;

            var from = head + 1;                    // 1 始まりの行番号
            var to = marks.Length - tail;
            if (to < from) return;                  // 見た目の変わった行は無い（打った行は AvalonEdit が描き直す）

            if (to - from + 1 > RedrawAllThreshold)
            {
                TextArea.TextView.Redraw();
                return;
            }

            RedrawLines(from, to);
        }

        /// <summary>行の見た目を決めるものから作る目印の数。同じなら描き直さなくてよい。</summary>
        private static int LineMark(LineInfo info, InlineScan scan)
        {
            unchecked
            {
                var mark = (int)info.Kind;
                mark = mark * 31 + info.Level;
                mark = mark * 31 + info.Depth;
                mark = mark * 31 + (info.IsChecked ? 1 : 0);
                mark = mark * 31 + (info.ContinuesHeading ? 1 : 0);
                mark = mark * 31 + info.Marker.Length;
                mark = mark * 31 + info.Source.GetHashCode();

                if (scan != null)
                {
                    foreach (var span in scan.Spans)
                    {
                        mark = mark * 31 + span.Start;
                        mark = mark * 31 + span.Length;
                        mark = mark * 31 + (int)span.Kind;
                    }

                    foreach (var link in scan.Links)
                    {
                        mark = mark * 31 + link.Start;
                        mark = mark * 31 + link.Length;
                        mark = mark * 31 + (link.IsImage ? 1 : 0);
                        mark = mark * 31 + (link.Target ?? string.Empty).GetHashCode();
                    }
                }

                return mark;
            }
        }

        /// <summary>from 行から to 行まで（1 始まり）を描き直す。見えていない行は、見えるときに作られるので手間はかからない。</summary>
        private void RedrawLines(int from, int to)
        {
            if (Document == null) return;

            from = Math.Max(1, from);
            to = Math.Min(Document.LineCount, to);
            if (to < from) return;

            var start = Document.GetLineByNumber(from);
            var end = Document.GetLineByNumber(to);
            TextArea.TextView.Redraw(start.Offset, end.EndOffset - start.Offset, DispatcherPriority.Normal);
        }

        // =====================================================================
        // 記号を消す（SPEC 8章）
        // =====================================================================

        /// <summary>
        /// 生のソースを見せる行を決める（SPEC 8.1）。
        ///
        /// クリックしただけでは記号を出さない。ちょっと触っただけで行の見た目が
        /// 変わると煩わしいため。出すのは次のときだけ。
        /// - 行の左の隙間（行番号の右から本文の書き出しまで）をクリックした
        /// - キーボードで動かすか、文字を打った（以後はキャレットの行について回る）
        /// 消えた記号の前後はキャレットの位置を見分けられないので、打つときは必ず出す。
        /// </summary>
        private void OnCaretMoved(object sender, EventArgs e)
        {
            var line = TextArea.Caret.Line;

            if (_followCaret) SetLiveLine(line);
            else if (line != _liveLine) SetLiveLine(-1);

            // 表に入った・表から出たら、表を生の Markdown と表の部品とで描き替える（SPEC 8章）。
            int first, last;
            var table = TryGetTableRange(line, out first, out last) ? first : -1;
            if (table != _rawTable)
            {
                _rawTable = table;
                ScheduleTableRefresh();
            }
        }

        // 表の畳み直しを後に回しているか。
        private bool _tableRefreshPending;

        /// <summary>
        /// 表の畳み直しと描き直しを、今の処理が終わってから1回だけ行う。
        /// カーソル・選択の知らせは、AvalonEdit が画面の行を組み立てている最中にも出る。その場で畳み直すと、組み立てている行を壊して落ちる。
        /// 文書が変わったときは組み立ての最中ではないので、その場で畳み直す（OnTextChanged）。
        /// </summary>
        private void ScheduleTableRefresh()
        {
            if (_tableRefreshPending) return;
            _tableRefreshPending = true;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                _tableRefreshPending = false;

                // 畳み方が変わったとき（表に入った・出た、選択が表にかかった・外れた）だけ描き直す（2026-10-18）。
                // 前はクリックで選択が変わるたびに、表が無くても全行を描き直していた。
                if (UpdateTableFolds()) TextArea.TextView.Redraw();
            }), DispatcherPriority.Normal);
        }

        // =====================================================================
        // 表（SPEC 8章。2026-10-16）
        //
        // カーソルが表の外にあるときは、表の行をまとめて罫線付きの表として描く（TableElement）。
        // 表の中に入ったら、表全体を生の Markdown に戻す。
        // =====================================================================

        // 生の Markdown で見せている表の1行目（カーソルのある表）。無ければ -1。
        private int _rawTable = -1;

        /// <summary>表として描くために畳んでいる表（2行目から最後の行まで）。</summary>
        private sealed class TableFold
        {
            public int First;
            public int Last;
            public CollapsedLineSection Section;
        }

        private readonly List<TableFold> _tableFolds = new List<TableFold>();

        /// <summary>
        /// 表として描く表の、2行目から最後の行までを畳む（描く前に。描く最中に行を飛ばすと AvalonEdit が止める）。
        /// 生で見せる表（カーソルが中にある・選択がかかっている）と、読めない表は畳まない。
        /// 文書が変わったとき・カーソルが表に出入りしたとき・選択が変わったときに呼ぶ。
        /// </summary>
        /// <returns>畳み方が変わったか（変わったら、描き直しが要る）。</returns>
        private bool UpdateTableFolds()
        {
            // 畳むべき表（1行目と最後の行）。
            var wanted = new List<KeyValuePair<int, int>>();

            if (Document != null)
            {
                var count = Document.LineCount;
                for (var line = 1; line <= count; line++)
                {
                    int first, last;
                    if (!TryGetTableRange(line, out first, out last)) continue;

                    line = last;   // 次はこの表の後ろから
                    if (last <= first || IsTableRaw(first, last)) continue;

                    var start = Document.GetLineByNumber(first);
                    var end = Document.GetLineByNumber(last);
                    if (!TableElement.CanBuild(Document.GetText(start.Offset, end.EndOffset - start.Offset))) continue;

                    wanted.Add(new KeyValuePair<int, int>(first, last));
                }
            }

            // 今の畳み方のうち、そのままでよいものは残す（2026-10-18）。前は打つたびに全部を開いて畳み直していて、
            // そのたびに AvalonEdit が行の高さを測り直していた。行が増減してずれた畳みは、今の行番号で比べる。
            var changed = false;
            var kept = new List<TableFold>();

            foreach (var fold in _tableFolds)
            {
                var section = fold.Section;
                var index = -1;

                if (section.IsCollapsed && section.Start != null && section.End != null
                    && !section.Start.IsDeleted && !section.End.IsDeleted)
                {
                    var first = section.Start.LineNumber - 1;
                    var last = section.End.LineNumber;
                    index = wanted.FindIndex(range => range.Key == first && range.Value == last);
                }

                if (index < 0)
                {
                    section.Uncollapse();
                    changed = true;
                    continue;
                }

                fold.First = wanted[index].Key;
                fold.Last = wanted[index].Value;
                kept.Add(fold);
                wanted.RemoveAt(index);
            }

            foreach (var range in wanted)
            {
                var start = Document.GetLineByNumber(range.Key);
                var end = Document.GetLineByNumber(range.Value);

                kept.Add(new TableFold
                {
                    First = range.Key,
                    Last = range.Value,
                    Section = TextArea.TextView.CollapseLines(start.NextLine, end),
                });
                changed = true;
            }

            _tableFolds.Clear();
            _tableFolds.AddRange(kept);
            return changed;
        }

        /// <summary>その表を畳んで、表として描いているか（MarkerHideGenerator が使う）。</summary>
        public bool IsTableFolded(int first, int last)
        {
            return _tableFolds.Exists(fold => fold.First == first && fold.Last == last);
        }

        /// <summary>その行を含む表の、最初と最後の行。表の行でなければ false。</summary>
        public bool TryGetTableRange(int lineNumber, out int first, out int last)
        {
            first = last = -1;
            if (lineNumber < 1 || lineNumber > Document.LineCount) return false;

            var info = GetLineInfo(lineNumber);
            if (info == null || info.Kind != LineKind.Table) return false;

            first = lineNumber;
            while (first > 1)
            {
                var above = GetLineInfo(first - 1);
                if (above == null || above.Kind != LineKind.Table) break;
                first--;
            }

            last = lineNumber;
            while (last < Document.LineCount)
            {
                var below = GetLineInfo(last + 1);
                if (below == null || below.Kind != LineKind.Table) break;
                last++;
            }

            return true;
        }

        /// <summary>その表を生の Markdown で見せるか。カーソルが表の中にあるか、選択が表にかかっているとき。</summary>
        public bool IsTableRaw(int first, int last)
        {
            var caret = TextArea.Caret.Line;
            if (caret >= first && caret <= last) return true;

            for (var line = first; line <= last; line++)
            {
                if (IsLive(line)) return true;
            }

            return false;
        }

        // =====================================================================
        // 表エディタで編集する（SPEC 8.4.2。2026-10-16）
        //
        // 表を押すと、表エディタ（TableEditor。自前の WPF の表）を開く。表エディタはエディタの上に開くウィンドウで、
        // 閉じるまでエディタは操作できない（2026-10-18 に SPREAD for Windows Forms から替えた）。
        // 表は文字列で受け渡す（TableModel.ToExchange）。「保存して閉じる」と、戻ってきた中身を Markdown の表にして、元の表と置き換える
        // （1回の書き換え。Ctrl+Z 1回で戻る）。生の Markdown のまま直すときは、表の行の行番号を押す（OnRevealMouseDown）。
        // =====================================================================

        /// <summary>表エディタを開く。その行から始まる表を読めなければ何もしない。</summary>
        public void EditTable(int firstLine)
        {
            int first, last;
            if (!TryGetTableRange(firstLine, out first, out last) || first != firstLine) return;

            var start = Document.GetLineByNumber(first);
            var end = Document.GetLineByNumber(last);
            var original = Document.GetText(start.Offset, end.EndOffset - start.Offset);

            var model = TableModel.Parse(original.Replace("\r\n", "\n").Split('\n'));
            if (model == null) return;

            var window = Window.GetWindow(this);

            // 見出し行より多いセルがある行は、表エディタに渡すとはみ出した分が消える。開く前に知らせて選ばせる（2026-10-27）。
            if (model.ExtraCellRows > 0)
            {
                var answer = MessageBox.Show(window,
                    "この表には、見出し行より多いセルがある行が " + model.ExtraCellRows + " 行あります（はみ出したセルは表として表示されていません）。" + Environment.NewLine +
                    "表エディタで開いて保存すると、はみ出したセルは消えます。" + Environment.NewLine + Environment.NewLine +
                    "残したいときは「いいえ」を押し、行番号を押して生の Markdown で見出し行に列を足してください。" + Environment.NewLine + Environment.NewLine +
                    "このまま表エディタで開きますか？",
                    "表エディタ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            string edited;
            try
            {
                edited = TableEditor.TableEditorHost.Edit(window, model.ToExchange());
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, "表エディタでエラーが起きました。表は変えていません。" + Environment.NewLine + Environment.NewLine + ex,
                    "表エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (edited == null || !model.ReplaceWith(edited)) return;   // 保存せずに閉じた

            // 閉じるまでエディタは操作できないが、自動保存などで表が変わっていないか確かめてから置き換える（入口の確かめ）。
            int nowFirst = -1, nowLast = -1;
            if (TryGetTableRange(first, out nowFirst, out nowLast) && nowFirst == first)
            {
                start = Document.GetLineByNumber(nowFirst);
                end = Document.GetLineByNumber(nowLast);
                if (Document.GetText(start.Offset, end.EndOffset - start.Offset) == original)
                {
                    Document.Replace(start.Offset, end.EndOffset - start.Offset, model.Serialize(NewLine));
                    return;
                }
            }

            var markdown = model.Serialize(NewLine);
            try { Clipboard.SetText(markdown); } catch (Exception) { }
            MessageBox.Show(window, "表エディタを開いている間に、表が変わっていたため、反映しませんでした。" + Environment.NewLine +
                "編集した表はクリップボードに入れたので、貼り付けて使えます。", "表エディタ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>押した所が表の部品の上か。本文側のマウスの処理（リンクを開く・段落を選ぶ・記号を出す）を、表の上では動かさない。</summary>
        private static bool IsInTable(object source)
        {
            for (var node = source as DependencyObject; node != null;
                 node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                var element = node as FrameworkElement;
                if (element != null && Equals(element.Tag, TableElement.FrameTag)) return true;
            }

            return false;
        }

        private void SetLiveLine(int lineNumber)
        {
            if (lineNumber == _liveLine) return;

            var previous = _liveLine;
            _liveLine = lineNumber;
            _hiddenLine = -1;

            RedrawLine(previous);
            RedrawLine(lineNumber);

            // 表の行に入った・出たら、表の畳み方も見直す（畳み方が変わったときは全部を描き直す）。
            int first, last;
            if (TryGetTableRange(previous, out first, out last) || TryGetTableRange(lineNumber, out first, out last))
                ScheduleTableRefresh();
        }

        /// <summary>
        /// 記号を隠している行で Enter を押したとき、見えている文字と隠れている記号の間で改行しないよう、カーソルを寄せる（2026-11-02）。
        /// ・カーソルより後ろが隠れた記号だけ（見出しの閉じの `#`、行末のリンクの `](…)` など）なら行末へ。見た目どおり、行の後ろに新しい行ができる
        /// ・カーソルより前が隠れた記号だけ（見出しの `## `、行頭のリンクの `[` など）なら行頭へ。見た目どおり、行の前に空の行ができる
        /// 前は記号の手前で切れ、`## [予定` と `](#p3)` に分かれるなど、見出しやリンクが壊れていた。
        /// 置き換えて見せている記号（箇条書きの •、画像の題）は見えている文字なので、寄せる対象にしない。選択があるとき・記号を出している行では何もしない。
        /// </summary>
        private void StepOverHiddenEdge()
        {
            if (!TextArea.Selection.IsEmpty) return;

            var number = TextArea.Caret.Line;
            var ranges = GetHiddenRanges(number);   // 記号を出している行は null
            if (ranges == null || ranges.Count == 0) return;

            var line = Document.GetLineByNumber(number);
            var column = CaretOffset - line.Offset;

            Func<int, bool> hidden = index => ranges.Any(range =>
                string.IsNullOrEmpty(range.Replacement) && index >= range.Start && index < range.Start + range.Length);

            if (column < line.Length && Enumerable.Range(column, line.Length - column).All(hidden))
            {
                CaretOffset = line.EndOffset;
                return;
            }

            if (column > 0 && Enumerable.Range(0, column).All(hidden))
            {
                _allowBeforeIndent = true;   // 行頭に置いたカーソルを、字下げの後ろへ戻させない
                CaretOffset = line.Offset;
            }
        }

        /// <summary>キーボードで触り始めた。キャレットの行を生ソースにし、以後ついて回る。</summary>
        private void FollowCaret()
        {
            _followCaret = true;
            SetLiveLine(TextArea.Caret.Line);
        }

        /// <summary>
        /// 動かすキーと書き換えるキーだけを拾う。Ctrl+S などで記号が出ないように。
        /// 普通の文字は TextEntering で拾う。
        /// </summary>
        private void OnRevealKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Back:
                case Key.Enter:
                    // 記号を出す前に、隠れた記号の手前・後ろで改行しないようにカーソルを寄せる（記号を出すと隠れていたかが分からなくなる）。
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) StepOverHiddenEdge();
                    FollowCaret();
                    return;

                case Key.Delete:
                case Key.Tab:
                case Key.ImeProcessed:      // 日本語入力の変換中
                    FollowCaret();
                    return;

                case Key.V:
                case Key.X:
                case Key.Z:
                case Key.Y:
                    if (Keyboard.Modifiers == ModifierKeys.Control) FollowCaret();
                    return;
            }
        }

        /// <summary>
        /// クリックでは記号を出さない。ただし左の隙間なら、その行を生ソースにする。
        /// 生ソースにした行の本文をクリックしても、その行は生ソースのまま（続けて直せるように）。
        /// </summary>
        private void OnRevealMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInTable(e.OriginalSource)) return;   // 表の上は表の部品が受け持つ（押すと生の Markdown に戻す。SPEC 8.4.1）

            _followCaret = false;

            var view = TextArea.TextView;
            var point = e.GetPosition(view);
            var gapLine = GapLineAt(point);

            // 本文を押したら、行番号の欄の続けて押した回数は数え直す。
            if (gapLine < 0 || point.X >= 0) _gutterClickCount = 0;

            if (gapLine < 0)
            {
                // 本文のクリック。キャレットが別の行へ移れば OnCaretMoved が記号を消す。
                return;
            }

            SetLiveLine(gapLine);

            if (point.X >= 0) return;

            // 本文より左（行番号とその右の余白）は、キャレットをその行の頭に置くだけ。
            // 続けて押した回数で変える（2026-10-27）: 1回は行頭、2回は記号を除いた部分を選ぶ（SelectLineContent）、
            // 3回は行末にキャレット（記号が隠れていると矢印キーで行末まで動かしにくいため）、4回以上は記号も含めて行の文字を全部選ぶ（改行は含めない）。
            // 行番号の上で AvalonEdit が行を選ぶのは止める。選ぶと記号を出す行が
            // 選択の範囲に広がり、どの行を出したいのかが分からなくなる。
            e.Handled = true;
            TextArea.ClearSelection();

            var line = Document.GetLineByNumber(gapLine);
            var clicks = CountGutterClicks(gapLine, e.Timestamp);
            if (clicks >= 4)
            {
                TextArea.Selection = Selection.Create(TextArea, line.Offset, line.EndOffset);
                CaretOffset = line.EndOffset;
            }
            else if (clicks == 3)
            {
                PutCaretAtLineEnd(line);
            }
            else if (clicks == 2)
            {
                SelectLineContent(line);
            }
            else
            {
                CaretOffset = line.Offset;
            }
            TextArea.Focus();
        }

        /// <summary>
        /// 行番号（とその右の余白）の右クリックで、その行の見出しへのリンクをコピーする（2026-11-02。SPEC 5.5。2026-11-03 にメニューをやめ、すぐコピーする）。
        /// 右ボタンのダブルクリックなら、さらにその行の見出しのブックマークを付け外しする（2026-11-03。SPEC 7章）。1回目のクリックでコピーは済んでいる。
        /// 押したときに止めておく（AvalonEdit にキャレットを動かさせない）。離したときにも止める（AvalonEdit の右クリックのメニューを出させない）。
        /// </summary>
        private void OnGutterRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(TextArea.TextView);
            if (point.X >= 0) return;

            var lineNumber = GapLineAt(point);
            if (lineNumber < 1) return;

            e.Handled = true;

            if (e.ClickCount < 2)
            {
                var copy = LinkCopyRequested;
                if (copy != null) copy(this, lineNumber);
                return;
            }

            var toggle = BookmarkToggleRequested;
            if (toggle != null) toggle(this, lineNumber);
        }

        private void OnGutterRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(TextArea.TextView);
            if (point.X >= 0) return;
            if (GapLineAt(point) < 1) return;

            e.Handled = true;
        }

        /// <summary>その行が属する見出し（その行か、それより上で一番近い見出し）。見出しより前の行なら null。</summary>
        public OutlineHeading HeadingAbove(int lineNumber)
        {
            OutlineHeading found = null;

            foreach (var heading in GetOutline().Entries)
            {
                if (heading.Line > lineNumber) break;
                found = heading;
            }

            return found;
        }

        /// <summary>行番号の欄で続けて押したとみなす間隔（ミリ秒）。Windows のダブルクリックの間隔（初めは 500）より長め。</summary>
        private const int GutterClickInterval = 800;

        private int _gutterClickLine;
        private int _gutterClickTime;
        private int _gutterClickCount;

        /// <summary>
        /// 行番号の欄で続けて押した回数（2026-10-27）。e.ClickCount は使わない。
        /// Windows の数え方は前のクリックから 0.5 秒以内・ほぼ同じ位置でないと数えないので、4回まで届きにくかった。
        /// ここでは前のクリックから GutterClickInterval 以内に同じ行を押せば続きとみなす（マウスが少しずれても同じ行なら数える）。
        /// </summary>
        private int CountGutterClicks(int lineNumber, int timestamp)
        {
            var continued = _gutterClickCount > 0 && lineNumber == _gutterClickLine
                && unchecked(timestamp - _gutterClickTime) <= GutterClickInterval;

            _gutterClickCount = continued ? _gutterClickCount + 1 : 1;
            _gutterClickLine = lineNumber;
            _gutterClickTime = timestamp;
            return _gutterClickCount;
        }

        /// <summary>
        /// 行の末尾にキャレットを置く。画像のある行では、画像の手前（リンクの文字列の後ろ）に置く。
        /// 画像は文書上の長さが 0 で行末にあるので、行末の位置だけ指定すると画像の後ろに置かれてしまう。
        /// </summary>
        private void PutCaretAtLineEnd(DocumentLine line)
        {
            var visualLine = TextArea.TextView.GetOrConstructVisualLine(line);
            var image = visualLine.Elements.OfType<InlineObjectElement>()
                .FirstOrDefault(element => element.DocumentLength == 0);

            if (image == null)
            {
                CaretOffset = line.EndOffset;
                return;
            }

            // 画像の手前の表示上の列。画像は折り返しで次の段に送られているので、前の段の末尾として置く。
            var position = new TextViewPosition(line.LineNumber, line.Length + 1, image.VisualColumn);
            position.IsAtEndOfLine = true;
            TextArea.Caret.Position = position;
        }

        /// <summary>見出しの行頭の印（## ）。</summary>
        private static readonly Regex HeadingOpener = new Regex(@"^ {0,3}#{1,6}(?:[ \t]+|$)", RegexOptions.Compiled);

        /// <summary>見出しの行末の閉じの印（ ##）。</summary>
        private static readonly Regex HeadingCloser = new Regex(@"[ \t]+#+[ \t]*$", RegexOptions.Compiled);

        /// <summary>リンク・画像1つだけ。名前の部分を取る。</summary>
        private static readonly Regex WholeLink = new Regex(@"^[ \t]*!?\[([^\]]+)\]\([^)]*\)[ \t]*$", RegexOptions.Compiled);

        /// <summary>
        /// 行番号のダブルクリックで、その行を選ぶ（2026-10-25）。キャレットは選んだ範囲の後ろに置く。
        /// ・見出しの行は、行頭の ## と行末の閉じの # を除いた見出しの文字だけ
        /// ・行がリンク（画像）1つだけなら、その名前の部分だけ（[カーテン](#p3) の「カーテン」。見出し・箇条書きの印の後ろでも）
        /// ・それ以外は行全体（改行は含めない）
        /// </summary>
        private void SelectLineContent(DocumentLine line)
        {
            var text = Document.GetText(line.Offset, line.Length);
            var info = GetLineInfo(line.LineNumber);

            var start = 0;
            var end = text.Length;

            var heading = info.Kind == LineKind.Heading && !info.ContinuesHeading;
            var opener = heading ? HeadingOpener.Match(text) : Match.Empty;
            var marker = opener.Success ? opener.Length
                       : !heading && !string.IsNullOrEmpty(info.Marker) && text.StartsWith(info.Marker, StringComparison.Ordinal) ? info.Marker.Length
                       : 0;

            var contentEnd = text.Length;
            if (heading)
            {
                var closer = HeadingCloser.Match(text, marker);
                if (closer.Success) contentEnd = closer.Index;
            }
            while (contentEnd > marker && char.IsWhiteSpace(text[contentEnd - 1])) contentEnd--;

            var link = WholeLink.Match(text.Substring(marker, contentEnd - marker));
            if (link.Success)
            {
                start = marker + link.Groups[1].Index;
                end = start + link.Groups[1].Length;
            }
            else if (heading && contentEnd > marker)
            {
                start = marker;
                end = contentEnd;
            }

            TextArea.Selection = Selection.Create(TextArea, line.Offset + start, line.Offset + end);
            CaretOffset = line.Offset + end;
        }

        /// <summary>
        /// その座標が行の左の隙間（本文の書き出しより左）なら、その行番号。違えば -1。
        /// 座標は TextView 基準。行番号や余白の上は X が負になる。
        /// </summary>
        private int GapLineAt(Point point)
        {
            var view = TextArea.TextView;
            if (point.Y < 0 || point.Y > view.ActualHeight) return -1;

            view.EnsureVisualLines();
            var visualLine = view.GetVisualLineFromVisualTop(point.Y + view.VerticalOffset);
            if (visualLine == null) return -1;

            if (point.X >= 0)
            {
                // 字下げ（見出しの深さぶんの幅や、行頭の空白）の後ろが本文の書き出し。
                var column = 0;
                foreach (var element in visualLine.Elements)
                {
                    var end = element.VisualColumn + element.VisualLength;
                    while (column < end && element.IsWhitespace(column)) column++;
                    if (column < end) break;
                }

                var textStart = visualLine.GetVisualPosition(column, VisualYPosition.LineTop).X - view.HorizontalOffset;
                if (point.X >= textStart) return -1;
            }

            return visualLine.FirstDocumentLine.LineNumber;
        }

        // 選択がかかっていて、生ソースで見せている行の範囲（1 始まり）。無ければ -1。
        private int _selectionFrom = -1;
        private int _selectionTo = -1;

        /// <summary>
        /// 選択が変わると、生ソースを見せる行の範囲も変わる。範囲に入った行・外れた行だけを描き直す（2026-10-18。前は全行を描き直していた）。
        /// </summary>
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            _hiddenLine = -1;

            int from = -1, to = -1;
            var selection = TextArea.Selection;
            if (selection != null && !selection.IsEmpty && selection.Length > 0)
            {
                from = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line);
                to = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line);
            }

            if (from != _selectionFrom || to != _selectionTo)
            {
                if (from < 0 || _selectionFrom < 0)
                {
                    RedrawLines(_selectionFrom, _selectionTo);
                    RedrawLines(from, to);
                }
                else
                {
                    // 両端の動いた分。間の行は、選択の中のままなので変わらない。
                    RedrawLines(Math.Min(from, _selectionFrom), Math.Max(from, _selectionFrom));
                    RedrawLines(Math.Min(to, _selectionTo), Math.Max(to, _selectionTo));
                }

                _selectionFrom = from;
                _selectionTo = to;
            }

            ScheduleTableRefresh();   // 選択が表にかかったら、その表は生で見せる（畳み方が変わったときだけ描き直す）
        }

        private void RedrawLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Document.LineCount) return;

            TextArea.TextView.Redraw(Document.GetLineByNumber(lineNumber), DispatcherPriority.Normal);
        }

        /// <summary>
        /// その行で画面から消す範囲。カーソルのある行は消さない（生のソースを見せる）。
        /// 消すものが無ければ null。
        ///
        /// 生成器が1行につき何度も呼ぶので、直前の1行ぶんだけ覚えておく。
        /// </summary>
        public IList<HiddenRange> GetHiddenRanges(int lineNumber)
        {
            // 消した記号は幅がゼロなので、選択の端がそれを含むかどうかを
            // 画面で見分けられない。選択が触れている行は生のソースを見せる。
            if (IsLive(lineNumber)) return null;

            if (_hiddenLine == lineNumber) return _hiddenRanges;

            _hiddenLine = lineNumber;
            _hiddenRanges = BuildHiddenRanges(lineNumber);

            return _hiddenRanges;
        }

        private bool IsLive(int lineNumber)
        {
            if (lineNumber == _liveLine) return true;

            // 長さ0の選択は選択とみなさない。AvalonEdit は表示上の列が違うだけで
            // （消えた記号や字下げの前後）、文字を1つも含まない「空でない」選択を作ることがある。
            var selection = TextArea.Selection;
            if (selection == null || selection.IsEmpty || selection.Length == 0) return false;

            var from = selection.StartPosition.Line;
            var to = selection.EndPosition.Line;
            if (from > to) { var swap = from; from = to; to = swap; }

            return lineNumber >= from && lineNumber <= to;
        }

        private List<HiddenRange> BuildHiddenRanges(int lineNumber)
        {
            var info = GetLineInfo(lineNumber);
            if (info == null) return null;

            if (lineNumber < 1 || lineNumber > Document.LineCount) return null;
            var lineLength = Document.GetLineByNumber(lineNumber).Length;

            switch (info.Kind)
            {
                case LineKind.Blank:
                case LineKind.Code:
                case LineKind.Fence:
                case LineKind.Table:
                case LineKind.Setext:
                    return null;          // 中身をそのまま見せる行

                case LineKind.Rule:
                    // 線は RuleRenderer が描くので、記号そのものは丸ごと消す。
                    if (lineLength == 0) return null;

                    var rule = new List<HiddenRange>(1);
                    rule.Add(new HiddenRange(0, lineLength, string.Empty));
                    return rule;
            }

            var ranges = new List<HiddenRange>();

            // 行頭の記号。リストは記号を落とす代わりに印を描く。
            if (info.Marker.Length > 0)
            {
                switch (info.Kind)
                {
                    case LineKind.Bullet:
                        ranges.Add(new HiddenRange(0, info.Marker.Length, Indent(info.Level) + "• "));
                        break;

                    case LineKind.Task:
                        ranges.Add(new HiddenRange(0, info.Marker.Length,
                            Indent(info.Level) + (info.IsChecked ? "☑ " : "☐ ")));
                        break;

                    case LineKind.Ordered:
                        break;            // 番号は中身なので残す

                    default:
                        ranges.Add(new HiddenRange(0, info.Marker.Length, string.Empty));
                        break;
                }
            }

            // インラインの記号と、リンクの遷移先。
            var scan = GetInline(lineNumber);
            if (scan == null) return ranges.Count > 0 ? ranges : null;

            foreach (var span in scan.Spans)
            {
                if (span.Kind != SpanKind.Marker && span.Kind != SpanKind.LinkDest) continue;
                ranges.Add(new HiddenRange(span.Start, span.Length, string.Empty));
            }

            // 画像だけを見せるときは、画像を出しているリンクの文字列の代わりに、名前を薄い字で出す（画像の題。SPEC 9章）。
            // 2026-10-27 まではリンクの文字列を丸ごと隠していた。そうすると画像が行の1段目になり、行番号が画像の下端に付き、
            // クリックしてリンクの文字列が出ると、その段が増えて行番号も画像も動いた。題の段をいつも置けば、クリックしても動かない。
            // 読めずに画像を出していないリンクは隠さない（何も見えなくなるため）。
            var images = GetLineImages(lineNumber);
            if (images != null)
            {
                foreach (var image in images)
                    ranges.Add(new HiddenRange(image.Link.Start, image.Link.Length, ImageCaption(image.Link), true));
            }

            if (ranges.Count == 0) return null;

            // 生成器は「重なりが無く、昇順で、行の中に収まっている」ことを前提にする。
            // 解析側にどんな計算ミスがあっても、ここではみ出す範囲を捨てておけば
            // 表示が崩れるだけで済み、例外にも本文の消失にもならない。
            // 始まりが同じなら長いほうを先に置く（リンクの文字列を丸ごと隠すとき、その中の記号より先に）。
            ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));

            var ordered = new List<HiddenRange>(ranges.Count);
            var reach = 0;

            foreach (var range in ranges)
            {
                if (range.Start < reach) continue;
                if (range.Start < 0 || range.Start >= lineLength) continue;

                var length = Math.Min(range.Length, lineLength - range.Start);
                if (length <= 0) continue;

                ordered.Add(length == range.Length
                    ? range
                    : new HiddenRange(range.Start, length, range.Replacement, range.Caption));

                reach = range.Start + length;
            }

            return ordered.Count > 0 ? ordered : null;
        }

        /// <summary>画像の題。リンクの名前（![名前](…)）、無ければ画像のファイル名。</summary>
        private static string ImageCaption(LinkSpan link)
        {
            if (!string.IsNullOrWhiteSpace(link.Label)) return link.Label.Trim();

            var target = Uri.UnescapeDataString(link.Target ?? string.Empty);
            var name = target.Replace('/', '\\');
            var slash = name.LastIndexOf('\\');
            name = slash >= 0 ? name.Substring(slash + 1) : name;
            return name.Length > 0 ? name : "画像";
        }

        private static string Indent(int level)
        {
            return level > 0 ? new string(' ', level * 2) : string.Empty;
        }

        // =====================================================================
        // 画像のインライン表示（SPEC 9章）
        // =====================================================================

        /// <summary>
        /// その行の画像リンクが指す画像。無ければ null。
        ///
        /// リンクの行は記号を消す・消さないを切り替えるが、画像はカーソルの位置に
        /// かかわらず常に出す。切り替えると、カーソルが通るたびに画像が出たり
        /// 消えたりして行の高さが変わり、画面がちらつく。
        ///
        /// 生成器が1行につき何度も呼ぶので、直前の1行ぶんだけ覚えておく。
        /// </summary>
        public IList<LineImage> GetLineImages(int lineNumber)
        {
            if (_imageLine == lineNumber) return _lineImages;

            _imageLine = lineNumber;
            _lineImages = BuildLineImages(lineNumber);

            return _lineImages;
        }

        private List<LineImage> BuildLineImages(int lineNumber)
        {
            if (_linkTextOnly) return null;   // リンクの文字だけを見せる（SPEC 9章）

            var info = GetLineInfo(lineNumber);
            if (info == null || info.Kind == LineKind.Code || info.Kind == LineKind.Fence) return null;

            var scan = GetInline(lineNumber);
            if (scan == null) return null;

            List<LineImage> images = null;
            foreach (var link in scan.Links)
            {
                if (!link.IsImage) continue;

                // 読めなければリンクだけ見せれば足りる。
                var image = LoadImage(link.Target);
                if (image == null) continue;

                if (images == null) images = new List<LineImage>();
                images.Add(new LineImage(link, image));
            }

            return images;
        }

        /// <summary>
        /// 画像の行の見せ方。true ならリンクの文字だけ、false なら画像だけ（リンクの文字列は隠す）。
        /// どちらでも、カーソルのある行（生ソースの行）ではリンクの文字列を出す。
        /// </summary>
        /// <summary>画像を読み直して描き直す（図エディタで図を描き直したあと。SPEC 8.4.3）。読んだ画像は更新時刻で見分けるので、変わった物だけ読み直す。</summary>
        public void RefreshImages()
        {
            _imageChecks.Clear();
            _imageLine = -1;
            _hiddenLine = -1;
            TextArea.TextView.Redraw();
        }

        public void SetLinkTextOnly(bool linkTextOnly)
        {
            if (_linkTextOnly == linkTextOnly) return;

            _linkTextOnly = linkTextOnly;
            _hiddenLine = -1;
            _imageLine = -1;
            UpdateImageDisplayButton();
            TextArea.TextView.Redraw();
        }

        private void UpdateImageDisplayButton()
        {
            _imageDisplayButton.Content = _linkTextOnly ? "リンク" : "画像";
            _imageDisplayButton.ToolTip = _linkTextOnly
                ? "画像の行を、リンクの文字だけで見せている。押すと画像だけにする"
                : "画像の行を、画像だけで見せている（カーソルのある行はリンクの文字も出す）。押すとリンクの文字だけにする";
        }

        private sealed class CachedImage
        {
            public DateTime Stamp;
            public BitmapSource Image;
        }

        private sealed class CheckedImage
        {
            public DateTime Checked;
            public BitmapSource Image;
        }

        /// <summary>
        /// リンク先の画像を読む。無い・読めない・対応していない形式なら null。
        ///
        /// OnLoad で読み切ってから Freeze するので、ファイルを掴んだままにしない。
        /// 掴んでいると、画像を編集ソフトで上書きしたり消したりできなくなる。
        /// </summary>
        private BitmapSource LoadImage(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (target.IndexOf("://", StringComparison.Ordinal) >= 0) return null;   // 外部の画像は取りに行かない

            CheckedImage check;
            var now = DateTime.UtcNow;
            if (_imageChecks.TryGetValue(target, out check) && now - check.Checked < ImageRecheck) return check.Image;

            var image = ReadImage(target);
            _imageChecks[target] = new CheckedImage { Checked = now, Image = image };
            return image;
        }

        private BitmapSource ReadImage(string target)
        {
            var path = ResolvePath(target);
            if (path == null || !File.Exists(path)) return null;
            if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)) return null;

            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            CachedImage cached;
            if (_images.TryGetValue(path, out cached) && cached.Stamp == stamp) return cached.Image;

            BitmapSource image = null;
            try
            {
                // 写真の向きの印（EXIF）に従って向きを直す（2026-10-18。前は印を見ず、スマホの写真などが回って見えた）。
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        var orientation = ImageOrientation.Read(frame);
                        frame.Freeze();
                        image = Shrink(ImageOrientation.Apply(frame, orientation));
                    }
                }
            }
            catch (Exception)
            {
                // 壊れた画像や未対応の形式。リンクとして見せれば足りる。
            }

            // 読めなかったことも覚えておく。描き直しのたびに読み直すと重い。
            _images[path] = new CachedImage { Stamp = stamp, Image = image };
            return image;
        }

        /// <summary>横が MaxImagePixelWidth を超える画像を、縦横比を保って縮める。縮めた画素は一度だけ作って覚える（CachedBitmap）。</summary>
        private static BitmapSource Shrink(BitmapSource image)
        {
            if (image == null || image.PixelWidth <= MaxImagePixelWidth) return image;

            var scale = (double)MaxImagePixelWidth / image.PixelWidth;
            var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var cached = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            return cached;
        }

        // =====================================================================
        // リンク先の解決（SPEC 5章）
        // =====================================================================

        /// <summary>リンク先をこのファイルからの相対パスとして解決する。解決できなければ null。</summary>
        public string ResolvePath(string target)
        {
            if (string.IsNullOrEmpty(FilePath)) return null;

            var cleaned = target;

            var anchor = cleaned.IndexOf('#');
            if (anchor >= 0) cleaned = cleaned.Substring(0, anchor);
            if (cleaned.Length == 0) return null;

            // 他のツールが書いた %E8%A8%AD… 形式を解くために復号するが、
            // 実際のファイル名に % が入っていると別のパスになってしまう。
            // 復号した側で見つからなければ、そのままの綴りでも探す。
            string decoded = null;
            try
            {
                decoded = Attachments.Combine(FilePath, Uri.UnescapeDataString(cleaned));
            }
            catch (ArgumentException)
            {
            }

            if (decoded != null && File.Exists(decoded)) return decoded;

            var raw = Attachments.Combine(FilePath, cleaned);
            if (raw != null && File.Exists(raw)) return raw;

            return decoded ?? raw;
        }

        // =====================================================================
        // 検索（SPEC 12章）
        // =====================================================================

        public Regex SearchRegex
        {
            get { return _search; }
        }

        /// <summary>検索欄の右に置く、小さな移動ボタン。</summary>
        private static Button CreateStepButton(string glyph, string hint)
        {
            var button = new Button();
            button.Content = glyph;
            button.ToolTip = hint;
            button.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");   // ファイラーのアイコンと同じ
            button.FontSize = 11;
            button.Padding = new Thickness(7, 0, 7, 0);
            button.Margin = new Thickness(1, 3, 1, 3);
            button.MinWidth = 22;

            var style = Application.Current != null
                ? Application.Current.Resources["ToolButton"] as Style
                : null;

            if (style != null) button.Style = style;

            return button;
        }

        /// <summary>
        /// 検索欄での Enter は次の当たりへ、Shift+Enter は前の当たりへ。
        /// 入力欄そのものの振る舞いなので、アプリ全体のショートカットとは別（SPEC 11章）。
        /// 続けて押せるよう、飛んだあともカーソルは検索欄に残す。
        /// </summary>
        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;

            MoveToMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1, true);
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        }

        private void ApplySearch()
        {
            var pattern = _searchBox.Text;

            if (string.IsNullOrEmpty(pattern))
            {
                _search = null;
                _searchBox.Foreground = Theme.Text;
            }
            else
            {
                try
                {
                    // 時間制限が無いと (a+)+b のような入力で画面が固まって戻らない。
                    _search = new Regex(pattern, RegexOptions.IgnoreCase,
                        TimeSpan.FromMilliseconds(100));
                    _searchBox.Foreground = Theme.Text;
                }
                catch (ArgumentException)
                {
                    // 打ちかけの正規表現。文字色で示すだけで当たりは出さない。
                    _search = null;
                    _searchBox.Foreground = Theme.InvalidInput;
                }
            }

            try
            {
                RecomputeMatches();
            }
            catch (RegexMatchTimeoutException)
            {
                // 破滅的なバックトラックを起こす式。当たりを出さずに打ち直してもらう。
                _matchOffsets.Clear();
                _matchLengths.Clear();
                _search = null;
                _searchBox.Foreground = Theme.InvalidInput;
            }

            _searchCount.Text = _search == null
                ? string.Empty
                : (_matchOffsets.Count == 0 ? "なし" : _matchOffsets.Count + " 件");

            RebuildResults();
            TextArea.TextView.Redraw();
        }

        // ---------------------------------------------------------------
        // 検索結果の一覧
        //
        // 打つたびに（150ms でまとめて）当たった行だけを並べ直す。クリックでその行の
        // 最初の当たりを選んで飛ぶ。一覧は検索欄が空になるまで出したままにするので、
        // 次々にクリックして見て回れる。
        // ---------------------------------------------------------------

        /// <summary>並べる行の上限。これを超えたら残りの行数だけ出す。</summary>
        private const int MaxResultRows = 300;

        private void RebuildResults()
        {
            _resultList.Children.Clear();
            _selectedResult = null;

            if (_search == null || _matchOffsets.Count == 0)
            {
                _resultArea.Visibility = Visibility.Collapsed;
                return;
            }

            var shown = 0;
            var total = 0;
            var i = 0;

            // 当たりは文書の順に並んでいるので、同じ行のものをまとめて1行にする。
            while (i < _matchOffsets.Count)
            {
                var line = Document.GetLineByOffset(_matchOffsets[i]);
                var first = i;

                while (i < _matchOffsets.Count && _matchOffsets[i] <= line.EndOffset) i++;

                total++;
                if (shown >= MaxResultRows) continue;

                _resultList.Children.Add(CreateResultRow(line, first, i));
                shown++;
            }

            if (total > shown)
            {
                var more = new TextBlock();
                more.Text = "ほか " + (total - shown) + " 行。検索語を足して絞り込んでください。";
                more.Foreground = Theme.Muted;
                more.FontSize = 12;
                more.Margin = new Thickness(14, 2, 8, 2);
                _resultList.Children.Add(more);
            }

            _resultArea.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 一覧の1行。[行番号] [行の文字（当たりに地色）]。
        /// 先頭の空白は詰め、当たりが右に遠い長い行は手前を … で省く。
        /// </summary>
        private Border CreateResultRow(DocumentLine line, int from, int to)
        {
            var source = Document.GetText(line.Offset, line.Length);

            var lead = 0;
            while (lead < source.Length && char.IsWhiteSpace(source[lead])) lead++;

            var firstColumn = _matchOffsets[from] - line.Offset;
            var start = firstColumn - lead > 30 ? firstColumn - 20 : lead;

            var text = new TextBlock();
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;

            if (start > lead)
            {
                var ellipsis = new System.Windows.Documents.Run("…");
                ellipsis.Foreground = Theme.Muted;
                text.Inlines.Add(ellipsis);
            }

            var position = start;

            for (var k = from; k < to; k++)
            {
                var column = _matchOffsets[k] - line.Offset;
                var length = Math.Min(_matchLengths[k], source.Length - column);
                if (column < position || length <= 0) continue;

                if (column > position)
                    text.Inlines.Add(new System.Windows.Documents.Run(source.Substring(position, column - position)));

                var hit = new System.Windows.Documents.Run(source.Substring(column, length));
                hit.Background = Theme.SearchHit;
                text.Inlines.Add(hit);

                position = column + length;
            }

            if (position < source.Length)
                text.Inlines.Add(new System.Windows.Documents.Run(source.Substring(position)));

            var number = new TextBlock();
            number.Text = line.LineNumber.ToString();
            number.Foreground = Theme.LineNumber;
            number.FontSize = 12;
            number.Width = 34;
            number.TextAlignment = TextAlignment.Right;
            number.Margin = new Thickness(0, 0, 10, 0);
            number.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(number, Dock.Left);

            var content = new DockPanel();
            content.Children.Add(number);
            content.Children.Add(text);

            var row = new Border();
            row.Padding = new Thickness(6, 2, 8, 2);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.Child = content;

            row.MouseEnter += delegate
            {
                if (row != _selectedResult) row.Background = ResourceBrush("HoverFill");
            };

            row.MouseLeave += delegate
            {
                if (row != _selectedResult) row.Background = Brushes.Transparent;
            };

            row.MouseLeftButtonUp += delegate { OpenResult(row, from); };

            return row;
        }

        /// <summary>
        /// その行の最初の当たりを選んで飛ぶ。フォーカスは本文へ渡すので、そのまま書き直せる。
        /// 一覧は出したまま、選んだ行に印を付ける。
        /// </summary>
        private void OpenResult(Border row, int index)
        {
            // 本文を書き換えると当たりは捨てて数え直す。古い一覧から飛ばない。
            if (index >= _matchOffsets.Count)
            {
                ApplySearch();
                return;
            }

            var offset = _matchOffsets[index];
            var length = _matchLengths[index];

            if (offset + length > Document.TextLength)
            {
                ApplySearch();
                return;
            }

            if (_selectedResult != null) _selectedResult.Background = Brushes.Transparent;
            _selectedResult = row;
            row.Background = ResourceBrush("SelectionFill");

            Select(offset, length);
            ScrollTo(Document.GetLineByOffset(offset).LineNumber, 0);
            Focus();

            _searchCount.Text = (index + 1) + " / " + _matchOffsets.Count;
        }

        private static Brush ResourceBrush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Transparent;
        }

        /// <summary>
        /// 照合は文書全体に対して行う。件数を出すにも移動するにも全体が要るため。
        ///
        /// ただし Markdown の解析のために全行のテキストをすでに保持しているので、
        /// それを舐めるだけで済み、新たな文字列の確保は起きない。
        /// 行ごとの照合なので、行をまたぐ正規表現は当たらない。
        /// </summary>
        private void RecomputeMatches()
        {
            _matchOffsets.Clear();
            _matchLengths.Clear();

            if (_search == null) return;

            var lines = Model.Lines;

            for (var i = 0; i < lines.Length; i++)
            {
                var matches = _search.Matches(lines[i].Source);
                if (matches.Count == 0) continue;

                var lineOffset = Document.GetLineByNumber(i + 1).Offset;

                foreach (Match match in matches)
                {
                    if (match.Length == 0) continue;   // a* のような空一致は拾わない

                    _matchOffsets.Add(lineOffset + match.Index);
                    _matchLengths.Add(match.Length);
                }
            }
        }

        /// <summary>
        /// 前後の当たりへ飛ぶ。端まで行ったら反対側へ回り込む。
        /// keepSearchFocus が真なら、検索欄にカーソルを残して続けて押せるようにする。
        /// </summary>
        private void MoveToMatch(int direction, bool keepSearchFocus)
        {
            // 数え直しが追いついていなければ、待たずにここで済ませる。
            if (_search != null && _matchOffsets.Count == 0)
            {
                _searchDelay.Stop();
                ApplySearch();
            }

            if (_matchOffsets.Count == 0)
            {
                GiveFocus(keepSearchFocus);
                return;
            }

            var target = -1;

            if (direction > 0)
            {
                var from = CaretOffset;

                for (var i = 0; i < _matchOffsets.Count; i++)
                {
                    if (_matchOffsets[i] <= from) continue;
                    target = i;
                    break;
                }

                if (target < 0) target = 0;
            }
            else
            {
                // 今いる当たりの先頭より前を探す。そうしないと同じ場所に留まる。
                var from = SelectionLength > 0 ? SelectionStart : CaretOffset;

                for (var i = _matchOffsets.Count - 1; i >= 0; i--)
                {
                    if (_matchOffsets[i] >= from) continue;
                    target = i;
                    break;
                }

                if (target < 0) target = _matchOffsets.Count - 1;
            }

            var offset = _matchOffsets[target];
            var length = _matchLengths[target];

            // 数え直しが追いついていない可能性があるので、文書と突き合わせてから飛ぶ。
            if (offset < 0 || offset + length > Document.TextLength)
            {
                ApplySearch();
                return;
            }

            Select(offset, length);
            ScrollTo(Document.GetLineByOffset(offset).LineNumber, 0);

            GiveFocus(keepSearchFocus);

            _searchCount.Text = (target + 1) + " / " + _matchOffsets.Count;
        }

        private void GiveFocus(bool toSearchBox)
        {
            if (toSearchBox) _searchBox.Focus();
            else Focus();
        }

        // =====================================================================
        // アウトライン（SPEC 7章）
        // =====================================================================

        /// <summary>
        /// 見出しの一覧は本文が変わるまで使い回す。
        /// キャレットが動くたびに全行を走査しないための切り分け。
        /// </summary>
        private List<OutlineHeading> EnsureOutline()
        {
            var model = Model;
            if (_outline != null) return _outline;

            _outline = model.Headings();
            return _outline;
        }

        public OutlineSnapshot GetOutline()
        {
            var entries = EnsureOutline();
            var caretLine = TextArea.Caret.Line;

            // 見出しは行番号の昇順に並んでいるので、直近の見出しは二分探索で足りる。
            var current = -1;
            var low = 0;
            var high = entries.Count - 1;

            while (low <= high)
            {
                var middle = (low + high) / 2;

                if (entries[middle].Line <= caretLine)
                {
                    current = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return new OutlineSnapshot(entries, current);
        }

        // =====================================================================
        // スタックの出し入れ（SPEC 6章）
        //
        // ページはパネルから外されると表示位置を失うため、外す直前に覚えて
        // 戻ってきたら復元する。サブページから戻ったとき、飛んだ場所に戻すため。
        // =====================================================================

        public void CaptureViewState()
        {
            _savedCaret = CaretOffset;
            _savedScroll = VerticalOffset;
        }

        public void RestoreViewState()
        {
            if (_savedCaret < 0) return;

            var caret = _savedCaret;
            var scroll = _savedScroll;

            // レイアウトが終わってからでないとスクロール位置が効かない。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (caret <= Document.TextLength) CaretOffset = caret;
                ScrollToVerticalOffset(scroll);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 同じページの中の見出しを指す anchor を探す。見つかればその行番号、無ければ -1。
        /// HTML のページ内リンクと同じで、飛ぶだけ。ページは増えない。
        /// </summary>
        public int FindHeading(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return -1;

            var wanted = NormalizeAnchor(anchor);
            if (wanted.Length == 0) return -1;

            foreach (var heading in GetOutline().Entries)
            {
                if (NormalizeAnchor(HeadingText.Display(heading.Text)) == wanted) return heading.Line;
            }

            return -1;
        }

        /// <summary>
        /// 見出しの文字列が anchor に当たるか（FindHeading と同じ突き合わせ）。ほかのページ・ほかの文書の見出しを探すときに使う。
        /// 見出しがリンク（`## [予定](#p3)`）なら、見えている名前（予定）で比べる（2026-11-02。リンクのコピーは見えている名前で書くため）。
        /// </summary>
        public static bool AnchorMatches(string headingText, string anchor)
        {
            if (string.IsNullOrEmpty(anchor) || string.IsNullOrEmpty(headingText)) return false;

            var wanted = NormalizeAnchor(anchor);
            return wanted.Length > 0 && NormalizeAnchor(HeadingText.Display(headingText)) == wanted;
        }

        /// <summary>
        /// 見出しの文字列と anchor を突き合わせるための正規化。
        /// `## My Section` を `#my-section` で引けるようにしつつ、
        /// 日本語の見出しはそのまま名前で引けるようにする。
        /// </summary>
        private static string NormalizeAnchor(string text)
        {
            return text.Trim().Replace(' ', '-').Replace('\t', '-').ToLowerInvariant();
        }

        /// <summary>
        /// その行へカーソルを置いて見えるところまでスクロールする。フォーカスは渡さない（開いた直後の行を生ソースにしないため）。
        /// 行が無ければ何もしない（先頭のまま）。ファイルを開き直して前の位置に戻すときに使う（SPEC 4章）。
        /// </summary>
        public void ShowLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Document.LineCount) return;

            CaretOffset = Document.GetLineByNumber(lineNumber).Offset;
            ScrollToLine(lineNumber);
        }

        public void GoToLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Document.LineCount) return;

            var line = Document.GetLineByNumber(lineNumber);
            CaretOffset = line.Offset;
            ScrollToLine(lineNumber);
            Focus();
        }

        // =====================================================================
        // リンクのクリック（SPEC 5章）
        // =====================================================================

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 単クリックで修飾キー無しのときだけ開く。
            // 無条件に奪うと、ダブルクリックの単語選択も Shift+クリックの選択伸ばしも
            // できなくなり、リンクを含む文字列を編集できなくなる。
            if (e.ClickCount != 1) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (IsInTable(e.OriginalSource)) return;   // 表の中のリンクは開かない。押すと生の Markdown に戻す

            // 画像はリンクと同じく開く。カーソルのある行でも、画像は編集する文字ではない。
            var link = ImageLinkOf(e.OriginalSource) ?? HitTestLink(e.GetPosition(this));
            if (link == null) return;

            e.Handled = true;

            var point = e.GetPosition(this);

            // 戻ってきたときにこのリンクの位置にいられるよう、カーソルを置いておく。
            var position = GetPositionFromPoint(point);
            if (position != null)
            {
                var line = Document.GetLineByNumber(position.Value.Line);
                CaretOffset = line.Offset + link.Start;
            }

            var handler = LinkActivated;
            if (handler != null) handler(this, new LinkActivatedEventArgs(link));
        }

        /// <summary>
        /// ダブルクリックは単語ではなく段落を選ぶ。段落は空行（空白だけの行も含む）で挟まれた範囲。
        /// AvalonEdit の単語選択は記号で切れるので、日本語の文では半端な所で止まる。
        /// 空行の上でダブルクリックしたときは既定の動きに任せる。
        /// </summary>
        private void OnParagraphDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (ImageLinkOf(e.OriginalSource) != null) return;
            if (IsInTable(e.OriginalSource)) return;

            var position = GetPositionFromPoint(e.GetPosition(this));
            if (position == null) return;

            var line = Document.GetLineByNumber(position.Value.Line);
            if (IsBlankLine(line)) return;

            var first = line;
            while (first.PreviousLine != null && !IsBlankLine(first.PreviousLine)) first = first.PreviousLine;

            var last = line;
            while (last.NextLine != null && !IsBlankLine(last.NextLine)) last = last.NextLine;

            e.Handled = true;
            Select(first.Offset, last.EndOffset - first.Offset);
            TextArea.Focus();
        }

        private bool IsBlankLine(DocumentLine line)
        {
            return Document.GetText(line.Offset, line.Length).Trim().Length == 0;
        }

        private void OnTextViewMouseMove(object sender, MouseEventArgs e)
        {
            var point = e.GetPosition(this);
            var over = ImageLinkOf(e.OriginalSource) != null || HitTestLink(point) != null;

            if (over) TextArea.TextView.Cursor = Cursors.Hand;
            else if (e.LeftButton == MouseButtonState.Released && IsOverSelection(point)) TextArea.TextView.Cursor = Cursors.SizeAll;
            else TextArea.TextView.Cursor = null;
        }

        /// <summary>
        /// その座標が選択範囲の文字の上か。掴んでドラッグすれば動かせる場所を、カーソルで見せる。
        /// 行末より右の空白は文字の上ではない（そこから押すと選択し直しになる）。
        /// </summary>
        private bool IsOverSelection(Point point)
        {
            var selection = TextArea.Selection;
            if (selection == null || selection.IsEmpty) return false;

            var textView = TextArea.TextView;
            var local = TranslatePoint(point, textView);
            if (local.X < 0 || local.Y < 0) return false;

            var visualY = local.Y + textView.VerticalOffset;
            var visualLine = textView.GetVisualLineFromVisualTop(visualY);
            if (visualLine == null) return false;

            var textLine = visualLine.GetTextLineByVisualYPosition(visualY);
            if (textLine == null) return false;
            if (local.X + textView.HorizontalOffset > textLine.WidthIncludingTrailingWhitespace) return false;

            var position = GetPositionFromPoint(point);
            if (position == null) return false;

            var offset = Document.GetOffset(position.Value.Location);
            foreach (var segment in selection.Segments)
            {
                if (offset >= segment.StartOffset && offset < segment.EndOffset) return true;
            }

            return false;
        }

        /// <summary>
        /// その座標にリンクがあるか。無ければ null。
        ///
        /// 座標から文字位置を求める処理は、行末より右をクリックすると
        /// 一番近い文字に吸い付く。そのままだと行末がリンクの行で、
        /// 右の空白をクリックしただけで飛んでしまうので、
        /// 先に「本当に文字の上か」を座標で確かめる。
        /// </summary>
        /// <summary>行の下に出した画像の上なら、その画像を指すリンク。</summary>
        private static LinkSpan ImageLinkOf(object source)
        {
            var element = source as FrameworkElement;
            return element == null ? null : element.Tag as LinkSpan;
        }

        private LinkSpan HitTestLink(Point point)
        {
            var textView = TextArea.TextView;
            if (textView == null) return null;

            var local = TranslatePoint(point, textView);

            // 行番号欄や左余白の上では何もしない。
            if (local.X < 0 || local.Y < 0) return null;

            var visualY = local.Y + textView.VerticalOffset;
            var visualX = local.X + textView.HorizontalOffset;

            var visualLine = textView.GetVisualLineFromVisualTop(visualY);
            if (visualLine == null) return null;

            var textLine = visualLine.GetTextLineByVisualYPosition(visualY);
            if (textLine == null) return null;

            // 行末より右の空白は、文字の上ではない。
            if (visualX > textLine.WidthIncludingTrailingWhitespace) return null;

            var lineNumber = visualLine.FirstDocumentLine.LineNumber;

            // 生ソースを見せている行は編集中。リンクではなくテキストとして扱う。
            if (IsLive(lineNumber)) return null;

            var info = GetLineInfo(lineNumber);
            if (info == null || info.Kind == LineKind.Code) return null;

            var scan = GetInline(lineNumber);
            if (scan == null) return null;

            var position = GetPositionFromPoint(point);
            if (position == null) return null;

            var line = Document.GetLineByNumber(lineNumber);
            var column = Document.GetOffset(position.Value.Location) - line.Offset;

            foreach (var link in scan.Links)
            {
                if (column >= link.Start && column < link.Start + link.Length) return link;
            }

            return null;
        }

        // =====================================================================
        // 差し込み
        // =====================================================================

        /// <summary>
        /// カーソル位置にテキストを差し込む（SPEC 9章）。
        /// ドロップでもここに入れる。落とした座標ではなく、あらかじめ置いた
        /// カーソルの位置に入るほうが狙いを定めやすいため。
        /// </summary>
        public void InsertAtCaret(string text)
        {
            if (IsReadOnly) return;   // 行き先の無いページには書かない

            // 選択を残したまま挿すと、次の1打鍵で選択ごと挿した分まで消える。
            TextArea.ClearSelection();

            var offset = CaretOffset;
            if (offset < 0 || offset > Document.TextLength) offset = Document.TextLength;

            Document.Insert(offset, text);
            CaretOffset = offset + text.Length;
            Focus();
        }

        /// <summary>サブページへのリンクを差し込む（SPEC 5章）。</summary>
        public void InsertPageLink(string label, string target)
        {
            var inserted = "[" + label + "](" + target + ")";

            // 矩形選択では SelectionStart / SelectionLength が「選択の外接範囲」になり、
            // 選んでいない列や行まで含んだ1本の連続範囲を返す。
            // それを置換すると、選択していない本文が消える。置換は単純な選択のときだけ。
            if (!IsSimpleSelection())
            {
                InsertAtCaret(inserted);
                return;
            }

            var start = SelectionStart;

            Document.Replace(start, SelectionLength, inserted);
            TextArea.ClearSelection();

            CaretOffset = start + inserted.Length;
            Focus();
        }

        /// <summary>ひと続きの範囲を選んでいるか。矩形選択や複数範囲なら false。</summary>
        private bool IsSimpleSelection()
        {
            var selection = TextArea.Selection;
            if (selection == null || selection.IsEmpty) return false;
            if (selection is RectangleSelection) return false;

            var count = 0;
            foreach (var segment in selection.Segments)
            {
                count++;
                if (count > 1) return false;
            }

            return count == 1;
        }

        /// <summary>選択範囲を置き換える。矩形選択などのときは何もしない。</summary>
        public void ReplaceSelectionWith(string text)
        {
            if (IsReadOnly || !IsSimpleSelection()) return;

            var start = SelectionStart;

            Document.Replace(start, SelectionLength, text);
            TextArea.ClearSelection();

            CaretOffset = start + text.Length;
            Focus();
        }

        /// <summary>
        /// 文書の一部を置き換える。切り出したページを本文に戻すときに使う。
        /// also は置き換えと同じ1回の操作として元に戻す・やり直す、文書の外の変更（ページの削除など）。
        /// 分けておくと、元に戻したときにリンクだけが戻り、指す先のページが無くなる。
        /// 置き換えられなければ false で、also も実行しない。
        /// </summary>
        public bool ReplaceRange(int offset, int length, string text, Action also, Action undoAlso)
        {
            // 行き先の無いページには書かない（Document への直接の書き込みは IsReadOnly では止まらない）。
            if (IsReadOnly) return false;
            if (offset < 0 || length < 0 || offset + length > Document.TextLength) return false;

            Document.UndoStack.StartUndoGroup();
            try
            {
                Document.Replace(offset, length, text);

                if (also != null)
                {
                    also();
                    Document.UndoStack.Push(new ExternalChange(undoAlso, also));
                }
            }
            finally
            {
                Document.UndoStack.EndUndoGroup();
            }

            TextArea.ClearSelection();

            CaretOffset = offset + text.Length;
            Focus();
            return true;
        }

        /// <summary>元に戻す履歴に載せる、文書の外の変更。</summary>
        private sealed class ExternalChange : IUndoableOperation
        {
            private readonly Action _undo;
            private readonly Action _redo;

            public ExternalChange(Action undo, Action redo)
            {
                _undo = undo;
                _redo = redo;
            }

            public void Undo()
            {
                if (_undo != null) _undo();
            }

            public void Redo()
            {
                if (_redo != null) _redo();
            }
        }

        /// <summary>選択している文字列。矩形選択などのときは空を返す。</summary>
        public string SimpleSelectedText
        {
            get { return IsSimpleSelection() ? SelectedText : string.Empty; }
        }

        // =====================================================================
        // 字下げの中にカーソルを置かない（SPEC 8.4）
        //
        // 字下げは文書上0文字なので、行頭の文書位置は「字下げの前（行の一番左）」と
        // 「後」の2か所に描ける。AvalonEdit は行の一番左を必ず止まれる位置として扱うため、
        // Backspace・Home・字下げの上のクリックで、カーソルが一番左へ行ってしまう。
        // 文書上は同じ位置なので打てば正しい位置に入るが、見た目がずれるので後ろへ戻す。
        // =====================================================================

        /// <summary>次のカーソル移動だけは、字下げの前に置いたままにする（← で前の行へ移るため）。</summary>
        private bool _allowBeforeIndent;

        /// <summary>この行の行頭に、文字数0の字下げがあるか（行頭の記号を消していない行）。</summary>
        private bool HasBareIndent(int lineNumber)
        {
            var info = GetLineInfo(lineNumber);
            if (info == null || info.Depth <= 0) return false;

            var ranges = GetHiddenRanges(lineNumber);
            if (ranges != null)
            {
                foreach (var range in ranges)
                {
                    if (range.Start == 0) return false;   // 記号と字下げが1つの要素になっている
                }
            }

            return true;
        }

        /// <summary>カーソルを戻している最中。戻したことでもう一度呼ばれても、何もしない。</summary>
        private bool _fixingCaret;

        private void KeepCaretOutOfIndent(object sender, EventArgs e)
        {
            if (_fixingCaret) return;

            if (_allowBeforeIndent)
            {
                _allowBeforeIndent = false;
                return;
            }

            var caret = TextArea.Caret;
            if (caret.Column != 1 || caret.VisualColumn != 0) return;
            if (!HasBareIndent(caret.Line)) return;

            // 字下げの後ろが、カーソルの止まれる位置であることを確かめる。行頭が結合文字
            // （後から付ける濁点など）だと止まれず、AvalonEdit が一番左へ押し戻す。
            // そこへまた戻すと押し合いが終わらず、捕まえられない形で落ちる。
            var visualLine = TextArea.TextView.GetVisualLine(caret.Line);
            if (visualLine == null) return;

            var stop = visualLine.GetNextCaretPosition(0, System.Windows.Documents.LogicalDirection.Forward,
                CaretPositioningMode.Normal, false);
            if (stop != 1) return;

            _fixingCaret = true;
            try
            {
                // 表示上の列 1 = 字下げの後ろ。文書上の位置は変わらない。
                caret.Position = new TextViewPosition(caret.Line, 1, 1);
            }
            finally
            {
                _fixingCaret = false;
            }
        }

        /// <summary>
        /// 行頭（字下げの後ろ）で ← を押したら前の行の末尾へ、Backspace なら前の行とつなげる。
        /// 字下げの後ろからだと、AvalonEdit はまず同じ位置の一番左へ動かす（Backspace は
        /// そこまでの0文字を消す）だけなので、それを上の処理が戻すと何も起きなくなる。
        /// 先に一番左へ置いてから、キーを通す。Shift+← の選択も同じ経路で前の行へ伸びる。
        /// </summary>
        private void OnIndentKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Left && e.Key != Key.Back) return;

            // 選択があれば Backspace は選択を消すだけなので、手を出さない。
            if (e.Key == Key.Back && !TextArea.Selection.IsEmpty) return;

            // 1行目では前の行が無く ← は何もしないので、一番左に置いたままになってしまう。
            var caret = TextArea.Caret;
            if (caret.Line <= 1 || caret.Column != 1 || caret.VisualColumn == 0) return;
            if (!HasBareIndent(caret.Line)) return;

            _allowBeforeIndent = true;
            caret.Position = new TextViewPosition(caret.Line, 1, 0);
        }

        /// <summary>行頭の印（見出し・引用・箇条書き・番号・チェックボックス）。見出しに変えるときに外す。</summary>
        private static readonly Regex LineOpener = new Regex(
            @"^[ \t]*(?:>[ \t]*)*(?:#{1,6}(?:[ \t]+|$)|(?:[-*+]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)?", RegexOptions.Compiled);

        /// <summary>
        /// Ctrl+Enter で、今の行を上の節と同じレベルの見出しにする（2026-10-25）。Ctrl+Shift+Enter なら1段下のレベル（最大 6）。
        /// 同じレベルの見出しを続けて書くことが多く、毎回 ## を打つ手間を省くため。
        /// レベルは、今の行より上で一番近い見出しと同じ（無ければ 1）。今の行は見出しに変えるので数えない。
        /// ・空の行なら、その行を「## 」にしてキャレットを後ろに置く
        /// ・文字のある行なら、行頭の印（見出しの #・箇条書き・引用）を「## 」に付け替える。リンクの行もそのまま見出しになる
        ///   （[カーテン](#p3) → ## [カーテン](#p3)）。キャレットは行末に置く
        /// 元に戻すは1回。コードの中・表の行・区切り線・下線式の見出しの行では何もしない。
        /// </summary>
        private void OnSiblingHeadingKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || IsReadOnly) return;

            var deeper = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            if (!deeper && Keyboard.Modifiers != ModifierKeys.Control) return;

            var number = TextArea.Caret.Line;
            var current = GetLineInfo(number);
            var kind = current.Kind;
            if (kind == LineKind.Code || kind == LineKind.Fence || kind == LineKind.Table) return;

            e.Handled = true;

            var line = Document.GetLineByNumber(number);
            var text = Document.GetText(line);

            // 下線式の見出し（=== / --- の上の行）は # を付けると下線が残って形が崩れるので、変えない。
            if (kind == LineKind.Setext || kind == LineKind.Rule || current.ContinuesHeading
                || (kind == LineKind.Heading && !HeadingOpener.IsMatch(text))) return;

            var level = 1;
            for (var n = number - 1; n >= 1; n--)
            {
                var info = GetLineInfo(n);
                if (info.Kind != LineKind.Heading || info.ContinuesHeading) continue;

                level = Math.Max(1, Math.Min(6, info.Level));
                if (deeper) level = Math.Min(6, level + 1);
                break;
            }

            var marker = new string('#', level) + " ";

            TextArea.ClearSelection();

            var opener = LineOpener.Match(text).Length;
            if (text.Substring(opener).Trim().Length == 0)
            {
                Document.Replace(line.Offset, line.Length, marker);
                CaretOffset = line.Offset + marker.Length;
            }
            else
            {
                Document.Replace(line.Offset, opener, marker);
                CaretOffset = Document.GetLineByNumber(number).EndOffset;
            }

            FollowCaret();
        }
    }
}

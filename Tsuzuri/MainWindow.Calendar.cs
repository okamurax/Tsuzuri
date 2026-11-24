using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Sync;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: カレンダー（SPEC 22章。2026-11-20）。
    // 予定を週の行で下へずっと並べ、カレンダーの画面で直接打つ。保存先は .md（ScheduleFile）で、裏に置くだけ（利用者の考え）。
    // ランチャーと同じく、本文の場所いっぱいに重ねて出し、本文の左下のつまみ（ランチャーのつまみと縦に並ぶ。MainWindow.SideTabs.cs）か Alt+C で出し入れする（2026-11-23。前は画面のタブの並びのボタン）。
    // ランチャーとは同時に出さない（片方を出すと、もう片方を閉じる）。
    // アウトラインの見出しをカレンダーの日へドラッグすると、その見出しと結んだ予定になる（2026-11-20）。結ぶのは見出しの文字で、名前が変わっても追わない。
    public partial class MainWindow
    {
        private CalendarView _calendar;

        /// <summary>
        /// 予定の保存先。app.json の scheduleFile。無ければ app.json の隣の schedule.md。
        /// 人が直接開かないファイルなので、名前に日本語を使わない（2026-11-21。前は 予定.md。前の名前からの移し替えは持たない）。
        /// </summary>
        private string ScheduleFilePath
        {
            get { return SchedulePathOf(_settings); }
        }

        /// <summary>予定の保存先（ScheduleFilePath と同じ。通知を出す ScheduleNotifier からも使う）。</summary>
        internal static string SchedulePathOf(AppSettings settings)
        {
            var path = settings.ScheduleFile;
            if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path.Trim());
            return Path.Combine(Path.GetDirectoryName(AppSettings.StorePath) ?? ".", "schedule.md");
        }

        /// <summary>
        /// 予定の通知（Windows の通知）を押したとき（2026-11-22）。前に出ている画面を前に出し、カレンダーを開く。
        /// 開いていればそのまま（ランチャーの下に隠れていれば、ランチャーを閉じて見せる）。
        /// </summary>
        internal static void ShowCalendarFromNotice()
        {
            var window = FrontWindow();
            if (window == null) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            if (window._calendar == null || window._launcher != null) window.ToggleCalendar();
        }

        private void ToggleCalendar()
        {
            // 図エディタを出している間は出さない（2026-11-23。利用者の判断。どちらも同じように覆うのでかぶる）
            if (_calendar == null && _drawing != null)
            {
                Status("図エディタを出している間は、カレンダーを出せません。図を保存するか終了してから出してください");
                _drawing.View.FocusCanvas();
                return;
            }

            if (_calendar != null)
            {
                // ランチャーの下に隠れていれば、ランチャーを閉じてカレンダーを見せる（閉じるのは見えているときに押したとき）
                if (_launcher != null) CloseLauncher();
                else CloseCalendar();
                return;
            }

            CloseLauncher();
            var heights = ReadWeekHeights();
            _calendar = new CalendarView(ScheduleFilePath, Status, ScheduleHeadings, OpenScheduleLink,
                                         heights, delegate { WriteWeekHeights(heights); },
                                         _settings.CalendarRowHeight ?? 0, WriteRowHeight, SendScheduleToGoogle);
            if (_googleSending) _calendar.SetSendingToGoogle(true);
            CalendarHost.Child = _calendar;
            CalendarHost.Visibility = Visibility.Visible;
            PaintSideTabs();

            // 覆われた本文に打ち込まないよう、キー入力をカレンダーへ移す（ランチャーと同じ）
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_calendar != null && !CalendarHost.IsKeyboardFocusWithin) _calendar.Focus();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>高さを広げた週（app.json の calendarWeekHeights。「yyyy-MM-dd=高さ」）。読めない項目は捨てる。</summary>
        private Dictionary<DateTime, double> ReadWeekHeights()
        {
            var heights = new Dictionary<DateTime, double>();
            foreach (var entry in _settings.CalendarWeekHeights ?? new List<string>())
            {
                var parts = (entry ?? string.Empty).Split('=');
                DateTime week;
                double height;
                if (parts.Length == 2
                    && DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out week)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height)
                    && height > 0 && height < 10000)
                    heights[week.Date] = height;
            }
            return heights;
        }

        /// <summary>全部の週の高さ（app.json の calendarRowHeight）。元の高さなら持たない。</summary>
        private void WriteRowHeight(double height)
        {
            _settings.CalendarRowHeight = height > 0 ? Math.Round(height) : (double?)null;
            _settings.Save();
        }

        private void WriteWeekHeights(Dictionary<DateTime, double> heights)
        {
            var list = heights.OrderBy(pair => pair.Key)
                              .Select(pair => pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "="
                                              + Math.Round(pair.Value).ToString(CultureInfo.InvariantCulture))
                              .ToList();
            _settings.CalendarWeekHeights = list.Count > 0 ? list : null;
            _settings.Save();
        }

        /// <summary>
        /// AI に渡す今の状態に、カレンダーを足す（2026-11-21。利用者と日付の話をするため）。開いていなければ何も書かない。
        /// 見えている週・選んでいる日・選んでいる予定。保存先や結んだ文書が AI に渡さない場所なら、その中身は書かない。
        /// </summary>
        private void AppendCalendarContext(StringBuilder text)
        {
            var calendar = _calendar;
            if (calendar == null) return;

            if (AiGuard.IsGuarded(calendar.FilePath))
            {
                text.Append("カレンダー: 開いている（予定の保存先は、利用者が AI に渡さないと決めた場所にある。中身を尋ねたり探したりしない）\n");
                return;
            }

            DateTime first, last;
            calendar.VisibleRange(out first, out last);
            text.Append("カレンダー: 開いている（予定の保存先: ").Append(calendar.FilePath).Append("）\n");
            text.Append("  見えている週: ").Append(Day(first)).Append("〜").Append(Day(last)).Append('\n');

            var from = calendar.SelectedFrom;
            var to = calendar.SelectedTo;
            if (from.HasValue && to.HasValue)
            {
                text.Append("  選んでいる日: ").Append(Day(from.Value));
                if (to.Value > from.Value) text.Append("〜").Append(Day(to.Value)).Append("（").Append((int)(to.Value - from.Value).TotalDays + 1).Append(" 日）");
                text.Append('\n');
            }

            var item = calendar.SelectedItem;
            var dates = calendar.SelectedDates;
            if (item != null && dates != null)
            {
                // 繰り返しの予定は、選んだ回の日と、決まり（保存先の repeat の行と同じ RRULE）
                text.Append("  選んでいる予定: ").Append(Day(dates.Item1));
                if (dates.Item2 > dates.Item1) text.Append("〜").Append(Day(dates.Item2));
                if (item.IsRepeat)
                {
                    var rule = ScheduleRepeat.Parse(item.Repeat);
                    text.Append("（繰り返しの1回。").Append(rule != null ? rule.Describe(item.Start) : item.Repeat)
                        .Append("、").Append(Day(item.Start)).Append(" から。repeat: ").Append(item.Repeat).Append('）');
                }
                if (item.Notify.HasValue) text.Append("（通知 ").Append(ScheduleFile.NotifyText(item.Notify.Value)).Append('）');
                if (item.IsLinked && AiGuard.IsGuarded(item.LinkFile))
                    text.Append("（AI に渡さない場所の文書の見出しと結んだ予定。名前は伏せた）\n");
                else
                {
                    text.Append(" ").Append(item.Title);
                    if (item.IsLinked) text.Append("（文書の見出しと結んだ予定: ").Append(item.LinkFile).Append(" の「").Append(item.LinkHeading).Append("」）");
                    text.Append(item.Memo.Trim().Length > 0 ? "（メモあり。保存先を読む）" : string.Empty).Append('\n');
                }
            }

            if (!(from.HasValue) && item == null) text.Append("  選んでいる日・予定: なし\n");
        }

        /// <summary>AI に渡す日付（2026-11-21（土））。</summary>
        private static string Day(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "（" + CultureInfo.GetCultureInfo("ja-JP").DateTimeFormat.GetShortestDayName(date.DayOfWeek) + "）";
        }

        /// <summary>カレンダーを閉じる。マスで打っている名前・欄で直している中身は、先に保存する（打った文字を捨てない）。閉じたら true。</summary>
        // ---------------------------------------------------------------
        // Google へ送る（SPEC 22章「Google へ送る」。2026-11-23）
        // カレンダーの上の段の「Google へ送る」を押したときだけ送る（利用者の考え）。今日以降に掛かる予定を、Google の「Tsuzuri」カレンダーへ入れ替える。
        // 送る形にするのは ScheduleExport（送り先に依らない）、送るのは GoogleCalendarClient。
        // ---------------------------------------------------------------

        /// <summary>送っている間（アプリで1つ。ほかの画面のカレンダーからも重ねて送らない）。</summary>
        private static bool _googleSending;

        private async void SendScheduleToGoogle()
        {
            if (_googleSending)
            {
                Status("Google へ送っているところです");
                return;
            }
            if (_calendar != null && !_calendar.CommitEdit())
            {
                Status("予定の欄に直すところがあります。直して保存するか、「キャンセル」を押してから送ってください");
                return;
            }

            GoogleSettings google;
            try
            {
                google = GoogleSettings.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "google.json を読めませんでした。書き方を確かめてください。" + Environment.NewLine + ex.Message, "Google へ送る",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (google == null || !google.IsUsable)
            {
                ShowGoogleSetup(google == null);
                return;
            }

            // 予定はファイルから読む（カレンダーは直すたびに保存している）
            List<ScheduleItem> items;
            try
            {
                var path = ScheduleFilePath;
                items = ScheduleFile.Parse(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "予定のファイルを読めませんでした。" + Environment.NewLine + ex.Message, "Google へ送る", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var today = DateTime.Today;

            // 押し間違えで送らないように、送る前に尋ねる（2026-11-24 利用者の希望）。ログインの前・送る前
            if (MessageBox.Show(this,
                    "今日以降（前に送った日がそれより前なら、その日以降）に掛かる予定を、Google の「Tsuzuri」カレンダーへ送ります。" + Environment.NewLine +
                    "そのカレンダーの同じ範囲の予定は、送った予定に入れ替わります。送りますか？",
                    "Google へ送る", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            {
                Status("Google へは送りませんでした");
                return;
            }

            _googleSending = true;
            PaintGoogleSending();
            try
            {
                Action<string> progress = message => Dispatcher.BeginInvoke(new Action(() => Status(message)));
                Func<int, bool> confirmEmpty = count => (bool)Dispatcher.Invoke(new Func<bool>(() =>
                    MessageBox.Show(this, "送る予定がありません（今日以降（前に送った日がそれより前なら、その日以降）に掛かる予定が無い）。" + Environment.NewLine +
                                    "Google の「Tsuzuri」カレンダーにある同じ範囲の予定 " + count + " 件を、全部消してよいですか？",
                                    "Google へ送る", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK));
                // ブラウザでのログインを待つ間は、URL を貼る窓を出す（ブラウザが Mac 側で開くと 127.0.0.1 へ戻れないため）
                var client = new GoogleCalendarClient(google, progress);
                Window paste = null;
                client.LoginWaiting = () => Dispatcher.BeginInvoke(new Action(() => paste = ShowGooglePaste(client)));
                client.LoginDone = () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (paste == null) return;
                    paste.Tag = "done";
                    paste.Close();
                }));
                // 送る範囲の初めの日（今日か、前に送った日）は Google の口が決める
                var result = await Task.Run(() => client.Replace(from => ScheduleExport.Build(items, from), today, confirmEmpty));

                if (result.Cancelled) Status("Google へは送りませんでした");
                else
                    Status("Google へ送りました: " + result.Sent + " 件" + (result.CreatedCalendar ? "（「Tsuzuri」カレンダーを作りました）" : "（前の " + result.Removed + " 件と入れ替え）")
                           + (result.NotRemoved > 0 ? "。前の予定 " + result.NotRemoved + " 件を消せませんでした（次に送るときにまた消します）" : string.Empty));
            }
            catch (GoogleException ex)
            {
                Status("Google へ送れませんでした");
                MessageBox.Show(this, ex.Message, "Google へ送る", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Status("Google へ送れませんでした");
                MessageBox.Show(this, "Google へ送れませんでした。" + Environment.NewLine + ex.Message, "Google へ送る", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _googleSending = false;
                PaintGoogleSending();
            }
        }

        /// <summary>どの画面のカレンダーの「Google へ送る」も、送っている間は押せなくする（送っているのはアプリで1つ）。</summary>
        private static void PaintGoogleSending()
        {
            foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                if (window._calendar != null) window._calendar.SetSendingToGoogle(_googleSending);
        }

        /// <summary>
        /// ブラウザでログインしている間の小さな窓。ブラウザが「127.0.0.1 に接続できません」になったら、そのタブのアドレス欄の URL を貼って続ける
        /// （Parallels の設定で、Windows から開いたリンクが Mac のブラウザで開くと、Windows の Tsuzuri へ戻れないため。2026-11-23）。閉じるとログインをやめる。
        /// </summary>
        private Window ShowGooglePaste(GoogleCalendarClient client)
        {
            var box = new TextBox { Margin = new Thickness(0, 8, 0, 8), MinWidth = 420 };
            var ok = new Button { Content = "続ける", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "やめる", IsCancel = true, MinWidth = 80 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                Text = "ブラウザで Google にログインしてください。" + Environment.NewLine + Environment.NewLine +
                       "ログインのあと、ブラウザに「127.0.0.1 に接続できません」と出たら、そのタブのアドレス欄の URL（http://127.0.0.1:…/?state=…&code=…）を全部コピーして、ここに貼って「続ける」を押してください。" +
                       "「ログインしました」と出たら、何もしなくてかまいません。",
            });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            var window = new Window
            {
                Title = "Google にログイン",
                Content = panel,
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            ok.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                client.SubmitRedirectUrl(box.Text);
                box.Clear();
            };
            cancel.Click += delegate { window.Close(); };
            window.Closed += delegate { if (window.Tag as string != "done") client.CancelLogin(); };
            window.Show();
            box.Focus();
            return window;
        }

        /// <summary>google.json が無い・埋めていない。ひな形を作ってメモ帳で開き、作り方を知らせる（WordPress の wordpress.json と同じやり方）。</summary>
        private void ShowGoogleSetup(bool missing)
        {
            try
            {
                GoogleSettings.WriteTemplate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "google.json を作れませんでした。" + Environment.NewLine + ex.Message, "Google へ送る", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                (missing ? "Google へ送る設定がまだありません。ひな形の google.json を作りました。" : "google.json の clientId・clientSecret が埋まっていません（ひな形のまま等）。") +
                Environment.NewLine + Environment.NewLine +
                "Google Cloud のプロジェクトで Google Calendar API を有効にし、OAuth クライアント（種類は「デスクトップ アプリ」）を作って、" +
                "その ID と秘密を google.json に書いてください（作ったときの「JSON をダウンロード」のファイルを google.json という名前で置き換えてもよい）。" +
                "手順は README の「Google カレンダーへ送る」にあります。" + Environment.NewLine +
                "google.json はメモ帳で開きます。",
                "Google へ送る", MessageBoxButton.OK, MessageBoxImage.Information);
            try
            {
                Process.Start("notepad.exe", "\"" + GoogleSettings.StorePath + "\"");
            }
            catch (Exception)
            {
                Status("google.json: " + GoogleSettings.StorePath);
            }
        }

        private bool CloseCalendar()
        {
            if (_calendar == null) return true;
            if (!_calendar.CommitEdit())
            {
                Status("予定の欄に直すところがあります。直して保存するか、「キャンセル」を押してください");
                return false;
            }

            var hadFocus = CalendarHost.IsKeyboardFocusWithin;
            _calendar = null;
            CalendarHost.Child = null;
            CalendarHost.Visibility = Visibility.Collapsed;
            PaintSideTabs();
            if (hadFocus && Active != null) Active.TextArea.Focus();
            return true;
        }

        // ---------------------------------------------------------------
        // アウトラインの見出しと結んだ予定
        // ---------------------------------------------------------------

        /// <summary>閉じている文書の見出しの文字（ファイルの更新日時で覚える。全部の画面で共有）。カレンダーを組み直すたびに読み直さないため。</summary>
        private static readonly Dictionary<string, KeyValuePair<DateTime, HashSet<string>>> HeadingCache =
            new Dictionary<string, KeyValuePair<DateTime, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 結んだ文書の見出しの文字（どのページでも。ブックマークと同じ探し方）。文書が無ければ null。カレンダーが組み直すたびに、文書ごとに1回呼ぶ。
        /// どこかの画面で開いていれば、その中身（保存していない編集も）で見る。閉じていればファイルを読む（更新日時で覚える）。
        /// </summary>
        private HashSet<string> ScheduleHeadings(string file)
        {
            foreach (var window in OpenWindows)
            {
                var open = window.FindOpenDocument(file);
                if (open != null) return HeadingsOf(open);
            }

            if (!File.Exists(file)) return null;
            var stamp = File.GetLastWriteTimeUtc(file);
            KeyValuePair<DateTime, HashSet<string>> cached;
            if (!HeadingCache.TryGetValue(file, out cached) || cached.Key != stamp)
            {
                cached = new KeyValuePair<DateTime, HashSet<string>>(stamp, HeadingsOf(PageDocument.Load(file)));
                HeadingCache[file] = cached;
            }
            return cached.Value;
        }

        private static HashSet<string> HeadingsOf(PageDocument document)
        {
            var headings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in document.Pages)
                foreach (var heading in MarkdownParser.Headings(document.TextOf(page.Id))) headings.Add(heading.Text);
            return headings;
        }

        /// <summary>結んだ見出しへ飛ぶ。カレンダーを閉じ、文書を出して（ほかの画面で開いていればその画面を前に出し、閉じていれば開く）、見出しへ移る。</summary>
        private void OpenScheduleLink(ScheduleItem item)
        {
            if (!File.Exists(item.LinkFile))
            {
                Status("文書が見つかりません: " + item.LinkFile);
                return;
            }
            if (!CloseCalendar()) return;

            var mark = new HeadingBookmark { File = item.LinkFile, Page = item.LinkPage ?? string.Empty, Text = item.LinkHeading };
            OpenFileThen(item.LinkFile, (window, document) =>
            {
                mark.File = document.FilePath;
                window.OpenHeadingBookmark(mark);
            });
        }

        /// <summary>アウトラインの見出しをつかんで動かしている（MainWindow.OutlineDrag.cs）。カレンダーの日の上なら、その日を塗って true。</summary>
        private bool ShowCalendarDrop()
        {
            return _calendar != null && _calendar.ShowDrop(Mouse.GetPosition(_calendar));
        }

        /// <summary>
        /// アウトラインの見出しを離した。カレンダーの日の上なら、その見出しと結んだ予定を足して true（節は動かさない）。
        /// 保存していない文書（ファイルが無い）の見出しは結べないので断る。
        /// </summary>
        private bool DropOnCalendar(string heading, bool released)
        {
            if (_calendar == null) return false;
            if (!released || !_calendar.ShowDrop(Mouse.GetPosition(_calendar)))
            {
                _calendar.ClearDrop();
                return false;
            }

            var owner = _outlineOwner ?? Active;
            if (owner == null || string.IsNullOrEmpty(owner.FilePath) || !File.Exists(owner.FilePath))
            {
                _calendar.ClearDrop();
                Status("保存していない文書の見出しは、予定にできません。先に保存してください");
                return true;
            }

            _calendar.DropHeading(owner.FilePath, owner.PageId ?? string.Empty, heading);
            return true;
        }
    }
}

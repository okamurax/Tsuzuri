using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>カレンダーの予定1つ（SPEC 22章）。日だけを持つ（時間は名前に書く。利用者の書き方）。</summary>
    public sealed class ScheduleItem
    {
        /// <summary>始まりの日（時刻は持たない）。</summary>
        public DateTime Start;

        /// <summary>終わりの日（その日を含む）。1日だけなら Start と同じ。</summary>
        public DateTime End;

        public string Title = string.Empty;

        /// <summary>自由に書くメモ（何行でも）。無ければ空。</summary>
        public string Memo = string.Empty;

        /// <summary>
        /// アウトラインの見出しから登録した予定なら、その見出し（文書のファイル・ページ・見出しの文字）。直接打った予定は null（2026-11-20）。
        /// 名前（Title）は登録したときの見出しの文字。見出しの名前が変わっても追わない（見つからない印を出すだけ。利用者の判断）。
        /// </summary>
        public string LinkFile;
        public string LinkPage;
        public string LinkHeading;

        public bool IsLinked
        {
            get { return !string.IsNullOrEmpty(LinkFile); }
        }

        /// <summary>
        /// 繰り返しの決まり（RRULE。ScheduleRepeat。2026-11-21）。繰り返さなければ null。Start が1回目、End - Start が1回の長さ。
        /// </summary>
        public string Repeat;

        /// <summary>繰り返しのうち、消した回（決まりどおりの日）。</summary>
        public List<DateTime> Skips = new List<DateTime>();

        /// <summary>繰り返しのうち、動かした回（決まりどおりの日 → 動かした先の始まりの日）。</summary>
        public SortedDictionary<DateTime, DateTime> Moves = new SortedDictionary<DateTime, DateTime>();

        public bool IsRepeat
        {
            get { return !string.IsNullOrEmpty(Repeat); }
        }

        public ScheduleItem Clone()
        {
            return new ScheduleItem
            {
                Start = Start, End = End, Title = Title, Memo = Memo,
                LinkFile = LinkFile, LinkPage = LinkPage, LinkHeading = LinkHeading,
                Repeat = Repeat, Skips = new List<DateTime>(Skips), Moves = new SortedDictionary<DateTime, DateTime>(Moves),
            };
        }

        /// <summary>何日にまたがるか（1 以上）。</summary>
        public int Days
        {
            get { return (int)(End - Start).TotalDays + 1; }
        }
    }

    /// <summary>
    /// カレンダーの予定の保存先（SPEC 22章。2026-11-20）。予定はカレンダーの画面で直し、.md は裏の置き場所として扱う（利用者の考え）。
    /// それでも Markdown にしておくのは、人も Claude も読め、ほかの文書と同じに持ち運べるから。形:
    ///
    ///   &lt;!-- tsuzuri-schedule … --&gt;
    ///   # 予定
    ///
    ///   ## 2026-11-21 10:00 A社打合せ
    ///
    ///   メモ（何行でも）
    ///
    ///   ## 2026-11-21〜2026-11-25 出張
    ///
    ///   ## 2026-11-26 見積の作成
    ///   &lt;!-- link: D:\案件\見積.md | p3 | 見積の作成 --&gt;   ← アウトラインの見出しから登録した予定（見出しのすぐ下の行）
    ///
    ///   ## 2026-11-24 10:00 定例
    ///   &lt;!-- repeat: FREQ=WEEKLY;BYDAY=TU --&gt;              ← 繰り返し（RRULE。ScheduleRepeat。2026-11-21）
    ///   &lt;!-- skip: 2026-12-29 2027-01-05 --&gt;               ← 消した回
    ///   &lt;!-- move: 2026-12-08&gt;2026-12-09 --&gt;              ← 動かした回（元の日&gt;動かした先）
    ///
    /// 見出しのすぐ下の link・repeat・skip・move の行（どの順でもよい。書くときはこの順）は予定の中身で、メモではない。
    ///
    /// 予定1つが見出し1つ。見出しは「日付（〜終わりの日）」と名前。最初の予定より前の行は読み飛ばし、書くときは決まった頭を付ける。
    /// メモの中の「## 」で始まる行は、書くときに頭に \ を付け、読むときに外す（予定の見出しと見分けるため。\ が付いた行も1つ足して1つ外す）。
    /// 書くときは、始まりの日の順に並べる（同じ日なら前の並びのまま）。
    /// </summary>
    public static class ScheduleFile
    {
        public const string Marker = "<!-- tsuzuri-schedule: Tsuzuri のカレンダーのデータ。カレンダーの画面で直す -->";

        private const string DateFormat = "yyyy-MM-dd";

        // 「## 2026-11-21 名前」「## 2026-11-21〜2026-11-25 名前」。〜 の代わりに ~ も読む。名前は空でもよい
        private static readonly Regex HeaderRe = new Regex(
            @"^## (\d{4}-\d{2}-\d{2})(?:\s*[〜~]\s*(\d{4}-\d{2}-\d{2}))?(?: (.*))?$", RegexOptions.Compiled);

        // アウトラインの見出しから登録した予定の、見出しの場所（予定の見出しのすぐ下の行）。ファイル | ページ | 見出しの文字
        private static readonly Regex LinkRe = new Regex(@"^<!-- link: (.+?) \| (.*?) \| (.*) -->$", RegexOptions.Compiled);

        // 繰り返しの決まり・消した回・動かした回（見出しのすぐ下の行。2026-11-21）
        private static readonly Regex RepeatRe = new Regex(@"^<!-- repeat: ([A-Za-z0-9=;,\-]+) -->$", RegexOptions.Compiled);
        private static readonly Regex SkipRe = new Regex(@"^<!-- skip: ([0-9\- ]+) -->$", RegexOptions.Compiled);
        private static readonly Regex MoveRe = new Regex(@"^<!-- move: ([0-9\-> ]+) -->$", RegexOptions.Compiled);

        // メモの中の「## 」「\## 」「\\## 」…。書くときに \ を1つ足し、読むときに1つ外す（\ の付いた行を書いても、読み戻すと同じになる）
        private static readonly Regex EscapedRe = new Regex(@"^\\*## ", RegexOptions.Compiled);

        public static List<ScheduleItem> Parse(string text)
        {
            var items = new List<ScheduleItem>();
            if (string.IsNullOrEmpty(text)) return items;

            ScheduleItem current = null;
            var memo = new List<string>();

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var match = HeaderRe.Match(raw);
                DateTime start;
                if (match.Success && TryDate(match.Groups[1].Value, out start))
                {
                    Finish(current, memo, items);
                    DateTime end;
                    if (!match.Groups[2].Success || !TryDate(match.Groups[2].Value, out end) || end < start) end = start;
                    current = new ScheduleItem
                    {
                        Start = start,
                        End = end,
                        Title = match.Groups[3].Success ? match.Groups[3].Value.Trim() : string.Empty,
                    };
                    memo.Clear();
                    continue;
                }

                if (current == null) continue;   // 最初の予定より前（印・題）は読み飛ばす

                // 予定の見出しのすぐ下の行（メモより前）が link・repeat・skip・move なら、予定の中身。それぞれ1回だけ
                if (memo.Count == 0 && ReadMeta(current, raw)) continue;
                memo.Add(EscapedRe.IsMatch(raw) && raw[0] == '\\' ? raw.Substring(1) : raw);
            }

            Finish(current, memo, items);
            return items;
        }

        private static bool ReadMeta(ScheduleItem item, string line)
        {
            if (!line.StartsWith("<!-- ", StringComparison.Ordinal)) return false;

            Match match;
            if (!item.IsLinked && (match = LinkRe.Match(line)).Success)
            {
                item.LinkFile = match.Groups[1].Value;
                item.LinkPage = match.Groups[2].Value;
                item.LinkHeading = match.Groups[3].Value;
                return true;
            }
            if (!item.IsRepeat && (match = RepeatRe.Match(line)).Success)
            {
                item.Repeat = match.Groups[1].Value;
                return true;
            }
            if (item.Skips.Count == 0 && (match = SkipRe.Match(line)).Success)
            {
                var dates = new List<DateTime>();
                foreach (var word in match.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    DateTime date;
                    if (!TryDate(word, out date)) return false;
                    if (!dates.Contains(date)) dates.Add(date);
                }
                if (dates.Count == 0) return false;
                dates.Sort();
                item.Skips = dates;
                return true;
            }
            if (item.Moves.Count == 0 && (match = MoveRe.Match(line)).Success)
            {
                var moves = new SortedDictionary<DateTime, DateTime>();
                foreach (var word in match.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = word.Split('>');
                    DateTime from, to;
                    if (pair.Length != 2 || !TryDate(pair[0], out from) || !TryDate(pair[1], out to) || moves.ContainsKey(from)) return false;
                    moves[from] = to;
                }
                if (moves.Count == 0) return false;
                item.Moves = moves;
                return true;
            }
            return false;
        }

        private static void Finish(ScheduleItem item, List<string> memo, List<ScheduleItem> items)
        {
            if (item == null) return;

            var first = 0;
            var last = memo.Count - 1;
            while (first <= last && memo[first].Trim().Length == 0) first++;
            while (last >= first && memo[last].Trim().Length == 0) last--;
            item.Memo = first <= last ? string.Join("\n", memo.Skip(first).Take(last - first + 1)) : string.Empty;
            items.Add(item);
        }

        public static string Serialize(IEnumerable<ScheduleItem> items)
        {
            var text = new StringBuilder();
            text.Append(Marker).Append('\n');
            text.Append("# 予定\n");

            // 始まりの日の順。同じ日は前の並びのまま（OrderBy は安定）
            foreach (var item in items.OrderBy(item => item.Start))
            {
                text.Append('\n').Append("## ").Append(item.Start.ToString(DateFormat, CultureInfo.InvariantCulture));
                if (item.End > item.Start) text.Append('〜').Append(item.End.ToString(DateFormat, CultureInfo.InvariantCulture));
                var title = OneLine(item.Title);
                if (title.Length > 0) text.Append(' ').Append(title);
                text.Append('\n');
                if (item.IsLinked)
                    text.Append("<!-- link: ").Append(Plain(item.LinkFile)).Append(" | ").Append(Plain(item.LinkPage))
                        .Append(" | ").Append(Plain(item.LinkHeading)).Append(" -->\n");
                if (item.IsRepeat && RepeatRe.IsMatch("<!-- repeat: " + item.Repeat + " -->"))
                {
                    text.Append("<!-- repeat: ").Append(item.Repeat).Append(" -->\n");
                    if (item.Skips.Count > 0)
                        text.Append("<!-- skip: ").Append(string.Join(" ", item.Skips.OrderBy(date => date).Select(date => date.ToString(DateFormat, CultureInfo.InvariantCulture)))).Append(" -->\n");
                    if (item.Moves.Count > 0)
                        text.Append("<!-- move: ").Append(string.Join(" ", item.Moves.Select(pair => pair.Key.ToString(DateFormat, CultureInfo.InvariantCulture) + ">"
                                                                                                      + pair.Value.ToString(DateFormat, CultureInfo.InvariantCulture)))).Append(" -->\n");
                }

                var memo = (item.Memo ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
                if (memo.Trim().Length == 0) continue;

                text.Append('\n');
                foreach (var line in memo.Split('\n'))
                    text.Append(EscapedRe.IsMatch(line) ? "\\" + line : line).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>link の行に書く文字（1行。コメントの終わり --&gt; は崩す）。</summary>
        private static string Plain(string text)
        {
            return OneLine(text).Replace("-->", "-- >");
        }

        /// <summary>名前は1行（改行は空白にする）。</summary>
        public static string OneLine(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s*[\r\n]+\s*", " ").Trim();
        }

        private static bool TryDate(string text, out DateTime date)
        {
            return DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}

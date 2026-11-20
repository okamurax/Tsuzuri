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

        public ScheduleItem Clone()
        {
            return new ScheduleItem { Start = Start, End = End, Title = Title, Memo = Memo };
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
                memo.Add(EscapedRe.IsMatch(raw) && raw[0] == '\\' ? raw.Substring(1) : raw);
            }

            Finish(current, memo, items);
            return items;
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

                var memo = (item.Memo ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
                if (memo.Trim().Length == 0) continue;

                text.Append('\n');
                foreach (var line in memo.Split('\n'))
                    text.Append(EscapedRe.IsMatch(line) ? "\\" + line : line).Append('\n');
            }

            return text.ToString();
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

        /// <summary>
        /// 打った日付を読む（編集の画面）。「2026-11-21」「2026/11/21」「11/21」「11-21」（年は基準の日の年）。読めなければ false。
        /// </summary>
        public static bool TryReadDate(string text, DateTime reference, out DateTime date)
        {
            date = DateTime.MinValue;
            var match = Regex.Match((text ?? string.Empty).Trim(), @"^(?:(\d{4})[-/.])?(\d{1,2})[-/.](\d{1,2})$");
            if (!match.Success) return false;

            var year = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : reference.Year;
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

            date = new DateTime(year, month, day);
            return true;
        }
    }
}

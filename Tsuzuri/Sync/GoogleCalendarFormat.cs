using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Tsuzuri.Markdown;

namespace Tsuzuri.Sync
{
    /// <summary>
    /// 送る予定（ExportEvent）を Google カレンダー API の形（JSON）にする・まとめて送る（バッチ）の組み立てと読み取り（SPEC 22章「Google へ送る」。2026-11-23）。
    /// 通信はしない（tools/schedule-file で試験する）。通信は GoogleCalendarClient。
    /// </summary>
    public static class GoogleCalendarFormat
    {
        /// <summary>
        /// events.insert の本文。timeZone はカレンダーの時間帯（IANA の名前。Asia/Tokyo など）。時刻付きの予定と、その繰り返しで使う。
        /// toUtc は、現地の日時を UTC にする（時刻付きの繰り返しの UNTIL は UTC で書く決まりのため）。
        /// sendTag は送るたびの印（extendedProperties.private.tsuzuriSend）。送れなかったときに、今回入れた分を探して消すのに使う。
        /// </summary>
        public static string EventJson(ExportEvent e, string timeZone, Func<DateTime, DateTime> toUtc, string sendTag)
        {
            var json = new StringBuilder("{");
            Field(json, "summary", e.Title);
            if (!string.IsNullOrEmpty(e.Description)) Field(json.Append(','), "description", e.Description);
            json.Append(",\"start\":").Append(When(e.AllDay, e.Start, timeZone));
            json.Append(",\"end\":").Append(When(e.AllDay, e.End, timeZone));

            var recurrence = Recurrence(e, timeZone, toUtc);
            if (recurrence.Count > 0)
                json.Append(",\"recurrence\":[").Append(string.Join(",", recurrence.Select(Quote))).Append(']');

            // 通知: 通知のある予定は、始まりの時刻にメール。ほかは知らせない（カレンダーの既定の通知も使わない）
            json.Append(",\"reminders\":{\"useDefault\":false,\"overrides\":[");
            if (e.Remind) json.Append("{\"method\":\"email\",\"minutes\":0}");
            json.Append("]}");
            json.Append(",\"extendedProperties\":{\"private\":{\"tsuzuri\":\"1\",\"tsuzuriSend\":").Append(Quote(sendTag)).Append("}}");
            json.Append('}');
            return json.ToString();
        }

        /// <summary>RRULE と EXDATE の行。繰り返さなければ空。</summary>
        public static List<string> Recurrence(ExportEvent e, string timeZone, Func<DateTime, DateTime> toUtc)
        {
            var lines = new List<string>();
            if (e.Rule == null) return lines;

            var rule = ScheduleRepeat.Parse(e.Rule.ToString());
            var until = rule.Until;
            rule.Until = null;
            var text = "RRULE:" + rule;
            if (until.HasValue)
            {
                // 終日なら日付のまま。時刻付きなら、その日の終わり（現地の 23:59:59）を UTC で（RFC 5545。DTSTART が時刻付きなら UNTIL も時刻付き）
                text += ";UNTIL=" + (e.AllDay
                    ? until.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                    : toUtc(until.Value.Date.AddDays(1).AddSeconds(-1)).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            lines.Add(text);

            if (e.Excluded.Count > 0)
            {
                lines.Add(e.AllDay
                    ? "EXDATE;VALUE=DATE:" + string.Join(",", e.Excluded.Select(date => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                    : "EXDATE;TZID=" + timeZone + ":" + string.Join(",", e.Excluded.Select(date => date.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))));
            }
            return lines;
        }

        private static string When(bool allDay, DateTime value, string timeZone)
        {
            if (allDay) return "{\"date\":" + Quote(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + "}";
            return "{\"dateTime\":" + Quote(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)) + ",\"timeZone\":" + Quote(timeZone) + "}";
        }

        private static StringBuilder Field(StringBuilder json, string name, string value)
        {
            return json.Append(Quote(name)).Append(':').Append(Quote(value ?? string.Empty));
        }

        /// <summary>JSON の文字列。</summary>
        public static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            return text.Append('"').ToString();
        }

        // ---------------------------------------------------------------
        // バッチ（https://www.googleapis.com/batch/calendar/v3。1回に 50 件まで）
        // ---------------------------------------------------------------

        public sealed class BatchPart
        {
            public string Method;   // POST・DELETE
            public string Path;     // /calendar/v3/calendars/{id}/events …
            public string Body;     // JSON（無ければ null）
        }

        public sealed class BatchResult
        {
            public int Status;
            public string Body = string.Empty;
        }

        /// <summary>バッチの本文（multipart/mixed）。Content-ID は part の番号（item0, item1 …）。</summary>
        public static string BatchBody(IList<BatchPart> parts, string boundary)
        {
            var text = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                text.Append("--").Append(boundary).Append("\r\n");
                text.Append("Content-Type: application/http\r\n");
                text.Append("Content-ID: <item").Append(i.ToString(CultureInfo.InvariantCulture)).Append(">\r\n\r\n");
                text.Append(part.Method).Append(' ').Append(part.Path).Append(" HTTP/1.1\r\n");
                if (part.Body != null)
                {
                    text.Append("Content-Type: application/json; charset=UTF-8\r\n\r\n");
                    text.Append(part.Body).Append("\r\n");
                }
                else text.Append("\r\n");
            }
            text.Append("--").Append(boundary).Append("--\r\n");
            return text.ToString();
        }

        /// <summary>
        /// バッチの返事を、part の番号ごとの結果にする（Content-ID の「response-item3」→ 3）。読めない part は入れない（呼び出し側が失敗として扱う）。
        /// contentType は返事の Content-Type（boundary を読む）。
        /// </summary>
        public static Dictionary<int, BatchResult> ParseBatch(string contentType, string body)
        {
            var results = new Dictionary<int, BatchResult>();
            var boundary = BoundaryOf(contentType);
            if (boundary == null || body == null) return results;

            var delimiter = "--" + boundary;
            foreach (var chunk in body.Split(new[] { delimiter }, StringSplitOptions.None))
            {
                var part = chunk.Replace("\r\n", "\n");
                if (part.StartsWith("--", StringComparison.Ordinal)) continue;   // 終わりの印

                // 外側の見出し（Content-Type・Content-ID）と、内側の HTTP の返事（状態の行・見出し・本文）
                var outerEnd = part.IndexOf("\n\n", StringComparison.Ordinal);
                if (outerEnd < 0) continue;
                var outer = part.Substring(0, outerEnd);
                var inner = part.Substring(outerEnd + 2);

                int index;
                if (!TryContentIndex(outer, out index)) continue;

                var statusLineEnd = inner.IndexOf('\n');
                var statusLine = statusLineEnd < 0 ? inner : inner.Substring(0, statusLineEnd);
                var pieces = statusLine.Trim().Split(' ');
                int status;
                if (pieces.Length < 2 || !pieces[0].StartsWith("HTTP/", StringComparison.Ordinal)
                    || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out status)) continue;

                var headersEnd = inner.IndexOf("\n\n", StringComparison.Ordinal);
                var content = headersEnd < 0 ? string.Empty : inner.Substring(headersEnd + 2).Trim();
                results[index] = new BatchResult { Status = status, Body = content };
            }
            return results;
        }

        private static string BoundaryOf(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            foreach (var piece in contentType.Split(';'))
            {
                var pair = piece.Trim();
                if (!pair.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
                return pair.Substring("boundary=".Length).Trim('"');
            }
            return null;
        }

        private static bool TryContentIndex(string headers, out int index)
        {
            index = -1;
            foreach (var line in headers.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon < 0 || !string.Equals(line.Substring(0, colon).Trim(), "Content-ID", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring(colon + 1).Trim().Trim('<', '>');
                var at = value.LastIndexOf("item", StringComparison.Ordinal);
                return at >= 0 && int.TryParse(value.Substring(at + 4), NumberStyles.None, CultureInfo.InvariantCulture, out index);
            }
            return false;
        }
    }
}

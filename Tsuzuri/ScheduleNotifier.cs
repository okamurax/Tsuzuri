using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Forms = System.Windows.Forms;

namespace Tsuzuri
{
    /// <summary>
    /// カレンダーの予定の通知を Windows の通知で出す（2026-11-22。SPEC 22章「通知の時刻」。Google へ送る前に、想定の時刻に鳴るかを手元で確かめるため）。
    /// アプリに1つ。カレンダーを開いていなくても、Tsuzuri が動いている間は保存先を時々読み、時刻になった回を出す。
    /// ・出すのは、前に見た時刻より後で今までの回（ScheduleRepeat.Alarms）。起動より前・Tsuzuri を閉じていた間の分は出さない
    /// ・スリープなどで間が空いたら、その間の分をまとめて出す（遅れたことを添える）
    /// ・通知は通知領域のアイコン（NotifyIcon）の吹き出しで出す。Windows 10・11 では通知（トースト）になり、通知センターにも残る。
    ///   アイコンは Tsuzuri が動いている間ずっと置く（消すと、通知センターに残った通知も消えるため）
    /// ・通知を押すと、Tsuzuri を前に出してカレンダーを開く
    /// </summary>
    internal static class ScheduleNotifier
    {
        /// <summary>保存先を見る間隔。</summary>
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

        /// <summary>これより遅れて出す通知には、遅れたことを添える。</summary>
        private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

        private static DispatcherTimer _timer;
        private static Forms.NotifyIcon _icon;
        private static Func<string> _path;
        private static Action _opened;

        /// <summary>ここまでの通知は出した（または出さないと決めた）。</summary>
        private static DateTime _checkedUntil;

        private static string _readPath;
        private static DateTime _readTime;
        private static long _readLength = -1;
        private static List<ScheduleItem> _items = new List<ScheduleItem>();

        /// <summary>始める。path は保存先（呼ぶたびに今の設定から作る）、opened は通知を押したとき（Tsuzuri を前に出してカレンダーを開く）。</summary>
        public static void Start(Func<string> path, Action opened)
        {
            if (_timer != null) return;
            _path = path;
            _opened = opened;
            _checkedUntil = DateTime.Now;

            try
            {
                _icon = new Forms.NotifyIcon
                {
                    Icon = AppIcon(),
                    Text = "Tsuzuri（カレンダーの通知）",
                    Visible = true,
                };
                _icon.BalloonTipClicked += delegate { Open(); };
                _icon.DoubleClick += delegate { Open(); };
            }
            catch (Exception)
            {
                // 通知領域が使えない（エクスプローラーが落ちているなど）。通知は出せないが、Tsuzuri は続ける
                _icon = null;
                return;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
            _timer.Tick += delegate { Check(); };
            _timer.Start();
        }

        /// <summary>終わる（アプリを閉じるとき）。アイコンを片付けないと、通知領域に抜け殻が残る。</summary>
        public static void Stop()
        {
            if (_timer != null) _timer.Stop();
            _timer = null;
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            _icon = null;
        }

        private static System.Drawing.Icon AppIcon()
        {
            try
            {
                var exe = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch (Exception)
            {
            }
            return System.Drawing.SystemIcons.Information;
        }

        private static void Open()
        {
            try
            {
                if (_opened != null) _opened();
            }
            catch (Exception)
            {
            }
        }

        private static void Check()
        {
            if (_icon == null) return;
            var now = DateTime.Now;
            if (now < _checkedUntil)
            {
                // 時計が戻った（手で直した・時刻の同期）。戻った先から数え直すと同じ通知を2度出すので、出さずに今から数える
                _checkedUntil = now;
                return;
            }

            if (!Read()) return;   // 読めなければ、次に読めたときにその間の分を出す
            var alarms = ScheduleRepeat.Alarms(_items, _checkedUntil, now);
            _checkedUntil = now;
            if (alarms.Count == 0) return;
            Show(alarms, now);
        }

        /// <summary>保存先を読む（変わっていなければ前のまま）。読めなければ false。無ければ予定は無し。</summary>
        private static bool Read()
        {
            string path;
            try
            {
                path = _path != null ? _path() : null;
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _items = new List<ScheduleItem>();
                    _readPath = path;
                    _readLength = -1;
                    return true;
                }
                if (path == _readPath && info.LastWriteTimeUtc == _readTime && info.Length == _readLength) return true;

                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    text = reader.ReadToEnd();
                _items = ScheduleFile.Parse(text);
                _readPath = path;
                _readTime = info.LastWriteTimeUtc;
                _readLength = info.Length;
                return true;
            }
            catch (Exception)
            {
                // 書き換えの途中・共有フォルダが一時的に見えないなど。次の間隔でまた読む
                return false;
            }
        }

        private static void Show(List<ScheduleAlarm> alarms, DateTime now)
        {
            var first = alarms[0];
            string title;
            var text = new StringBuilder();
            if (alarms.Count == 1)
            {
                title = Name(first.Occurrence.Item);
                text.Append(When(first.At, now));
                if (first.Occurrence.Days > 1)
                    text.Append("（").Append(first.Occurrence.Start.ToString("M/d")).Append("〜").Append(first.Occurrence.End.ToString("M/d")).Append("）");
                if (now - first.At > LateAfter) text.Append("\n").Append(Late(first.At, now));
            }
            else
            {
                // 間が空いて何件も溜まったとき・同じ時刻に何件もあるとき。吹き出しは1つにまとめる（続けて出すと前のが消える）
                title = "予定の通知 " + alarms.Count + " 件";
                foreach (var alarm in alarms.Take(5))
                    text.Append(When(alarm.At, now)).Append("  ").Append(Name(alarm.Occurrence.Item)).Append("\n");
                if (alarms.Count > 5) text.Append("ほか ").Append(alarms.Count - 5).Append(" 件\n");
                if (now - first.At > LateAfter) text.Append("（スリープなどで遅れて出しています）");
            }

            try
            {
                // 吹き出しの題は 63 文字・本文は 255 文字まで（それより長いと出ない）
                _icon.ShowBalloonTip(10000, Clip(title, 60), Clip(text.ToString().TrimEnd('\n'), 250), Forms.ToolTipIcon.None);
            }
            catch (Exception)
            {
            }
        }

        private static string Name(ScheduleItem item)
        {
            return string.IsNullOrWhiteSpace(item.Title) ? "（名前のない予定）" : item.Title;
        }

        private static string When(DateTime at, DateTime now)
        {
            return (at.Date == now.Date ? "" : at.ToString("M/d") + " ") + ScheduleFile.NotifyText(at.TimeOfDay);
        }

        private static string Late(DateTime at, DateTime now)
        {
            var minutes = (int)(now - at).TotalMinutes;
            return "（" + (minutes < 60 ? minutes + " 分" : (minutes / 60) + " 時間") + "遅れて出しています。スリープなどのため）";
        }

        private static string Clip(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}

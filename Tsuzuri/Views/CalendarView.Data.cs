using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    // CalendarView の一部: 保存先の読み書き・見張り（外で書き換わったら出し直す）・AI に渡す今の状態（2026-11-21 に CalendarView.cs から分けた）。
    public sealed partial class CalendarView
    {
        // ---------------------------------------------------------------
        // 読み書き
        // ---------------------------------------------------------------

        /// <summary>
        /// 保存先の中身を読む。無ければ空。読めなければ（ほかのソフトが書いている最中など）false。
        /// 読めないときに空として扱うと、次に直したときに空のまま保存して、予定が全部消える（2026-11-21 のレビューで見つけた）。
        /// </summary>
        private bool TryRead(out string text)
        {
            try
            {
                text = File.Exists(_path) ? File.ReadAllText(_path, Encoding.UTF8) : string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                text = null;
                _status("予定を読めませんでした（少しして読み直します。それまで予定は直せません）: " + ex.Message);
                return false;
            }
        }

        /// <summary>初めに読む。読めなければ空のまま出し、直せないようにする（見張りが読めるまで読み直す）。</summary>
        private void Load()
        {
            string text;
            _loadedStamp = Stamp();
            if (!TryRead(out text)) return;
            _book.Replace(text);
            _loadedText = text;
        }

        private DateTime Stamp()
        {
            try
            {
                return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 直す前に呼ぶ。外（Claude・ほかの画面のカレンダー）で書き換わっていたら読み直す（変わったかは中身で決める。更新日時は秒単位の場所もあり、
        /// 同じ秒の書き換えを見落として上書きしたため）。読めなければ false（直さない）。
        /// 読み直したあとは、直す相手を Current で探し直す（前は読み直したら操作ごとやめていて、打った名前や落とした見出しが消えた）。
        /// </summary>
        private bool Sync()
        {
            string text;
            if (!TryRead(out text)) return false;
            _loadedStamp = Stamp();
            if (text != _loadedText) ReloadFrom(text);
            return true;
        }

        /// <summary>直す相手が外で変わった・消えたので、やめたことを知らせる。</summary>
        private void Gone()
        {
            _status("その予定は外で変わったか消えていたので、やめました（読み直した予定で、もう一度してください）");
            Rebuild();
        }

        /// <summary>打っている・欄で直している・つかんでいる最中か（この間は読み直さない・組み直さない）。</summary>
        private bool Busy
        {
            get { return _editorOpen || _newBox != null || _dragItem != null || _selectingDays || _sizingRow != null; }
        }

        private void Save()
        {
            if (_loadedText == null)
            {
                _status("予定を読めていないので、保存しません");   // 読めないまま書くと、読めなかった予定が消える
                return;
            }
            var temp = _path + ".saving";
            try
            {
                var folder = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                var text = _book.Serialize();
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(_path)) PageDocument.ReplaceWith(temp, _path);
                else File.Move(temp, _path);
                _loadedText = text;
                _loadedStamp = Stamp();
            }
            catch (Exception ex)
            {
                _status("予定を保存できませんでした: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 見張る（外で書き換わったら、すぐ出し直す。2026-11-21）
        // ---------------------------------------------------------------

        private void StartWatching()
        {
            _pollTimer.Start();
            if (_watcher != null) return;
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                var watcher = new FileSystemWatcher(folder, Path.GetFileName(_path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (sender, e) => Poke();
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (sender, e) => Poke();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception)
            {
                // 見張れない場所（共有フォルダなど）は、2秒ごとに更新日時を見るだけにする
            }
        }

        private void StopWatching()
        {
            _pollTimer.Stop();
            _reloadTimer.Stop();
            _noticeTimer.Stop();
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        /// <summary>見張りの知らせ（見張りのスレッドから）。画面のスレッドで、少し待ってから読む。</summary>
        private void Poke()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }));
        }

        /// <summary>外で書き換わって読み直したとき。増えた・変わった予定を光らせ、上の段に何件変わったかを出す。</summary>
        private void MarkChanges(List<ScheduleItem> before)
        {
            int removed;
            var added = _book.AddedSince(before, out removed);
            var changed = Math.Min(added.Count, removed);   // 変わった予定は「消えて増えた」に見える。少ない方を「変わった」と数える

            _flash = new HashSet<ScheduleItem>(added);
            _flashStart = DateTime.UtcNow;
            if (added.Count == 0 && removed == 0) return;

            var parts = new List<string>();
            if (added.Count - changed > 0) parts.Add("増えた " + (added.Count - changed));
            if (changed > 0) parts.Add("変わった " + changed);
            if (removed - changed > 0) parts.Add("消えた " + (removed - changed));
            var message = "外で変わりました: " + string.Join("・", parts);

            // 見えていない週で変わった予定があれば、押すとそこへ
            DateTime first, last;
            VisibleRange(out first, out last);
            // 繰り返しの予定は、見えている週に回が無ければ、次の回（無ければ1回目）
            DateTime? outside = null;
            foreach (var item in added)
            {
                if (ScheduleRepeat.Occurrences(item, first, last).Count > 0) continue;
                var next = ScheduleRepeat.Occurrences(item, last.AddDays(1), last.AddDays(800)).Select(occ => (DateTime?)occ.Start).Min() ?? item.Start;
                if (!outside.HasValue || next < outside.Value) outside = next;
            }
            _noticeDate = outside;
            if (outside.HasValue) message += "（" + outside.Value.ToString("M/d", CultureInfo.InvariantCulture) + " へ ›）";

            _notice.Content = message;
            _notice.IsHitTestVisible = outside.HasValue;
            _notice.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        // ---------------------------------------------------------------
        // AI に渡す今の状態（2026-11-21。MainWindow.Ai の AiContext）
        // ---------------------------------------------------------------

        /// <summary>予定の保存先。</summary>
        public string FilePath
        {
            get { return _path; }
        }

        /// <summary>選んでいる日の初め（選んでいなければ null）。</summary>
        public DateTime? SelectedFrom
        {
            get { return _dayAnchor.HasValue && _dayEnd.HasValue ? (_dayAnchor.Value < _dayEnd.Value ? _dayAnchor : _dayEnd) : null; }
        }

        /// <summary>選んでいる日の終わり（その日を含む。選んでいなければ null）。</summary>
        public DateTime? SelectedTo
        {
            get { return _dayAnchor.HasValue && _dayEnd.HasValue ? (_dayAnchor.Value < _dayEnd.Value ? _dayEnd : _dayAnchor) : null; }
        }

        /// <summary>選んでいる予定の写し（選んでいなければ null）。</summary>
        public ScheduleItem SelectedItem
        {
            get { return _selected != null ? _selected.Clone() : null; }
        }

        /// <summary>選んでいる回の始まり・終わりの日（繰り返しの予定なら、その回。動かした回は動かした先）。選んでいなければ null。</summary>
        public Tuple<DateTime, DateTime> SelectedDates
        {
            get
            {
                if (_selected == null) return null;
                if (!_selected.IsRepeat || !_selectedOriginal.HasValue) return Tuple.Create(_selected.Start, _selected.End);
                DateTime start;
                if (!_selected.Moves.TryGetValue(_selectedOriginal.Value, out start)) start = _selectedOriginal.Value;
                return Tuple.Create(start, start.AddDays((_selected.End - _selected.Start).TotalDays));
            }
        }

        /// <summary>見えている日の範囲（いちばん上に見えている週の日曜から、いちばん下に見えている週の土曜まで）。並ぶ前は今週。</summary>
        public void VisibleRange(out DateTime first, out DateTime last)
        {
            first = WeekStart(DateTime.Today);
            last = first.AddDays(6);
            var top = 0.0;
            var found = false;
            foreach (var pair in _rows)
            {
                var height = pair.Value.ActualHeight;
                var bottom = top + height;
                if (height > 0 && bottom > _scroll.VerticalOffset + 1 && top < _scroll.VerticalOffset + _scroll.ViewportHeight - 1)
                {
                    if (!found) first = pair.Key;
                    found = true;
                    last = pair.Key.AddDays(6);
                }
                top = bottom;
            }
        }

        /// <summary>
        /// 保存先が外（Claude など）で書き換わっていたら読み直して出し直す。打ちかけ・欄で直している最中・帯をつかんでいる最中は読み直さない
        /// （そのときは、次に直すときに Sync が読み直す）。
        /// </summary>
        /// <returns>読み直した・読み直す要が無かったら true。打っている最中などで待つなら false（見張りが少し後にまた呼ぶ）。</returns>
        public bool ReloadIfChanged()
        {
            if (Stamp() == _loadedStamp && _loadedText != null) return true;
            if (Busy) return false;

            string text;
            if (!TryRead(out text)) return false;   // 書いている最中など。少しして読み直す
            _loadedStamp = Stamp();
            if (text != _loadedText) ReloadFrom(text);
            return true;
        }

        /// <summary>外で書き換わった中身で出し直す。選んでいた予定は、同じ形の予定があれば選んだままにし、増えた・変わった予定を光らせる。</summary>
        private void ReloadFrom(string text)
        {
            var before = _book.Items;
            var firstRead = _loadedText == null;
            var selectedKey = _selected != null ? ScheduleBook.KeyOf(_selected) : null;
            _book.Replace(text);   // 戻す記録は捨てる（外の書き換えを戻してしまわないため）
            _loadedText = text;
            _selected = _book.Find(null, selectedKey);
            if (!firstRead) MarkChanges(before);
            Rebuild();
        }
    }
}

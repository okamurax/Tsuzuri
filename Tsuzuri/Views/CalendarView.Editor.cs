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
    // CalendarView の一部: 名前と繰り返しを直す欄（2026-11-21 に CalendarView.cs から分けた）。
    public sealed partial class CalendarView
    {
        // ---------------------------------------------------------------
        // 名前と繰り返しを直す欄（日付は帯で直す。メモの欄は 2026-11-21 にやめた。利用者の判断）
        // 繰り返しは、直すといつも全部の回が変わる（利用者の判断。この回だけ変えるのは、帯で動かすか消すだけ）
        // ---------------------------------------------------------------

        private UIElement BuildEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorLayer.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));

            var panel = new StackPanel();
            _editDates.Foreground = Resource("TextMuted");
            _editDates.Margin = new Thickness(0, 0, 0, 10);
            _editDates.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(_editDates);
            _editLabel.Foreground = Resource("TextMuted");
            _editLabel.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(_editLabel);
            panel.Children.Add(_editTitle);

            // 繰り返し: 種類・間隔・曜日・終わりの日
            var repeatLabel = Label("繰り返し（直すと全部の回が変わる）");
            repeatLabel.Margin = new Thickness(0, 12, 0, 4);
            panel.Children.Add(repeatLabel);
            _editRepeat.Style = (Style)Application.Current.FindResource("DarkComboBox");
            _editRepeat.SelectionChanged += delegate { UpdateRepeatFields(); };
            panel.Children.Add(_editRepeat);

            _editIntervalRow.Orientation = Orientation.Horizontal;
            _editIntervalRow.Margin = new Thickness(0, 8, 0, 0);
            _editInterval.Width = 56;
            _editInterval.HorizontalContentAlignment = HorizontalAlignment.Right;
            _editIntervalRow.Children.Add(_editInterval);
            _editIntervalUnit.Margin = new Thickness(8, 0, 0, 0);
            _editIntervalUnit.VerticalAlignment = VerticalAlignment.Center;
            _editIntervalUnit.Foreground = Resource("TextPrimary");
            _editIntervalRow.Children.Add(_editIntervalUnit);
            panel.Children.Add(_editIntervalRow);

            _editWeekDays.Orientation = Orientation.Horizontal;
            _editWeekDays.Margin = new Thickness(0, 8, 0, 0);
            for (var d = 0; d < 7; d++)
            {
                var day = (DayOfWeek)d;
                var chip = new Border
                {
                    Width = 34,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = Japanese.DateTimeFormat.GetShortestDayName(day),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = d == 0 ? SundayText : d == 6 ? SaturdayText : Resource("TextPrimary"),
                    },
                };
                chip.MouseLeftButtonDown += (sender, e) =>
                {
                    e.Handled = true;
                    if (_editing != null && day == _editing.Start.DayOfWeek) return;   // 1回目の曜日は外せない
                    if (!_editDays.Remove(day)) _editDays.Add(day);
                    PaintWeekDays();
                };
                _editDayChips[d] = chip;
                _editWeekDays.Children.Add(chip);
            }
            panel.Children.Add(_editWeekDays);

            _editUntilLabel = Label("終わりの日（空なら無期限。例 2027-03-31・3/31）");
            _editUntilLabel.Margin = new Thickness(0, 8, 0, 4);
            panel.Children.Add(_editUntilLabel);
            panel.Children.Add(_editUntil);

            var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            _editDelete.Click += delegate
            {
                var item = _editing;
                var original = _editingOriginal;
                CloseEditor();
                if (item != null) Delete(item, original);
            };
            DockPanel.SetDock(_editDelete, Dock.Left);
            buttons.Children.Add(_editDelete);
            _editDeleteAll.Click += delegate
            {
                var item = _editing;
                CloseEditor();
                if (item != null) Delete(item, null);
            };
            DockPanel.SetDock(_editDeleteAll, Dock.Left);
            buttons.Children.Add(_editDeleteAll);

            var save = new Button { Content = "保存", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 72 };
            save.Click += delegate { CommitEdit(); };

            // 見出しと結んだ予定は、名前の代わりにリンク先を出し、「見出しへ飛ぶ」も置く（2026-11-21）
            var jump = ToolButton("見出しへ飛ぶ", "文書のその見出しへ（カレンダーは閉じる）");
            jump.Margin = new Thickness(0, 0, 6, 0);
            jump.Click += delegate
            {
                var item = _editing;
                if (!CommitEdit()) return;   // 繰り返しを変えていれば保存してから（終わりの日が読めなければ飛ばない）
                if (item != null) _openLink(item);
            };
            _editJump = jump;

            var cancel = ToolButton("キャンセル", "直さずに閉じる（Esc）");
            cancel.Margin = new Thickness(0, 0, 6, 0);
            cancel.Click += delegate { CloseEditor(); };
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(jump, Dock.Right);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            buttons.Children.Add(jump);
            panel.Children.Add(buttons);

            KeyEventHandler enter = (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitEdit();
            };
            _editTitle.KeyDown += enter;
            _editInterval.KeyDown += enter;
            _editUntil.KeyDown += enter;

            _editorLayer.Children.Add(new Border
            {
                Child = panel,
                Width = 480,
                Padding = new Thickness(18),
                CornerRadius = new CornerRadius(6),
                Background = Resource("ChromeBackground"),
                BorderBrush = Resource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return _editorLayer;
        }

        /// <summary>繰り返しの種類の選び肢（始まりの日から作る。「毎月 24日」「毎月 第4火曜」…）。読めない決まりなら「そのまま」も。</summary>
        private void FillRepeatChoices(ScheduleItem item, bool keep)
        {
            var start = item.Start;
            var nth = (start.Day - 1) / 7 + 1;
            var dayName = Japanese.DateTimeFormat.GetShortestDayName(start.DayOfWeek);
            var rule = ScheduleRepeat.Parse(item.Repeat);
            _editRepeat.Items.Clear();
            if (keep)
                _editRepeat.Items.Add(new ComboBoxItem
                {
                    Content = "そのまま（" + (rule != null ? rule.Describe(start) : "Tsuzuri で読めない決まり: " + item.Repeat) + "）",
                    Tag = "keep",
                });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "なし", Tag = "none" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎日", Tag = "daily" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "平日（月〜金）", Tag = "weekdays" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎週（曜日を選ぶ）", Tag = "weekly" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 " + start.Day + "日", Tag = "monthday" });
            if (nth <= 4) _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 第" + nth + dayName + "曜", Tag = "monthnth" });
            if (start.AddDays(7).Month != start.Month) _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 最終" + dayName + "曜", Tag = "monthlast" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎年 " + start.Month + "月" + start.Day + "日", Tag = "yearly" });
        }

        private string RepeatChoice
        {
            get
            {
                var choice = _editRepeat.SelectedItem as ComboBoxItem;
                return choice != null ? (string)choice.Tag : "none";
            }
        }

        private void SelectRepeatChoice(string tag)
        {
            foreach (ComboBoxItem choice in _editRepeat.Items)
            {
                if ((string)choice.Tag != tag) continue;
                _editRepeat.SelectedItem = choice;
                return;
            }
            _editRepeat.SelectedIndex = 0;
        }

        /// <summary>選んだ種類に合わせて、間隔・曜日・終わりの日を出し入れする。</summary>
        private void UpdateRepeatFields()
        {
            var choice = RepeatChoice;
            var repeats = choice != "none" && choice != "keep";
            _editIntervalRow.Visibility = repeats && choice != "weekdays" ? Visibility.Visible : Visibility.Collapsed;
            _editIntervalUnit.Text = choice == "daily" ? "日ごと" : choice == "weekly" ? "週ごと" : choice == "yearly" ? "年ごと" : "か月ごと";
            _editWeekDays.Visibility = choice == "weekly" ? Visibility.Visible : Visibility.Collapsed;
            _editUntilLabel.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            _editUntil.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            PaintWeekDays();
        }

        /// <summary>
        /// 曜日のボタンを塗る。1回目の曜日は、いつも選んだことにして薄く出し、押せなくする（2026-11-21。利用者の判断。
        /// 1回目はいつも回に数えるので、その曜日を外すと、外した曜日に1回だけ出てしまうため）。
        /// </summary>
        private void PaintWeekDays()
        {
            var first = _editing != null ? _editing.Start.DayOfWeek : (DayOfWeek?)null;
            if (first.HasValue && !_editDays.Contains(first.Value)) _editDays.Add(first.Value);
            for (var d = 0; d < 7; d++)
            {
                var on = _editDays.Contains((DayOfWeek)d);
                var locked = first.HasValue && (DayOfWeek)d == first.Value;
                var chip = _editDayChips[d];
                chip.Background = on ? Resource("AccentFill") : Brushes.Transparent;
                chip.BorderBrush = locked ? Resource("InputBorder") : on ? TodayEdge : Resource("InputBorder");
                chip.Opacity = locked ? 0.45 : 1;
                chip.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
                chip.ToolTip = locked ? "1回目の曜日（外せない）" : null;
            }
        }

        private void OpenEditor(ScheduleItem item, DateTime? original, DateTime shownStart, DateTime shownEnd)
        {
            CommitNew();
            _editing = item;
            _editingOriginal = original;

            var rule = ScheduleRepeat.Parse(item.Repeat);
            var dates = Dates(shownStart, shownEnd);
            if (item.IsRepeat)
                dates += "\n繰り返し: " + (rule != null ? rule.Describe(item.Start) : item.Repeat) + "（" + item.Start.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + " から）"
                         + (original.HasValue && original.Value != shownStart ? "\nこの回は " + original.Value.ToString("M/d (ddd)", Japanese) + " から動かした" : string.Empty);
            _editDates.Text = dates;

            if (item.IsLinked)
            {
                // 名前は見出しの文字のまま（直せない）。代わりにリンク先を出す（選んで写せる）
                _editLabel.Text = Found(item) ? "リンク先" : "リンク先（見出しが見つかりません。名前を変えたか消した）";
                _editTitle.Text = LinkAddress(item);
                _editTitle.IsReadOnly = true;
            }
            else
            {
                _editLabel.Text = "名前（時間もここに書く）";
                _editTitle.Text = item.Title;
                _editTitle.IsReadOnly = false;
            }
            _editJump.Visibility = item.IsLinked ? Visibility.Visible : Visibility.Collapsed;

            // 繰り返し。欄の選び肢で同じ決まりを作れないもの（読めない決まり・Claude が書いた「始まりの日と違う日付の毎月」など）は「そのまま」を選んでおく
            // （選び肢に当てはめて保存すると、決まりが変わってしまうため）
            _editDays.Clear();
            _editInterval.Text = rule != null ? rule.Interval.ToString(CultureInfo.InvariantCulture) : "1";
            _editUntil.Text = rule != null && rule.Until.HasValue ? rule.Until.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
            string tag;
            if (!item.IsRepeat) tag = "none";
            else if (rule == null) tag = "keep";
            else
            {
                var start = item.Start;
                var nth = (start.Day - 1) / 7 + 1;
                switch (rule.Kind)
                {
                    case RepeatKind.Daily: tag = "daily"; break;
                    case RepeatKind.Weekly:
                        _editDays.AddRange(rule.WeekDays.Count > 0 ? rule.WeekDays : new List<DayOfWeek> { start.DayOfWeek });
                        var weekdays = rule.Interval == 1 && _editDays.Count == 5 && !_editDays.Contains(DayOfWeek.Sunday) && !_editDays.Contains(DayOfWeek.Saturday);
                        tag = weekdays ? "weekdays" : "weekly";
                        break;
                    case RepeatKind.MonthlyDay:
                        tag = rule.MonthDay == 0 || rule.MonthDay == start.Day ? "monthday" : "keep";
                        break;
                    case RepeatKind.MonthlyNth:
                        if (rule.NthDay != start.DayOfWeek) tag = "keep";
                        else if (rule.Nth < 0) tag = start.AddDays(7).Month != start.Month ? "monthlast" : "keep";
                        else tag = rule.Nth == nth && nth <= 4 ? "monthnth" : "keep";
                        break;
                    default: tag = "yearly"; break;
                }
            }
            FillRepeatChoices(item, tag == "keep");
            SelectRepeatChoice(tag);
            if (_editDays.Count == 0) _editDays.Add(item.Start.DayOfWeek);
            UpdateRepeatFields();

            // 消す: 繰り返しなら「この回だけ」と「全部」
            _editDelete.Content = item.IsRepeat && original.HasValue ? "この回だけ消す" : "削除";
            _editDeleteAll.Visibility = item.IsRepeat && original.HasValue ? Visibility.Visible : Visibility.Collapsed;

            _editorLayer.Visibility = Visibility.Visible;
            _editorOpen = true;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                _editTitle.Focus();
                if (!_editTitle.IsReadOnly) _editTitle.SelectAll();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>見出しと結んだ予定のリンク先（ファイル › ページ › 見出し。ページが無ければ省く）。</summary>
        private static string LinkAddress(ScheduleItem item)
        {
            var address = item.LinkFile;
            if (!string.IsNullOrEmpty(item.LinkPage)) address += " › " + item.LinkPage;
            return address + " › " + item.LinkHeading;
        }

        private void CloseEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorOpen = false;
            _editing = null;
            _editingOriginal = null;
            Focus();
        }

        /// <summary>
        /// 欄の繰り返しを、保存先に書く決まりにする。なしなら null。読めない決まりを「そのまま」なら元の決まり。
        /// 終わりの日が読めなければ false（欄は閉じない）。
        /// </summary>
        private bool ReadRepeat(ScheduleItem item, out string repeat)
        {
            repeat = null;
            var start = item.Start;
            var choice = RepeatChoice;
            if (choice == "none") return true;
            if (choice == "keep")
            {
                repeat = item.Repeat;
                return true;
            }

            int interval;
            if (!int.TryParse(_editInterval.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 999) interval = 1;

            DateTime? until = null;
            var untilText = _editUntil.Text.Trim();
            if (untilText.Length > 0)
            {
                DateTime parsed;
                if (!TryReadDate(untilText, start, out parsed))
                {
                    _status("終わりの日を読めませんでした（例 2027-03-31・2027/3/31・3/31）: " + untilText);
                    return false;
                }
                until = parsed;
            }

            var rule = new ScheduleRepeat { Interval = interval, Until = until };
            switch (choice)
            {
                case "daily": rule.Kind = RepeatKind.Daily; break;
                case "weekdays":
                    rule.Kind = RepeatKind.Weekly;
                    rule.Interval = 1;
                    rule.WeekDays = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
                    break;
                case "weekly":
                    rule.Kind = RepeatKind.Weekly;
                    rule.WeekDays = _editDays.Count > 0 ? _editDays.OrderBy(day => day).ToList() : new List<DayOfWeek> { start.DayOfWeek };
                    break;
                case "monthday":
                    rule.Kind = RepeatKind.MonthlyDay;
                    rule.MonthDay = start.Day;
                    break;
                case "monthnth":
                    rule.Kind = RepeatKind.MonthlyNth;
                    rule.Nth = (start.Day - 1) / 7 + 1;
                    rule.NthDay = start.DayOfWeek;
                    break;
                case "monthlast":
                    rule.Kind = RepeatKind.MonthlyNth;
                    rule.Nth = -1;
                    rule.NthDay = start.DayOfWeek;
                    break;
                default:
                    rule.Kind = RepeatKind.Yearly;
                    break;
            }

            // 毎週は1回目の曜日をいつも含む（欄で外せない。PaintWeekDays）
            if (rule.Kind == RepeatKind.Weekly && !rule.WeekDays.Contains(start.DayOfWeek))
            {
                rule.WeekDays.Add(start.DayOfWeek);
                rule.WeekDays.Sort();
            }

            if (until.HasValue && until.Value < start)
            {
                _status("終わりの日が始まりの日（" + start.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + "）より前です");
                return false;
            }
            repeat = rule.ToString();
            return true;
        }

        /// <summary>終わりの日の欄（2027-03-31・2027/3/31・3/31）。年が無ければ、始まりの日以降で最初のその月日。</summary>
        private static bool TryReadDate(string text, DateTime start, out DateTime date)
        {
            var formats = new[] { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
            DateTime monthDay;
            if (DateTime.TryParseExact(text, new[] { "M/d", "M-d", "M.d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out monthDay))
            {
                try
                {
                    date = new DateTime(start.Year, monthDay.Month, monthDay.Day);
                    if (date < start) date = date.AddYears(1);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 打ちかけを保存する（カレンダー・画面を閉じる前。MainWindow.Calendar.cs）。マスで打っている名前は足し、欄が開いていれば欄の中身を保存して閉じる。
        /// 終わりの日を読めなければ、欄は開いたまま false（閉じるのをやめる）。
        /// </summary>
        public bool CommitEdit()
        {
            CommitNew();
            if (!_editorOpen) return true;

            var editing = _editing;
            if (editing == null)
            {
                CloseEditor();
                return true;
            }

            string repeat;
            if (!ReadRepeat(editing, out repeat)) return false;
            var title = _editTitle.Text;   // 結んだ予定の欄に出しているのはリンク先だが、Edit が名前を見出しの文字のままにする
            CloseEditor();
            var key = ScheduleBook.KeyOf(editing);
            if (!Sync()) return true;

            var target = _book.Find(editing, key);
            if (target == null)
            {
                Gone();
                return true;
            }
            if (!_book.Edit(target, title, repeat)) return true;

            _selected = target;
            if (!target.IsRepeat) _selectedOriginal = null;
            Save();
            Rebuild();
            return true;
        }
    }
}

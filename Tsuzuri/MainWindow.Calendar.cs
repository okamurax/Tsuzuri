using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: カレンダー（SPEC 22章。2026-11-20）。
    // 予定を月か2週で並べ、カレンダーの画面で直接打つ。保存先は .md（ScheduleFile）で、裏に置くだけ（利用者の考え）。
    // ランチャーと同じく、本文の場所いっぱいに重ねて出し、画面のタブの並びの一番右（ランチャーの右）のタブで出し入れする。
    // ランチャーとは同時に出さない（片方を出すと、もう片方を閉じる）。
    public partial class MainWindow
    {
        private CalendarView _calendar;
        private Button _calendarButton;

        /// <summary>予定の保存先。app.json の scheduleFile。無ければ app.json の隣の 予定.md。</summary>
        private string ScheduleFilePath
        {
            get
            {
                var path = _settings.ScheduleFile;
                if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path.Trim());
                return Path.Combine(Path.GetDirectoryName(AppSettings.StorePath) ?? ".", "予定.md");
            }
        }

        /// <summary>「カレンダー」のタブ。見た目はランチャーのタブと同じ（App.xaml の LauncherTabButton）。Alt+C でも。</summary>
        private Button CalendarButton
        {
            get
            {
                if (_calendarButton != null) return _calendarButton;
                _calendarButton = new Button
                {
                    Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    Style = (Style)FindResource("LauncherTabButton"),
                    Width = 34,
                    Height = 26,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "カレンダー（Alt+C）。予定を月か2週で出し、ここで直接打つ。もう一度押すか Esc で閉じる",
                };
                _calendarButton.Click += delegate { ToggleCalendar(); };
                AccessKeyManager.Register("C", _calendarButton);
                return _calendarButton;
            }
        }

        private void ToggleCalendar()
        {
            if (_calendar != null)
            {
                CloseCalendar();
                return;
            }

            CloseLauncher();
            _calendar = new CalendarView(ScheduleFilePath, _settings, Status);
            CalendarHost.Child = _calendar;
            CalendarHost.Visibility = Visibility.Visible;
            CalendarButton.Tag = "Open";

            // 覆われた本文に打ち込まないよう、キー入力をカレンダーへ移す（ランチャーと同じ）
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_calendar != null && !CalendarHost.IsKeyboardFocusWithin) _calendar.Focus();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// カレンダーを閉じる。予定を足す・直す欄が開いていれば、先に保存する。読めない日付などで保存できなければ閉じない（打った文字を捨てない）。
        /// 閉じたら true。
        /// </summary>
        private bool CloseCalendar()
        {
            if (_calendar == null) return true;
            if (!_calendar.CommitEdit())
            {
                Status("予定の欄に直すところがあります。直して保存するか、「やめる」を押してください");
                return false;
            }

            var hadFocus = CalendarHost.IsKeyboardFocusWithin;
            _calendar = null;
            CalendarHost.Child = null;
            CalendarHost.Visibility = Visibility.Collapsed;
            CalendarButton.Tag = null;
            if (hadFocus && Active != null) Active.TextArea.Focus();
            return true;
        }

        /// <summary>カレンダーを出している間の Esc で閉じる。欄を閉じる・選ぶのをやめる Esc は、カレンダーが受け取る（Handled）ので、ここには来ない。</summary>
        private void CalendarHost_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _calendar == null) return;
            e.Handled = true;
            CloseCalendar();
        }
    }
}

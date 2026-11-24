using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Tsuzuri
{
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // 本文の左端のつまみ（2026-11-24。利用者の考え。SPEC 22章・4章）
        //
        // ルーズリーフのインデックスのように、本文の左端（アウトラインとの境）の細い帯から、カレンダーとランチャーのつまみを縦に並べて張り出す。
        // どちらも押すたびに開け閉めし、外を押しても Esc でも閉じない（ランチャーもカレンダーに合わせた）。出しているもののつまみは色を付ける。
        // 「見出し」とは呼ばない（文書の見出し・アウトラインの見出しと取り違えるため。レビューの指摘）。
        // 前は、カレンダーは帯の真ん中の持ち手（2026-11-23）、ランチャーは画面のタブの並びの一番右のボタンだった。
        // ---------------------------------------------------------------

        private void CalendarTab_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount > 1) return;   // つい2度押しても、開いてすぐ閉じない
            ToggleCalendar();
        }

        private void LauncherTab_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount > 1) return;
            ToggleLauncher();
        }

        private void SideTab_Hover(object sender, MouseEventArgs e)
        {
            PaintSideTabs();
        }

        /// <summary>つまみの色と説明。出しているものは選んだ色、マウスが載ればその色。カレンダーがランチャーの下に隠れているときは、見えているほう（ランチャー）だけを選んだ色にする。
        /// ランチャーを出している間は、つまみの間・上下のすき間をふさぐ（LauncherGap）。</summary>
        private void PaintSideTabs()
        {
            var launcherOpen = _launcher != null;
            LauncherGap.Visibility = launcherOpen ? Visibility.Visible : Visibility.Collapsed;
            var calendarShown = _calendar != null && !launcherOpen;
            PaintSideTab(CalendarTab, CalendarTabShape, CalendarTabIcon, calendarShown);
            PaintSideTab(LauncherTab, LauncherTabShape, LauncherTabIcon, launcherOpen);

            CalendarTab.ToolTip = _calendar == null ? "カレンダーを出す（Alt+C）。予定を週の行で下へずっと並べ、ここで直接打つ"
                : launcherOpen ? "カレンダーに戻る（ランチャーを閉じる。Alt+C）"
                : "カレンダーを閉じる（Alt+C）";
            LauncherTab.ToolTip = launcherOpen ? "ランチャーを閉じる（Alt+L）" : "ランチャーを出す（Alt+L）。ランチャーとファイラーを本文の場所に重ねて出す";
        }

        private void PaintSideTab(Grid tab, Path shape, TextBlock icon, bool open)
        {
            var hover = tab.IsMouseOver;
            shape.Fill = (Brush)FindResource(open ? "AccentFill" : hover ? "HoverFill" : "SidebarBackground");
            shape.Stroke = (Brush)FindResource(open ? "AccentBrush" : "BorderBrushSoft");
            icon.Foreground = (Brush)FindResource(open ? "AccentText" : hover ? "AccentBrush" : "TextMuted");
        }
    }
}

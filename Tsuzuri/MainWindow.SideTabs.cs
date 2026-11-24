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
        // 本文の左下のつまみ（2026-11-24。利用者の考え。SPEC 22章・4章）
        //
        // カレンダーとランチャーのアイコンのボタンを縦に2つ並べた、浮いた小さな板を本文の左下に重ねる。見た目は Mattermost の左のチームの切り替えに寄せる。
        // 経緯（同じ日）: 左端の細い帯の真ん中から 14px 幅のつまみ（アイコンが見えなかった）→ 左下に重ねる面取りのつまみ（形がしっくりこない）→ 今の形。
        // どちらも押すたびに開け閉めし、外を押しても Esc でも閉じない（ランチャーもカレンダーに合わせた）。出しているもののつまみは色を付ける。
        // 2つは別々に開け閉めし、両方出せば後から出したほうが手前に重なる。手前を閉じれば奥のほうが見える。つまみの色はそれぞれ自分を出しているかだけで決め、
        // 相手に隠れていても出していれば色を付ける（2026-11-24。利用者の考え。前は隠れたカレンダーのつまみを押すとランチャーを閉じていた）。
        // 「見出し」とは呼ばない（文書の見出し・アウトラインの見出しと取り違えるため。レビューの指摘）。
        // 前は、カレンダーは帯の真ん中の持ち手（2026-11-23）、ランチャーは画面のタブの並びの一番右のボタンだった。
        // ---------------------------------------------------------------

        /// <summary>カレンダーとランチャーを両方出していて、カレンダーを後から出した（カレンダーが上）。</summary>
        private bool _calendarAboveLauncher;

        /// <summary>カレンダーが見えている（出していて、ランチャーの奥に隠れていない）。</summary>
        private bool CalendarOnTop
        {
            get { return _calendar != null && (_launcher == null || _calendarAboveLauncher); }
        }

        /// <summary>
        /// カレンダーとランチャーの重なりの順。カレンダーは本文の場所（StackHost）の中にあるので、カレンダーを上にするときは StackHost ごとランチャー（ZIndex 3）の上へ出す。
        /// カレンダーと図エディタは同時に出さない（図エディタを出すとカレンダーは閉じ、図エディタの間は出せない）ので、図エディタの上にかかることはない。
        /// </summary>
        private void StackOverlays()
        {
            Panel.SetZIndex(StackHost, _calendar != null && _launcher != null && _calendarAboveLauncher ? 4 : 0);
        }

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

        /// <summary>つまみの色と説明。出しているものは選んだ色、マウスが載ればその色。それぞれ自分を出しているかだけで決める（相手の下に隠れていても、出していれば選んだ色）。</summary>
        private void PaintSideTabs()
        {
            var launcherOpen = _launcher != null;

            // 図エディタを出している間は、図エディタの左端（窓の左端。アウトラインは覆われている）の下へ。本文の列のままだと、キャンバスの中ほどにかかる（レビューの指摘）。
            // ランチャーを図エディタの上に出したときは、ランチャーの左下へ戻す
            Grid.SetColumn(SideTabs, _drawing != null && !launcherOpen ? 0 : 2);
            PaintSideTab(CalendarTab, CalendarTabBack, CalendarTabBar, CalendarTabIcon, _calendar != null);
            PaintSideTab(LauncherTab, LauncherTabBack, LauncherTabBar, LauncherTabIcon, launcherOpen);

            CalendarTab.ToolTip = _calendar == null ? "カレンダーを出す（Alt+C）。予定を週の行で下へずっと並べ、ここで直接打つ"
                : "カレンダーを閉じる（Alt+C）";
            LauncherTab.ToolTip = launcherOpen ? "ランチャーを閉じる（Alt+L）" : "ランチャーを出す（Alt+L）。ランチャーとファイラーを本文の場所に重ねて出す";
        }

        /// <summary>ふだんは地なしで薄い字、載せると地を付けて白、出しているものは青い地（AccentFill）に青いアイコンと左の縁の棒（Mattermost に寄せる）。</summary>
        private void PaintSideTab(Grid tab, Border back, Border bar, TextBlock icon, bool open)
        {
            var hover = tab.IsMouseOver;
            back.Background = open ? (Brush)FindResource(hover ? "AccentFillHover" : "AccentFill")
                : hover ? (Brush)FindResource("HoverFill") : Brushes.Transparent;
            bar.Visibility = open ? Visibility.Visible : Visibility.Hidden;
            icon.Foreground = (Brush)FindResource(open ? "AccentText" : hover ? "SidebarTextActive" : "SidebarText");
        }
    }
}

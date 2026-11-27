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
        // カレンダー・ランチャー・COM のエクスプローラのアイコンのボタンを縦に3つ並べた、浮いた小さな板を本文の左下に重ねる。見た目は Mattermost の左のチームの切り替えに寄せる。
        // 経緯（同じ日）: 左端の細い帯の真ん中から 14px 幅のつまみ（アイコンが見えなかった）→ 左下に重ねる面取りのつまみ（形がしっくりこない）→ 今の形。
        // どちらも押すたびに開け閉めし、外を押しても Esc でも閉じない（ランチャーもカレンダーに合わせた）。出しているもののつまみは色を付ける。
        // カレンダーとランチャーはいつもどちらか一方だけ（2026-11-26。前は別々に開け閉めして重ねていた）。3つ目の COM のエクスプローラ（2026-11-27）は本文の右に並ぶので、どちらとも一緒に出せる。
        // つまみの色はそれぞれ自分を出しているかだけで決める。
        // 「見出し」とは呼ばない（文書の見出し・アウトラインの見出しと取り違えるため。レビューの指摘）。
        // 前は、カレンダーは帯の真ん中の持ち手（2026-11-23）、ランチャーは画面のタブの並びの一番右のボタンだった。
        // ---------------------------------------------------------------

        /// <summary>カレンダーとランチャーを両方出していて、カレンダーを後から出した（カレンダーが上）。</summary>
        private bool _calendarAboveLauncher;

        /// <summary>カレンダーが見えている（ランチャーとはどちらか一方だけなので、出していれば見えている）。</summary>
        private bool CalendarOnTop
        {
            get { return _calendar != null; }   // カレンダーとランチャーはどちらか一方だけなので（2026-11-26）、出していれば見えている
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

            // 図エディタが見えている間は隠す（2026-11-25。利用者の判断。描くのに邪魔なので）。カレンダーは図エディタの間は出せず、ランチャーは Alt+L で出せる。
            // ランチャーを図エディタの上に出したときは、ランチャーを押して閉じられるように出す（ランチャーの左下）。
            // 前は、図エディタの間は窓の左端の下へ移していた（本文の列のままだと、キャンバスの中ほどにかかるため）
            SideTabs.Visibility = _drawing != null && !launcherOpen ? Visibility.Collapsed : Visibility.Visible;
            PaintSideTab(CalendarTab, CalendarTabBack, CalendarTabBar, CalendarTabIcon, _calendar != null);
            PaintSideTab(LauncherTab, LauncherTabBack, LauncherTabBar, LauncherTabIcon, launcherOpen);
            PaintSideTab(ComTab, ComTabBack, ComTabBar, ComTabIcon, ComExplorerOpen);

            CalendarTab.ToolTip = _calendar == null ? "カレンダーを出す（Alt+C）。予定を週の行で下へずっと並べ、ここで直接打つ"
                : "カレンダーを閉じる（Alt+C）";
            LauncherTab.ToolTip = launcherOpen ? "ランチャーを閉じる（Alt+L）" : "ランチャーを出す（Alt+L）。ランチャーとファイラーを本文の場所に重ねて出す";
            ComTab.ToolTip = ComExplorerOpen ? "COM のエクスプローラを閉じる（Alt+X）" : "COM のエクスプローラを出す（Alt+X）。開いている Excel と Outlook の一覧を、本文の右に並べて出す";
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

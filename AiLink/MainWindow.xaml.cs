using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace AiLink
{
    /// <summary>
    /// AI連携ツールの窓（1つだけ）。左の列に開いている Excel の一覧、右に AI の欄（PORTING.md）。
    /// 部分ごとのファイル: MainWindow.Ai*.cs（AI の欄）、MainWindow.Excel.cs（Excel）。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;

        /// <summary>知らせをしばらく出したら消す。</summary>
        private readonly DispatcherTimer _statusReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };

        public MainWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            RestoreWindowBounds();

            SidebarColumn.Width = new GridLength(_settings.SidebarWidth);

            _statusReset.Tick += delegate
            {
                _statusReset.Stop();
                StatusText.Text = string.Empty;
            };

            Closing += OnClosing;

            // 会話のタブは窓が出てから開く（端末（WebView2）は窓ができてから動く）。Excel の一覧も読む
            Loaded += delegate
            {
                StartAi();
                RefreshExcelList();
            };
            // ほかのアプリ（Excel・AI を入れた PowerShell など）から戻ったら、Excel の一覧を読み直す（ブックを開いた・閉じた・保存した）
            Activated += delegate
            {
                if (!IsLoaded) return;
                RefreshExcelList();
                RecheckAgent();   // AI の入れ方の案内を出していれば、入ったかを探し直す
            };
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            StoreWindowBounds();
            StoreLayout();
            _settings.Save();
            StopAi();
        }

        /// <summary>ステータスバーに知らせを出す（しばらくすると消える）。</summary>
        private void Status(string message)
        {
            StatusText.Text = message;
            _statusReset.Stop();
            _statusReset.Start();
        }

        /// <summary>前回の位置と大きさで出す。モニターを外したなどで画面の外に出てしまう位置なら、位置は使わず既定の場所に出す。</summary>
        private void RestoreWindowBounds()
        {
            if (_settings.WindowWidth <= 0) return;
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;

            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var grip = new Rect(_settings.WindowLeft, _settings.WindowTop, Width, 40);   // 上端（つかんで動かす段）が画面に 100px 以上かかっていれば使う
            grip.Intersect(screen);
            if (!grip.IsEmpty && grip.Width >= 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        }

        /// <summary>最大化しているときは、最大化する前の位置と大きさを覚える。最小化は覚えない。</summary>
        private void StoreWindowBounds()
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty || bounds.Width < 640 || bounds.Height < 400) return;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
        }

        /// <summary>上端の段をドラッグすると窓を動かし、ダブルクリックすると最大化と元の大きさを切り替える（タイトルバーの代わり）。</summary>
        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (WindowState == WindowState.Normal) DragMove();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>仕切りを動かした。左の列の幅を覚える。</summary>
        private void Layout_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            StoreLayout();
            _settings.Save();
        }

        private void StoreLayout()
        {
            if (SidebarColumn.ActualWidth >= 180) _settings.SidebarWidth = SidebarColumn.ActualWidth;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tsuzuri.Views
{
    /// <summary>
    /// Excel の一覧と Outlook の一覧を、タブで切り替えて出す（2026-11-26。利用者の判断。Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする）。
    /// 上の段の左に「Excel」「Outlook」のタブ、右に今出している一覧の「更新」。それぞれの一覧の見出しの段は外して、ここに置く（DetachHead）。
    /// 選んでいるシート・フォルダは、出していないほうも覚えたまま（タブは見た目を切り替えるだけ）。初めは Excel。
    /// </summary>
    public sealed class OfficeTabs : DockPanel
    {
        private readonly UIElement[] _views;
        private readonly Button[] _refresh;
        private readonly TextBlock[] _tabs;
        private int _current = -1;

        /// <summary>タブを切り替えた（0 = Excel、1 = Outlook）。</summary>
        public event Action<int> Switched;

        public OfficeTabs(ExcelListView excel, OutlookListView outlook)
        {
            _views = new UIElement[] { excel, outlook };
            _refresh = new[] { excel.DetachHead(), outlook.DetachHead() };
            _tabs = new[] { Tab("Excel", 0), Tab("Outlook", 1) };

            var strip = new DockPanel { Margin = new Thickness(6, 0, 6, 0), Height = 30 };
            foreach (var button in _refresh)
            {
                DockPanel.SetDock(button, Dock.Right);
                strip.Children.Add(button);
            }
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var tab in _tabs) tabs.Children.Add((Border)tab.Parent);
            strip.Children.Add(tabs);
            var line = new Border { Child = strip, BorderBrush = Brush("BorderBrushSoft"), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(line, Dock.Top);
            Children.Add(line);

            var body = new Grid();
            body.Children.Add(excel);
            body.Children.Add(outlook);
            Children.Add(body);

            Select(0);
        }

        /// <summary>今出しているタブ（0 = Excel、1 = Outlook）。</summary>
        public int Current
        {
            get { return _current; }
        }

        public void Select(int index)
        {
            if (index == _current) return;
            _current = index;
            for (var i = 0; i < _views.Length; i++)
            {
                var shown = i == index;
                _views[i].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                _refresh[i].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                _tabs[i].Foreground = shown ? Brush("TextPrimary") : Brush("TextMuted");
                _tabs[i].FontWeight = shown ? FontWeights.SemiBold : FontWeights.Normal;
                _tabs[i].TextDecorations = null;
                ((Border)_tabs[i].Parent).BorderBrush = shown ? Brush("AccentBrush") : Brushes.Transparent;
            }
            var switched = Switched;
            if (switched != null) switched(index);
        }

        private TextBlock Tab(string label, int index)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Child = text,
                Padding = new Thickness(8, 0, 8, 0),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = label + " の一覧を出す（選んでいるものは、出していないほうも覚えたまま）",
            };
            border.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                Select(index);
            };
            return text;
        }

        private static Brush Brush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Gray;
        }
    }
}

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiLink.Agents;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: AI の会話ごとの「プロジェクトフォルダ」（Tsuzuri の MainWindow.AiPin.cs のプロジェクトフォルダの段を写したもの。2026-11-25）。
    //
    // AI の欄の下端（渡した状態の行の下）に1段置き、選んでいる会話のタブの分を出す。タブごとに持ち、次の起動でも戻す（AiTabState）。
    // 入れ方: 右の「選ぶ…」（フォルダを選ぶ画面）・欄に打つ・エクスプローラーから落とす。
    // 第3段で、話しかけるたびに今の状態の頭に1行で書く（入っていなくても「指定なし」と毎回）。
    public partial class MainWindow
    {
        private Grid _aiProjectRow;
        private TextBox _aiProjectBox;

        /// <summary>タブを切り替えて欄に入れ直している最中（そのときの TextChanged はタブに書かない）。</summary>
        private bool _aiProjectRefreshing;

        private const string AiProjectHint = "空なら「指定なし」と伝える。右の「選ぶ…」で選ぶ（打つ・エクスプローラーからフォルダを落とすのでも入る）";

        /// <summary>下端の段を作る（初めて要るとき）。渡した状態の行（AiContextText）のすぐ下に入れる。</summary>
        private void EnsureAiProjectRow()
        {
            if (_aiProjectRow != null) return;
            var parent = AiContextText.Parent as Panel;
            if (parent == null) return;

            _aiProjectRow = new Grid { Margin = new Thickness(12, 0, 8, 4) };
            _aiProjectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _aiProjectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _aiProjectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "プロジェクトフォルダ",
                FontSize = 12,
                Foreground = TryFindResource("TextMuted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "この会話で扱うプロジェクトのフォルダ。入れておくと、話しかけるたびに AI に伝える",
            };
            _aiProjectRow.Children.Add(label);

            _aiProjectBox = new TextBox
            {
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
                AllowDrop = true,
                ToolTip = AiProjectHint,
                // 暗い地に合わせる（白い既定の欄にしない。地は透かして下の線だけ）
                Background = Brushes.Transparent,
                BorderBrush = TryFindResource("BorderBrushSoft") as Brush ?? Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CaretBrush = TryFindResource("TextPrimary") as Brush ?? Brushes.White,
            };
            Grid.SetColumn(_aiProjectBox, 1);
            _aiProjectRow.Children.Add(_aiProjectBox);

            _aiProjectBox.TextChanged += delegate
            {
                if (_aiProjectRefreshing || _aiTab == null) return;
                var text = _aiProjectBox.Text.Trim().Trim('"');
                _aiTab.Project = text.Length == 0 ? null : text;
                ShowAiProjectState();
            };
            _aiProjectBox.LostKeyboardFocus += delegate { StoreAiTabs(); };
            _aiProjectBox.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                StoreAiTabs();
                if (_aiTab != null) FocusAiTerminal(_aiTab);
            };
            _aiProjectBox.PreviewDragOver += (sender, e) =>
            {
                e.Effects = DroppedFolder(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            _aiProjectBox.PreviewDrop += (sender, e) =>
            {
                var folder = DroppedFolder(e.Data);
                if (folder == null) return;
                e.Handled = true;
                _aiProjectBox.Text = folder;
                StoreAiTabs();
            };

            var choose = new Button
            {
                Content = "選ぶ…",
                ToolTip = "フォルダを選ぶ画面で、この会話のプロジェクトフォルダを選ぶ",
                FontSize = 12,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(6, 2, 0, 2),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var buttonStyle = TryFindResource("ToolButton") as Style;
            if (buttonStyle != null) choose.Style = buttonStyle;
            choose.Click += delegate { ChooseAiProject(); };
            Grid.SetColumn(choose, 2);
            _aiProjectRow.Children.Add(choose);

            parent.Children.Insert(parent.Children.IndexOf(AiContextText) + 1, _aiProjectRow);
        }

        /// <summary>選んでいるタブの分を出す（タブを選んだとき・外で変えたとき）。</summary>
        private void RefreshAiProjectRow()
        {
            EnsureAiProjectRow();
            if (_aiProjectRow == null) return;

            var tab = _aiTab;
            _aiProjectRow.IsEnabled = tab != null;
            _aiProjectRefreshing = true;
            try
            {
                var project = tab != null ? tab.Project ?? string.Empty : string.Empty;
                if (_aiProjectBox.Text != project) _aiProjectBox.Text = project;
            }
            finally
            {
                _aiProjectRefreshing = false;
            }
            ShowAiProjectState();
        }

        /// <summary>欄の見た目（見つからない・AI に渡さない場所なら、ツールチップと色で知らせる）。</summary>
        private void ShowAiProjectState()
        {
            if (_aiProjectBox == null) return;
            var path = _aiTab != null ? _aiTab.Project : null;
            string problem = null;
            if (!string.IsNullOrEmpty(path))
            {
                if (AiGuard.IsGuarded(path)) problem = "AI に渡さない場所（" + AiGuard.MarkerName + " のあるフォルダ）なので、AI には伝えない";
                else if (!DirectoryExists(path)) problem = "このフォルダは見つからない（AI には「見つからない」と添えて伝える）";
            }
            _aiProjectBox.Foreground = problem != null ? Brushes.IndianRed : TryFindResource("TextPrimary") as Brush ?? SystemColors.ControlTextBrush;
            _aiProjectBox.ToolTip = problem ?? AiProjectHint;
        }

        private static bool DirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>落とされたものが1つのフォルダなら、そのパス。</summary>
        private static string DroppedFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length != 1) return null;
            return DirectoryExists(paths[0]) ? paths[0] : null;
        }

        /// <summary>「選ぶ…」: フォルダを選ぶ画面で、選んでいる会話のプロジェクトフォルダを選ぶ。</summary>
        private void ChooseAiProject()
        {
            var tab = _aiTab;
            if (tab == null) return;
            string problem;
            var folder = FolderPicker.Pick(this, "会話 " + tab.Name + " のプロジェクトフォルダ", out problem);
            if (problem != null) Status("フォルダを選べませんでした: " + problem);
            if (folder == null || !ReferenceEquals(tab, _aiTab)) return;
            tab.Project = folder;
            RefreshAiProjectRow();
            StoreAiTabs();
            Status("会話 " + tab.Name + " のプロジェクトフォルダにしました: " + folder);
            FocusAiTerminal(tab);
        }
    }
}

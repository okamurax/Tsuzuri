using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace AiLink
{
    // MainWindow の一部: AI の欄（会話のタブ・今の状態・タブどうしの伝言）。第2段で Tsuzuri の MainWindow.Ai*.cs から写す（PORTING.md）。
    // 今は骨組みのための仮の受け口だけ。
    public partial class MainWindow
    {
        /// <summary>ファイラーで名前を変えたあと、各タブのプロジェクトフォルダを直す。</summary>
        private void OnAiPathRenamed(Func<string, string> remap)
        {
        }

        /// <summary>ファイラーで見ているフォルダを、選んでいる会話のプロジェクトフォルダにする。</summary>
        private void FilerUseFolder_Click(object sender, RoutedEventArgs e)
        {
            Status("まだ作っていません（第2段で写す）");
        }

        private void AiShellSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
        }

        private void AiShellClose_Click(object sender, RoutedEventArgs e)
        {
        }
    }
}

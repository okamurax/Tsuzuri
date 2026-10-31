using System;
using System.Windows;

namespace DrawingEditor
{
    /// <summary>図エディタで保存したもの。描き直すための JSON と、本文に表示する画像（PNG）。</summary>
    public sealed class DrawingResult
    {
        public string Json { get; internal set; }
        public byte[] Png { get; internal set; }
    }

    /// <summary>
    /// エディタ本体（Tsuzuri）から図エディタを使う入口（SPEC 8.4.3。2026-10-18）。
    ///
    /// 図エディタは本体の上に開く WPF のウィンドウで、閉じるまで本体は操作できない（モーダル。表エディタと同じ）。
    /// 図は文書の添付フォルダに、同じ名前の .json（描き直すため）と .png（本文に表示する・Word に載せる）で置く。置くのは本体の役目。
    /// </summary>
    public static class DrawingEditorHost
    {
        /// <summary>この JSON が図エディタの図か（同じ名前の .json が別の物なら、図として開かない）。</summary>
        public static bool IsDrawing(string json)
        {
            return DrawingFile.Read(json) != null;
        }

        /// <summary>
        /// 図を描く・描き直す。閉じるまで戻らない。保存して閉じたら図を、保存せずに閉じたら null を返す。
        /// 新しい版で描いた図は、知らせて開かずに null を返す（2026-10-27）。
        /// </summary>
        /// <param name="owner">本体のウィンドウ。図エディタはその真ん中に出る。無ければ画面の真ん中。</param>
        /// <param name="json">描き直す図（.json の中身）。新しく描くなら null。</param>
        public static DrawingResult Edit(Window owner, string json)
        {
            var file = (json == null ? null : DrawingFile.Read(json)) ?? new DrawingFile();

            // 新しい版の図エディタで描いた図（版が新しい・この版の知らない図形がある）は開かない（2026-10-27）。
            // この版で保存すると、知らない図形がファイルから消えてしまうため。読むだけで開く作りより簡単で、図を壊さない。
            if (file.Version > DrawingFile.SupportedVersion || file.Dropped > 0)
            {
                var message = "この図は、新しい版の図エディタで描かれています"
                    + (file.Dropped > 0 ? "（この版では描けない図形が " + file.Dropped + " 個あります）" : string.Empty) + "。"
                    + Environment.NewLine + "この版で保存すると図の一部が消えてしまうため、開きません。新しい版のエディタで開いてください。"
                    + Environment.NewLine + "図のファイルは変えていません。";
                if (owner != null) MessageBox.Show(owner, message, "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                else MessageBox.Show(message, "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var window = new DrawingEditorWindow(file);

            if (owner != null)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.ShowDialog();
            return window.Result;
        }
    }
}

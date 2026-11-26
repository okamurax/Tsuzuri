using System;
using System.IO;
using System.Text;
using Tsuzuri.Office;

namespace Tsuzuri
{
    // MainWindow の一部: Outlook（classic）を AI に扱わせるつなぎ（2026-11-26。利用者の考え）。
    // 中身（一覧・選んだフォルダ・今の状態の段・outlook.ps1 の頼みに答える所）は Office/OutlookLink.cs（AiLink と共通）。ここは Tsuzuri に置く所だけ。
    // 一覧はランチャーのファイラーの下の段で、Excel の一覧とタブで切り替える（Views/OfficeTabs.cs。MainWindow.Excel.cs の RefreshExcelList で作る）。
    public partial class MainWindow
    {
        private const string OutlookListName = "ランチャーの Outlook の一覧";

        private OutlookLink _outlook;

        /// <summary>「Outlook で AI にいつも伝えること」（中身は Office/OfficeNotes.cs。Excel と共通）。</summary>
        private static readonly OfficeNotes OutlookNotes = new OfficeNotes("Tsuzuri", "Outlook", "Claude");

        /// <summary>Outlook のつなぎ（初めて要るときに作る。画面のスレッドで）。</summary>
        private OutlookLink Outlook
        {
            get
            {
                if (_outlook == null)
                {
                    _outlook = new OutlookLink(AiFolder, OutlookListName, () =>
                    {
                        var part = _outlook.SummaryPart();
                        Status(part.Length == 0 ? "Outlook のフォルダを全部外しました。"
                                                : "Outlook のフォルダを選んでいます:" + part.Replace("  ・Outlook", string.Empty) + "（話しかけると、パスを Claude に渡します）");
                    });
                    _outlook.View.NotesRequested += () => Status(OutlookNotes.Open());
                }
                return _outlook;
            }
        }

        /// <summary>今の状態の「Outlook」の段。まだ一覧を出していなければ、選んでいない。</summary>
        private void AppendOutlookContext(StringBuilder text)
        {
            var outlook = _outlook;
            if (outlook != null) outlook.AppendContext(text);
            else text.Append("Outlook: ").Append(OutlookListName).Append("でフォルダを選んでいない（前に選んでいても、今は外されている）\n");
        }

        /// <summary>今の状態の「Outlook の作業ファイル」の段（この会話が下書きの中身を書くファイル。outlook.ps1 は環境変数 TSUZURI_AI_NAME で同じ名前を選ぶ）。</summary>
        private void AppendOutlookFiles(StringBuilder text, AiTab tab)
        {
            if (tab == null || string.IsNullOrEmpty(tab.Name))
            {
                text.Append("Outlook の作業ファイル: どの会話からの頼みか分からなかったので渡せていない（Outlook の下書きを作るのはやめ、利用者に伝える。探す・読むのはよい）\n");
                return;
            }
            text.Append("Outlook の作業ファイル（この会話専用）: 下書きは「").Append(Path.Combine(AiFolder, "outlook-draft-" + AiExcelFileSuffix(tab.Name) + ".txt")).Append("」\n");
        }

        /// <summary>outlook.ps1 の頼みに答える（パイプのスレッドから）。つなぎがまだ無ければ、画面のスレッドで作ってから。</summary>
        private string ServeOutlook(string request)
        {
            var outlook = _outlook ?? Dispatcher.Invoke(new Func<OutlookLink>(() => Outlook), TimeSpan.FromSeconds(10)) as OutlookLink;
            if (outlook == null) return "error\t画面が応えないので、Outlook を扱えなかった。少し待ってからやり直す";
            return outlook.Serve(request);
        }
    }
}

using System;
using System.Text;
using Tsuzuri.Office;

namespace AiLink
{
    // MainWindow の一部: Outlook（classic）を AI に扱わせるつなぎ（2026-11-26。利用者の考え）。
    // 中身（一覧・選んだフォルダ・今の状態の段・outlook.ps1 の頼みに答える所）は Tsuzuri の Office/OutlookLink.cs（リンクして同じものを使う）。ここは AiLink に置く所だけ。
    // 一覧は左の列で、Excel の一覧の下に置く（MainWindow.Excel.cs の RefreshExcelList）。
    public partial class MainWindow
    {
        private const string OutlookListName = "Outlook の一覧";

        private OutlookLink _outlook;

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
                                                : "Outlook のフォルダを選んでいます:" + part.Replace("  ・Outlook", string.Empty) + "（話しかけると、パスを AI に渡します）");
                    });
                }
                return _outlook;
            }
        }

        /// <summary>今の状態の「Outlook」の段。</summary>
        private void AppendOutlookContext(StringBuilder text)
        {
            var outlook = _outlook;
            if (outlook != null) outlook.AppendContext(text);
            else text.Append("Outlook: ").Append(OutlookListName).Append("でフォルダを選んでいない（前に選んでいても、今は外されている）\n");
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

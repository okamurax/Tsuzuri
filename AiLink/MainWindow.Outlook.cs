using System;
using System.Text;
using Tsuzuri.Office;

namespace AiLink
{
    // MainWindow の一部: Outlook（classic）を AI に扱わせるつなぎ（2026-11-26。利用者の考え）。
    // 中身（一覧・選んだフォルダ・今の状態の段・outlook.ps1 の頼みに答える所）は Tsuzuri の Office/OutlookLink.cs（リンクして同じものを使う）。ここは AiLink に置く所だけ。
    // 一覧は左の列で、Excel の一覧とタブで切り替える（Views/OfficeTabs.cs。MainWindow.Excel.cs の RefreshExcelList で作る）。
    public partial class MainWindow
    {
        private const string OutlookListName = "Outlook の一覧";

        private OutlookLink _outlook;

        /// <summary>「Outlook で AI にいつも伝えること」（中身は Office/OfficeNotes.cs。Excel と共通）。</summary>
        private static readonly OfficeNotes OutlookNotes = new OfficeNotes("AiLink", "Outlook", "AI");

        /// <summary>Outlook のつなぎ（初めて要るときに作る。画面のスレッドで）。</summary>
        private OutlookLink Outlook
        {
            get
            {
                if (_outlook == null)
                {
                    _outlook = new OutlookLink(AiFolder, OutlookListName, "AiLink", () =>
                    {
                        var part = _outlook.SummaryPart();
                        Status(part.Length == 0 ? "Outlook の選びを全部外しました。"
                                                : "Outlook で選んでいます:" + part.Replace("  ・Outlook", string.Empty) + "（話しかけると、フォルダのパスとメールの件名・ID を AI に渡します。本文は渡しません）");
                    });
                    _outlook.View.NotesRequested += () => Status(OutlookNotes.Open());
                }
                return _outlook;
            }
        }

        /// <summary>今の状態の「Outlook」の段。</summary>
        private void AppendOutlookContext(StringBuilder text)
        {
            var outlook = _outlook;
            if (outlook != null) outlook.AppendContext(text);
            else text.Append("Outlook: ").Append(OutlookListName).Append("でフォルダもメールも選んでいない（前に選んでいても、今は外されている）\n");
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

namespace AiLink.Agents
{
    /// <summary>
    /// AI のつなぎ口（AiLink/SPEC.md）。AiLink は Codex に固定するが、ほかの AI に替えられるように、AI ごとの違いはここの向こうに置く。
    /// 今あるのは、探し方・入れ方の案内・画面の印・プロセスの名前・会話の番号。起動のコマンドとフックの形は、まだ MainWindow.Ai.cs・MainWindow.AiHooks.cs にある。
    /// </summary>
    public interface IAgent
    {
        /// <summary>画面に出す名前（例 "Codex"）。</summary>
        string DisplayName { get; }

        /// <summary>
        /// AI の実行ファイルを探す。見つからなければ null。
        /// configured は ailink.json の agentExecutable（空なら決まった場所と PATH から探す）。
        /// AiLink を起動したあとに入れても見つかるように、PATH は今のプロセスのものだけでなく、利用者と PC の設定も読み直す。
        /// </summary>
        string FindExecutable(string configured);

        /// <summary>入っていないときに出す案内の文（何をするものか・入れ方・入れたあとにすること）。1行ずつ。</summary>
        string[] InstallSteps { get; }

        /// <summary>入れるコマンド（公式の PowerShell の1行）。案内に出し、「コマンドをコピー」で写す。</summary>
        string InstallCommand { get; }

        /// <summary>入れる版の選び（例 x64・ARM64）。案内に1つずつ「入れる（…）」のボタンを出す。</summary>
        AgentInstallChoice[] InstallChoices { get; }

        /// <summary>この PC に合う版と見たもの（InstallChoices のどれか）。分からなければ null。案内でそのボタンを目立たせる。</summary>
        AgentInstallChoice LikelyInstallChoice { get; }

        /// <summary>
        /// 入力欄が空で、頼みを待っているときに画面に出る文字（Codex の「Ask Codex to do anything」）。ほかの会話の伝言を打つ前に、
        /// これが画面にあるかを確かめる（書きかけ・確かめの画面に打ち込まないため）。無ければ null（自動では打たない）。
        /// </summary>
        string EmptyInputMarker { get; }

        /// <summary>入力欄の行の頭の印（Codex の「›」）。この行だけを見て、空の印・打った伝言があるかを確かめる。</summary>
        string InputLinePrefix { get; }

        /// <summary>作業中に画面に出る文字（Codex の「esc to interrupt」）。出ていれば打たない。</summary>
        string WorkingMarker { get; }

        /// <summary>AI が動いているときのプロセスの名前（タブの PowerShell の下にこれがあるかで、AI が動いているかを見る。例 codex.exe）。</summary>
        string[] ProcessNames { get; }

        /// <summary>会話の番号として正しい形か（起動のコマンドに書くので、ほかのものは使わない）。</summary>
        bool IsValidSessionId(string id);

        /// <summary>AI がその会話を残しているか（次の起動で続きとして開けるか）。調べられなければ true。</summary>
        bool SessionExists(string id);

        /// <summary>入れ方を説明している公式のページ。</summary>
        string InstallPage { get; }
    }

    /// <summary>入れる版の選び1つ。</summary>
    public sealed class AgentInstallChoice
    {
        /// <summary>ボタンに出す名前（例 "x64"）。</summary>
        public string Label;

        /// <summary>どんな PC 向けか（ツールチップに出す）。</summary>
        public string Description;

        /// <summary>別の PowerShell（Windows PowerShell 5.1）の窓で動かす台本。</summary>
        public string Script;
    }
}

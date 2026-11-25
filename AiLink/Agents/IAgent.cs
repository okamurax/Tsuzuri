namespace AiLink.Agents
{
    /// <summary>
    /// AI のつなぎ口（PORTING.md）。AiLink は Codex に固定するが、ほかの AI に替えられるように、AI ごとの違いはここの向こうに置く。
    /// 今は「入っているか」と「入れ方の案内」だけ。起動のコマンド・フック・会話の番号などは第3段で足す。
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

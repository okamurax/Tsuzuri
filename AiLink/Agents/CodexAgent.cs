using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AiLink.Agents
{
    /// <summary>
    /// OpenAI の Codex CLI（2026-11-25 に公式の手順を確かめた）。
    /// Windows では公式の入れ方（PowerShell の1行）で %LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe に入る（CODEX_INSTALL_DIR があればそこ）。
    /// Node.js は要らない。npm で入れた人は %APPDATA%\npm\codex.cmd。初めて動かすと、ChatGPT のアカウントでサインインする。
    /// </summary>
    public sealed class CodexAgent : IAgent
    {
        public string DisplayName
        {
            get { return "Codex"; }
        }

        public string InstallCommand
        {
            get { return "irm https://chatgpt.com/codex/install.ps1 | iex"; }
        }

        /// <summary>
        /// 版を選んで入れる（2026-11-25。利用者の考え）。公式の install.ps1 を取ってきて、CPU の種類を調べる1か所を、選んだ版に差し替えてから動かす。
        /// 公式の手順は [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture で調べるが、Windows の PowerShell 5.1 ではこれが無く、
        /// Set-StrictMode の下で「プロパティ 'OSArchitecture' が見つかりません」で止まる（PowerShell 7 では動く。Windows で試して分かった）。
        /// 公式の手順がこの書き方をやめていれば、差し替えずにそのまま動かす（そのときは公式の手順が自分で調べる）。
        /// 入れ終わったあとに Codex をその窓で起動するかの問いも、出さないように差し替える。
        /// </summary>
        public AgentInstallChoice[] InstallChoices
        {
            get
            {
                return new[]
                {
                    new AgentInstallChoice { Label = "x64", Description = "Intel・AMD の CPU の PC（ふつうの Windows の PC）", Script = ScriptFor("X64") },
                    new AgentInstallChoice { Label = "ARM64", Description = "ARM の CPU の PC（Snapdragon の PC・Mac の上の Windows など）", Script = ScriptFor("Arm64") },
                };
            }
        }

        public AgentInstallChoice LikelyInstallChoice
        {
            get
            {
                var machine = NativeMachine();
                var choices = InstallChoices;
                if (machine == ImageFileMachineArm64) return choices[1];
                if (machine == ImageFileMachineAmd64) return choices[0];
                return null;
            }
        }

        private static string ScriptFor(string architecture)
        {
            return "$installer = Invoke-RestMethod 'https://chatgpt.com/codex/install.ps1';" +
                   "$installer = $installer.Replace('[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture', \"'" + architecture + "'\");" +
                   // 入れ終わったあとの「Start Codex now?」は聞かない（2026-11-25。答えると Codex がこの窓の中で、AiLink の外で起動してしまう。
                   // AiLink は戻ってきたら自分のタブで起動する）。ほかの問い（前の版を退かすかなど）は残す
                   "$installer = $installer.Replace('Prompt-YesNo \"Start Codex now?\"', '$false');" +
                   "Invoke-Expression $installer";
        }

        /// <summary>
        /// この PC で動く exe か（2026-11-25 のレビュー。x64 の PC で「入れる（ARM64）」を押すと ARM64 の codex.exe が置かれ、
        /// 見つかったことにして案内が消えたあと、起動できずに入れ直す手段が無くなった）。exe の頭（PE）の CPU の種類を見る。
        /// ARM64 の PC は x64 の exe も動かせる。exe でないもの（npm の codex.cmd）・読めないものは動くとみなす。
        /// </summary>
        private static bool Runnable(string path)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
            ushort machine;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return true;   // MZ でなければ分からない
                    stream.Position = 0x3C;
                    var pe = reader.ReadInt32();
                    if (pe <= 0 || pe + 6 > stream.Length) return true;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550) return true;   // "PE\0\0"
                    machine = reader.ReadUInt16();
                }
            }
            catch (Exception)
            {
                return true;
            }
            var native = NativeMachine();
            if (machine == ImageFileMachineArm64) return native == ImageFileMachineArm64;
            return true;
        }

        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xAA64;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        /// <summary>PC の本当の CPU（x64 の真似をして動いていても、ARM64 なら ARM64）。調べられなければ 0。</summary>
        private static ushort NativeMachine()
        {
            try
            {
                ushort processMachine, nativeMachine;
                if (IsWow64Process2(System.Diagnostics.Process.GetCurrentProcess().Handle, out processMachine, out nativeMachine)) return nativeMachine;
            }
            catch (Exception)
            {
                // 古い Windows には IsWow64Process2 が無い
            }
            return Environment.Is64BitOperatingSystem ? ImageFileMachineAmd64 : (ushort)0;
        }

        public string InstallPage
        {
            get { return "https://learn.chatgpt.com/docs/codex/cli"; }
        }

        public string[] InstallSteps
        {
            get
            {
                return new[]
                {
                    "AI連携ツールの AI には、OpenAI の Codex（コマンドで動く版）を使います。このパソコンには、まだ入っていません。",
                    "1. 下の「入れる（…）」から、この PC に合う版を押してください（青いほうが、AI連携ツールが調べた見当です）。別の PowerShell の窓が開き、OpenAI の入れる手順が動きます（管理者の権限は要りません。自分のユーザーにだけ入ります）。",
                    "2. 窓に「終わりました」と出たら、その窓を閉じて、ここに戻ってください。入ったかどうかは自動で確かめます（「もう一度確かめる」でも確かめられます）。",
                    "3. 初めて使うときは、会社の ChatGPT のアカウントでサインインします（ブラウザーが開きます）。",
                    "うまく入らないときは、「コマンドをコピー」で写したものを、情報システムの担当に見せてください。",
                };
            }
        }

        /// <summary>codex.exe（公式の入れ方）か、npm で入れたときの node.exe（codex.cmd から cmd.exe の下で動く）。</summary>
        public string[] ProcessNames
        {
            get { return new[] { "codex.exe", "node.exe" }; }
        }

        /// <summary>入力欄の行の頭の印（2026-11-25 に Windows の Codex v0.172 の画面で確かめた）。</summary>
        public string InputLinePrefix
        {
            get { return "\u203A"; }
        }

        /// <summary>作業中に画面に出る文字（作業中は打たない）。</summary>
        public string WorkingMarker
        {
            get { return "esc to interrupt"; }
        }

        /// <summary>入力欄が空のときの薄い文字（2026-11-25 に Windows の Codex v0.172 の画面で確かめた）。</summary>
        public string EmptyInputMarker
        {
            get { return "Ask Codex to do anything"; }
        }

        /// <summary>Codex の会話の番号は Guid の形（例 01a212f9-6f77-7c60-b430-f223a299318e。2026-11-25 にフックで確かめた）。</summary>
        public bool IsValidSessionId(string id)
        {
            Guid parsed;
            return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out parsed);
        }

        /// <summary>
        /// Codex は会話を %CODEX_HOME%（無ければ %USERPROFILE%\.codex）\sessions\年\月\日\rollout-…-番号.jsonl に残す（2026-11-25 にフックの transcript_path で確かめた）。
        /// 話しかける前に閉じた会話は残らないことがあるので、無ければ戻さない。
        /// </summary>
        public bool SessionExists(string id)
        {
            if (!IsValidSessionId(id)) return false;
            try
            {
                var home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                var sessions = Path.Combine(home, "sessions");
                if (!Directory.Exists(sessions)) return true;
                return Directory.EnumerateFiles(sessions, "rollout-*" + id + ".jsonl", SearchOption.AllDirectories).Any();
            }
            catch (Exception)
            {
                return true;
            }
        }

        public string FindExecutable(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                if (File.Exists(expanded) && Runnable(expanded)) return expanded;
            }

            var candidates = new List<string>();
            var installDir = Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR", EnvironmentVariableTarget.User)
                             ?? Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR");
            if (!string.IsNullOrWhiteSpace(installDir)) candidates.Add(Path.Combine(installDir, "codex.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe"));

            // PATH は、今のプロセスのもの（起動した時点）に加え、利用者と PC の設定を読み直す（AiLink を起動したあとに入れても見つかるように）
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path;
                try
                {
                    path = Environment.GetEnvironmentVariable("PATH", target);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var folder in (path ?? string.Empty).Split(';'))
                {
                    var trimmed = folder.Trim().Trim('"');
                    if (trimmed.Length == 0) continue;
                    try
                    {
                        trimmed = Environment.ExpandEnvironmentVariables(trimmed);
                        candidates.Add(Path.Combine(trimmed, "codex.exe"));
                        candidates.Add(Path.Combine(trimmed, "codex.cmd"));
                    }
                    catch (Exception)
                    {
                        // PATH に変な項目があっても飛ばす
                    }
                }
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate) && Runnable(candidate)) return candidate;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }
    }
}

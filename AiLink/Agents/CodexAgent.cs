using System;
using System.Collections.Generic;
using System.IO;

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
        /// 公式の install.ps1 を取ってきて、CPU の種類を調べる1か所を差し替えてから動かす（2026-11-25。Windows の PowerShell 5.1 で試して分かった）。
        /// 公式の手順は [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture で調べるが、PowerShell 5.1 ではこれが無く、
        /// Set-StrictMode の下で「プロパティ 'OSArchitecture' が見つかりません」で止まる（PowerShell 7 では動く）。
        /// 差し替えた先は、まず同じものを試し、無ければ Win32_Processor（12 は ARM64、9 は x64）、それも無ければ環境変数で決める。
        /// 公式の手順がこの書き方をやめていれば、差し替えずにそのまま動かす。
        /// </summary>
        public string InstallScript
        {
            get
            {
                return "function Get-AiLinkArchitecture {" +
                       " try { $a = [string][System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture; if ($a) { return $a } } catch { }" +
                       " try { switch ([int](Get-CimInstance Win32_Processor | Select-Object -First 1).Architecture) { 12 { return 'Arm64' } 9 { return 'X64' } } } catch { }" +
                       " if ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64' -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { return 'Arm64' }" +
                       " return 'X64' };" +
                       "$installer = Invoke-RestMethod 'https://chatgpt.com/codex/install.ps1';" +
                       "$installer = $installer.Replace('[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture', '(Get-AiLinkArchitecture)');" +
                       "Invoke-Expression $installer";
            }
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
                    "1. 下の「入れる」を押すと、別の PowerShell の窓が開き、OpenAI の入れる手順が動きます（管理者の権限は要りません。自分のユーザーにだけ入ります）。",
                    "2. 窓に「終わりました」と出たら、その窓を閉じて、ここに戻ってください。入ったかどうかは自動で確かめます（「もう一度確かめる」でも確かめられます）。",
                    "3. 初めて使うときは、会社の ChatGPT のアカウントでサインインします（ブラウザーが開きます）。",
                    "うまく入らないときは、「コマンドをコピー」で写したものを、情報システムの担当に見せてください。",
                };
            }
        }

        public string FindExecutable(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                if (File.Exists(expanded)) return expanded;
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
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }
    }
}

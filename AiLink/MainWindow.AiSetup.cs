using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiLink.Agents;

namespace AiLink
{
    // MainWindow の一部: AI（Codex）が入っていない PC での案内（2026-11-25。利用者の考え。会社で配るので、入っていない人に入れ方を示す）。
    //
    // 起動したとき AI の実行ファイルが見つからなければ、会話のタブを開かず、AI の欄に案内を出す。
    // 「入れる」で別の PowerShell の窓を開いて公式の入れる手順を動かす（利用者が押したときだけ。勝手には入れない）。
    // 窓に戻るたびに（Activated）と、案内を出している間は3秒ごとに探し直し、見つかれば案内を消して会話のタブを開く。
    public partial class MainWindow
    {
        /// <summary>AI のつなぎ口。AiLink は Codex に固定する（PORTING.md）。</summary>
        private readonly IAgent _agent = new CodexAgent();

        /// <summary>見つけた AI の実行ファイル。見つかるまでは null。</summary>
        private string _agentExecutable;

        /// <summary>入っていないときの案内。出していなければ null。</summary>
        private Border _agentSetup;

        /// <summary>「入れる」で開いた PowerShell の窓（閉じるまで探し直さない）。</summary>
        private Process _agentInstaller;

        /// <summary>案内を出している間、入ったかを3秒ごとに探し直す（窓に戻ったこと（Activated）を取りこぼしても切り替わるように）。</summary>
        private System.Windows.Threading.DispatcherTimer _agentPoll;

        /// <summary>AI を探す。見つかれば true。見つからなければ案内を出して false。</summary>
        private bool EnsureAgent()
        {
            _agentExecutable = _agent.FindExecutable(_settings.AgentExecutable);
            if (_agentExecutable != null)
            {
                HideAgentSetup();
                return true;
            }
            ShowAgentSetup();
            return false;
        }

        /// <summary>案内を出しているときに窓に戻った: 探し直し、入っていれば案内を消して会話を始める。</summary>
        private void RecheckAgent()
        {
            if (_agentSetup == null) return;
            // 入れる窓が開いている間は探し直さない（入れる手順は途中で codex.exe を置いてから確かめるので、確かめる前に見つけてしまう）
            if (_agentInstaller != null)
            {
                try
                {
                    if (!_agentInstaller.HasExited) return;
                }
                catch (Exception)
                {
                }
                _agentInstaller = null;
            }
            if (!EnsureAgent()) return;
            Status(_agent.DisplayName + " が見つかりました: " + _agentExecutable);
            StartAi();
        }

        private void ShowAgentSetup()
        {
            if (_agentSetup != null) return;

            var text = TryFindResource("TextPrimary") as Brush ?? Brushes.White;
            var muted = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
            var buttonStyle = TryFindResource("ToolButton") as Style;

            var panel = new StackPanel { MaxWidth = 640, Margin = new Thickness(32, 28, 32, 28), HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new TextBlock
            {
                Text = _agent.DisplayName + " が入っていません",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = text,
                Margin = new Thickness(0, 0, 0, 14),
            });
            foreach (var step in _agent.InstallSteps)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = step,
                    FontSize = 14,
                    Foreground = text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
            Func<string, string, Action, Button> button = (label, tip, click) =>
            {
                var created = new Button { Content = label, ToolTip = tip, FontSize = 14, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 8), Focusable = false };
                if (buttonStyle != null) created.Style = buttonStyle;
                created.Click += delegate { click(); };
                buttons.Children.Add(created);
                return created;
            };
            // 版ごとに「入れる（…）」。この PC に合うと見たものを青くする（利用者が選ぶ。見当が外れていても選び直せる）
            var likely = _agent.LikelyInstallChoice;
            foreach (var choice in _agent.InstallChoices)
            {
                var chosen = choice;
                var isLikely = likely != null && likely.Label == choice.Label;
                var install = button("入れる（" + choice.Label + "）",
                    choice.Description + (isLikely ? "。この PC はたぶんこちら" : string.Empty) + "。別の PowerShell の窓で、OpenAI の入れる手順を動かす",
                    () => RunAgentInstaller(chosen));
                if (!isLikely) continue;
                install.Background = TryFindResource("AccentFill") as Brush ?? install.Background;
                install.Foreground = TryFindResource("AccentText") as Brush ?? text;
            }
            button("もう一度確かめる", "入ったかどうかを探し直す", () =>
            {
                if (EnsureAgent())
                {
                    Status(_agent.DisplayName + " が見つかりました: " + _agentExecutable);
                    StartAi();
                }
                else
                {
                    Status(_agent.DisplayName + " はまだ見つかりません。");
                }
            });
            button("コマンドをコピー", "入れるコマンドをクリップボードに写す", () =>
            {
                try
                {
                    Clipboard.SetText(_agent.InstallCommand);
                    Status("入れるコマンドを写しました: " + _agent.InstallCommand);
                }
                catch (Exception ex)
                {
                    Status("写せませんでした: " + ex.Message);
                }
            });
            button("説明のページ", "OpenAI の説明のページをブラウザーで開く", () => OpenInBrowser(_agent.InstallPage));
            panel.Children.Add(buttons);

            panel.Children.Add(new TextBlock
            {
                Text = "入れるコマンド（PowerShell）: " + _agent.InstallCommand,
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "入れる場所を変えている場合は、ailink.json の agentExecutable に codex.exe の場所を書いてください（パスの \\ は \\\\ と書きます。例 \"C:\\\\Tools\\\\codex.exe\"）。",
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

            _agentSetup = new Border
            {
                Background = TryFindResource("PageBackground") as Brush ?? Brushes.Black,
                Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            Panel.SetZIndex(_agentSetup, 10);
            AiTerminals.Children.Add(_agentSetup);

            if (_agentPoll == null)
            {
                _agentPoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _agentPoll.Tick += delegate { RecheckAgent(); };
            }
            _agentPoll.Start();
        }

        private void HideAgentSetup()
        {
            if (_agentPoll != null) _agentPoll.Stop();
            if (_agentSetup == null) return;
            AiTerminals.Children.Remove(_agentSetup);
            _agentSetup = null;
        }

        /// <summary>
        /// 「入れる（…）」: 選んだ版を、別の PowerShell の窓で入れる。終わっても窓は閉じずに残す（結果を読めるように）。
        /// 入れる手順が尋ねてくることがあるので（前の版を退かすかなど）、窓は利用者が操作できる普通の窓にする。
        /// </summary>
        private void RunAgentInstaller(AgentInstallChoice choice)
        {
            try
            {
                // 入れる手順は一時ファイルに書き、& で動かす（手順の中の exit が、窓ごと閉じずにその台本だけを終えるように。失敗のわけを読めるように）
                var file = Path.Combine(Path.GetTempPath(), "ailink-install-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".ps1");
                File.WriteAllText(file, choice.Script + "\r\n", new UTF8Encoding(true));
                var script = "Write-Host '" + _agent.DisplayName + " の " + choice.Label + " の版を入れます（OpenAI の入れる手順）。' -ForegroundColor Cyan;"
                             + "& " + Quote(file) + ";"
                             + "Remove-Item -LiteralPath " + Quote(file) + " -ErrorAction SilentlyContinue;"
                             + "Write-Host '';Write-Host '手順が終わりました（赤い文字が出ていれば、うまく入っていません）。この窓を閉じて、AI連携ツールに戻ってください。' -ForegroundColor Green";
                var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(powershell,
                    "-NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };
                _agentInstaller = Process.Start(start);
                Status("別の窓で " + _agent.DisplayName + " を入れています。終わったら、入れる窓を閉じてください（閉じると、入ったかを確かめます）。");
            }
            catch (Exception ex)
            {
                Status("入れる手順を動かせませんでした: " + ex.Message);
            }
        }

        private void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status("ページを開けませんでした: " + ex.Message);
            }
        }
    }
}

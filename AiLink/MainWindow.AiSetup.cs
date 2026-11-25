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
    // 窓に戻るたびに（Activated）探し直し、見つかれば案内を消して会話のタブを開く。
    public partial class MainWindow
    {
        /// <summary>AI のつなぎ口。AiLink は Codex に固定する（PORTING.md）。</summary>
        private readonly IAgent _agent = new CodexAgent();

        /// <summary>見つけた AI の実行ファイル。見つかるまでは null。</summary>
        private string _agentExecutable;

        /// <summary>入っていないときの案内。出していなければ null。</summary>
        private Border _agentSetup;

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
            var install = button("入れる", "別の PowerShell の窓で、OpenAI の入れる手順を動かす", RunAgentInstaller);
            install.Background = TryFindResource("AccentFill") as Brush ?? install.Background;
            install.Foreground = TryFindResource("AccentText") as Brush ?? text;
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
                Text = "入れる場所を変えている場合は、ailink.json の agentExecutable に codex.exe の場所を書いてください。",
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
        }

        private void HideAgentSetup()
        {
            if (_agentSetup == null) return;
            AiTerminals.Children.Remove(_agentSetup);
            _agentSetup = null;
        }

        /// <summary>
        /// 「入れる」: 別の PowerShell の窓で入れるコマンドを動かす。終わっても窓は閉じずに残す（結果を読めるように）。
        /// 入れる手順が尋ねてくることがあるので（前の版を退かすかなど）、窓は利用者が操作できる普通の窓にする。
        /// </summary>
        private void RunAgentInstaller()
        {
            try
            {
                var script = "Write-Host '" + _agent.DisplayName + " を入れます（OpenAI の入れる手順）。' -ForegroundColor Cyan;"
                             + _agent.InstallScript + ";"
                             + "Write-Host '';Write-Host '終わりました。この窓を閉じて、AI連携ツールに戻ってください。' -ForegroundColor Green";
                var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(powershell,
                    "-NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };
                Process.Start(start);
                Status("別の窓で " + _agent.DisplayName + " を入れています。終わったら、この窓に戻ってください。");
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

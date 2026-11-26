using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace AiLink
{
    /// <summary>
    /// 起動の入口。前提が揃わなければ起動しない（Tsuzuri の App と同じ考え）。
    /// ここで弾いておけば、中では「アプリが2つ動いている」「設定が読めていない」を考えなくてよい。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>画面・知らせに出すソフトの名前。</summary>
        public const string DisplayName = "AI連携ツール";

        // 生きている間持っておく。手放すと、2つ目の起動を止められない。
        private static Mutex _instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool first;
            _instance = new Mutex(true, @"Local\AiLink.SingleInstance", out first);
            if (!first)
            {
                MessageBox.Show(DisplayName + "はすでに起動しています。", DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 漏れた例外。画面のスレッドのものは、記録して知らせ、続ける。短い間に何度も起きるときだけ落とす。裏のスレッドのものは記録だけ残す。
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => LogCrash("裏のスレッド（終了します）", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                LogCrash("待たれなかったタスク", args.Exception);
                args.SetObserved();
            };

            AppSettings settings;
            try
            {
                settings = AppSettings.Load();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 開けない（ほかのプログラムが使っている・共有フォルダに一時つながらない等）。中身は正しいかもしれないので、退かさずに起動しない（2026-11-26 のレビュー）
                MessageBox.Show(
                    "設定ファイル（ailink.json）を開けないため、起動しません。少し待ってから起動し直してください。" + Environment.NewLine + Environment.NewLine +
                    AppSettings.StorePath + Environment.NewLine + ex.Message,
                    DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            catch (Exception ex)
            {
                // 中身が読めない設定は消さずに退かし、初めの状態で始めるかを尋ねる（手で直して \ を書き損じたときに、起動できなくならないように。2026-11-25・26 のレビュー）。
                // 退かす名前には時刻を付ける（前に退かしたファイルを消さない）
                var aside = AppSettings.StorePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                var answer = MessageBox.Show(
                    "設定ファイル（ailink.json）の中身を読めません。" + Environment.NewLine +
                    "「はい」で、このファイルを " + Path.GetFileName(aside) + " という名前に退かして、初めの状態で起動します（会話のタブなどの覚えは無くなります。退かしたファイルから手で戻せます）。" + Environment.NewLine +
                    "「いいえ」で、起動しません（ファイルを直してから起動してください。パスの \\ は \\\\ と書きます）。" + Environment.NewLine + Environment.NewLine +
                    AppSettings.StorePath + Environment.NewLine + ex.Message,
                    DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    Shutdown();
                    return;
                }
                try
                {
                    File.Move(AppSettings.StorePath, aside);
                }
                catch (Exception moveError)
                {
                    MessageBox.Show("設定ファイルを退かせませんでした: " + moveError.Message, DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
                settings = new AppSettings();
            }

            // 窓を作る前に決める（作ったあとでは効かない窓がある）
            if (settings.SoftwareRendering) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            var window = new AiLink.MainWindow(settings);
            MainWindow = window;
            window.Show();
        }

        /// <summary>画面のスレッドで漏れた例外の時刻（短い間に何度も起きたら、続けずに落とす）。</summary>
        private static readonly List<DateTime> _uiErrors = new List<DateTime>();
        private static bool _showingError;

        private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("画面のスレッド", e.Exception);

            var now = DateTime.Now;
            _uiErrors.RemoveAll(time => now - time > TimeSpan.FromSeconds(30));
            _uiErrors.Add(now);
            if (_uiErrors.Count > 3) return;   // 続けても同じことが起きる。落とす

            e.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                MessageBox.Show(
                    "思わぬエラーが起きました。このまま使えますが、開き直すのを勧めます。" + Environment.NewLine + Environment.NewLine +
                    e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine + Environment.NewLine +
                    "記録: " + CrashLogPath,
                    DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _showingError = false;
            }
        }

        /// <summary>漏れた例外の記録（ailink.json の隣の crash.log。1MB を越えたら作り直す）。</summary>
        private static string CrashLogPath
        {
            get { return Path.Combine(Path.GetDirectoryName(AppSettings.StorePath) ?? ".", "crash.log"); }
        }

        private static void LogCrash(string where, Exception ex)
        {
            try
            {
                var path = CrashLogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + where + Environment.NewLine
                                         + (ex == null ? "（例外の中身なし）" : ex.ToString()) + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 記録できなくても、元の異常を覆わない
            }
        }
    }
}

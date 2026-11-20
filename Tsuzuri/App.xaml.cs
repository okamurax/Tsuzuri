using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Tsuzuri
{
    /// <summary>
    /// 起動の入口。前提が揃わなければ起動しない（SPEC 2章）。
    /// ここで弾いておけば、中では「アプリが2つ動いている」「設定が読めていない」を考えなくてよい。
    /// </summary>
    public partial class App : Application
    {
        // 生きている間持っておく。手放すと、2つ目の起動を止められない。
        private static Mutex _instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool first;
            _instance = new Mutex(true, @"Local\Tsuzuri.SingleInstance", out first);
            if (!first)
            {
                MessageBox.Show("Tsuzuri はすでに起動しています。", "起動", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 漏れた例外（2026-11-20）。画面のスレッドのものは、記録して知らせ、続ける（前はそのまま落ちた。未保存の分は各画面が別名で残す）。
            // 続けられない（短い間に何度も起きる）ときだけ落とす。裏のスレッドのものは止められないので、記録だけ残す。
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
            catch (Exception ex)
            {
                MessageBox.Show(
                    "設定ファイル（app.json）を読めないため、起動しません。" + Environment.NewLine +
                    "直すか消してから起動してください（消すと初めの状態で始まります）。" + Environment.NewLine + Environment.NewLine +
                    AppSettings.StorePath + Environment.NewLine + ex.Message,
                    "起動", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            // 窓を作る前に決める（作ったあとでは効かない窓がある）。
            if (settings.SoftwareRendering) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            // 打ってから画面に出るまでの時間を測る（2026-11-04。perfLog が true のときだけ）。
            PerfLog.Enabled = settings.PerfLog;
            if (PerfLog.Enabled) CompositionTarget.Rendering += delegate { PerfLog.Rendered(); };

            var window = new Tsuzuri.MainWindow(settings);
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
            if (_uiErrors.Count > 3) return;   // 続けても同じことが起きる。落とす（未保存の分は別名で残してある）

            e.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                MessageBox.Show(
                    "思わぬエラーが起きました。未保存の編集は、念のため別名（.recovered-）でも残しました。" + Environment.NewLine +
                    "このまま使えますが、保存して開き直すのを勧めます。" + Environment.NewLine + Environment.NewLine +
                    e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine + Environment.NewLine +
                    "記録: " + CrashLogPath,
                    "Tsuzuri", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _showingError = false;
            }
        }

        /// <summary>漏れた例外の記録（app.json の隣の crash.log。1MB を越えたら作り直す）。</summary>
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

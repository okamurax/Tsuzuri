using System;
using System.Threading;
using System.Windows;

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

            var window = new Tsuzuri.MainWindow(settings);
            MainWindow = window;
            window.Show();
        }
    }
}

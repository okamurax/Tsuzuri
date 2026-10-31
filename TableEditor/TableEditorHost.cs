using System.Windows;

namespace TableEditor
{
    /// <summary>
    /// エディタ本体（Tsuzuri）から表エディタを使う入口（SPEC 8.4.2）。
    ///
    /// 表エディタは本体と同じ WPF のウィンドウで、本体の上に開く。閉じるまで本体は操作できない（モーダル）。
    /// 経緯（2026-10-18）: 前は SPREAD for Windows Forms を同じプロセスで開いていた。数式バーの字の色が変えられない、
    /// 本体の紺に合わせるのに無理が要る、DLL が二十数個増え、有料のライセンスが要る、などがあり、自前の WPF の表に替えた。
    /// 前のものは archive/TableEditor-SPREAD/ にある。
    /// </summary>
    public static class TableEditorHost
    {
        /// <summary>
        /// 表を編集する。閉じるまで戻らない。保存して閉じたら編集した表（受け渡しの形）を、保存せずに閉じたら null を返す。
        /// </summary>
        /// <param name="owner">本体のウィンドウ。表エディタはその真ん中に出る。無ければ画面の真ん中。</param>
        /// <param name="exchange">受け渡しの形の表（TableEditorWindow を参照）。</param>
        public static string Edit(Window owner, string exchange)
        {
            var window = new TableEditorWindow(exchange);

            if (owner != null)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.ShowDialog();
            return window.Result;
        }
    }
}

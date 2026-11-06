using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Tsuzuri
{
    /// <summary>
    /// 打ってから画面に出るまでの時間を測って書き出す（2026-11-04。app.json の perfLog が true のときだけ）。
    /// 日本語の確定から画面に出るまでが、大きな文書ほど遅い原因を探すためのもの。exe の隣の perf.log に追記する。
    /// 1回の入力（確定）ごとに、文字が届いてから、文書の書き換え・行の組み立て・描画までの経過を1行にまとめる。
    /// ページを移るとき・画面のタブを切り替えるときの段ごとの時間も、別の行に書く（下の BeginSteps から）。
    /// </summary>
    public static class PerfLog
    {
        public static bool Enabled;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly StringBuilder Line = new StringBuilder();
        private static double _start = -1;
        private static int _gen0, _gen1, _gen2;

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "perf.log"); }
        }

        private static double Now
        {
            get { return Clock.Elapsed.TotalMilliseconds; }
        }

        /// <summary>入力の始まり（文字が届いた）。続けて届いた文字は、同じ入力にまとめる。</summary>
        public static void Input(string text, int lineCount)
        {
            if (!Enabled) return;

            if (_start < 0)
            {
                _start = Now;
                _gen0 = GC.CollectionCount(0);
                _gen1 = GC.CollectionCount(1);
                _gen2 = GC.CollectionCount(2);
                Line.Clear();
                Line.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" 行数=").Append(lineCount).Append(" |");
            }

            Line.Append(" 文字'").Append(text).Append("'@").Append(Elapsed());
        }

        /// <summary>途中の印（かかった時間つき）。</summary>
        public static void Mark(string name, double milliseconds)
        {
            if (!Enabled || _start < 0) return;
            Line.Append(' ').Append(name).Append('=').Append(milliseconds.ToString("0.0")).Append("ms@").Append(Elapsed());
        }

        /// <summary>途中の印（時刻だけ）。</summary>
        public static void Mark(string name)
        {
            if (!Enabled || _start < 0) return;
            Line.Append(' ').Append(name).Append('@').Append(Elapsed());
        }

        /// <summary>描画の直前（CompositionTarget.Rendering）。入力があれば、ここまでを1行にして書き出す。</summary>
        public static void Rendered()
        {
            if (!Enabled || _start < 0) return;

            Line.Append(" 描画@").Append(Elapsed())
                .Append(" GC=").Append(GC.CollectionCount(0) - _gen0).Append('/').Append(GC.CollectionCount(1) - _gen1).Append('/').Append(GC.CollectionCount(2) - _gen2);
            _start = -1;

            try
            {
                File.AppendAllText(FilePath, Line.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 書けなくても動作は止めない。
            }
        }

        // ---------------------------------------------------------------
        // ページを移るとき（MainWindow.Stack.cs）と、画面のタブを切り替えるとき（MainWindow.Windows.cs）の時間（2026-11-05）。
        // 2万行の本体からサブページへ行って戻るのが遅い原因を探すために入れ、タブの切り替えにも使うようにした。
        // 打鍵の行とは別に1行にまとめる。移り始めから、組み直し・アウトライン・配置・描画・落ち着くまでの経過を書く。
        // 行の頭の印は、どちらでも「[ページ移動]」のまま。どちらかは続くラベル（「開く」「戻る」か「タブ」）で見分ける。
        // ---------------------------------------------------------------

        private static readonly StringBuilder StepLine = new StringBuilder();
        private static double _stepStart = -1;
        private static int _stepGen0, _stepGen1, _stepGen2;

        public static bool Stepping
        {
            get { return Enabled && _stepStart >= 0; }
        }

        /// <summary>ページを移り始めた・画面のタブを切り替え始めた。label はその中身（「開く p3」「タブ 2 …」など）。</summary>
        public static void BeginSteps(string label)
        {
            if (!Enabled) return;

            _stepStart = Now;
            _stepGen0 = GC.CollectionCount(0);
            _stepGen1 = GC.CollectionCount(1);
            _stepGen2 = GC.CollectionCount(2);
            StepLine.Clear();
            StepLine.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [ページ移動] ").Append(label).Append(" |");
        }

        /// <summary>1つの段にかかった時間（started は Start() の値）。</summary>
        public static void Step(string name, double started)
        {
            if (!Stepping) return;
            StepLine.Append(' ').Append(name).Append('=').Append((Now - started).ToString("0.0")).Append("ms");
        }

        /// <summary>途中の印（移り始めからの経過）。</summary>
        public static void StepMark(string name)
        {
            if (!Stepping) return;
            StepLine.Append(' ').Append(name).Append('@').Append((Now - _stepStart).ToString("0.0"));
        }

        /// <summary>補足（数など）。</summary>
        public static void StepNote(string name, object value)
        {
            if (!Stepping) return;
            StepLine.Append(' ').Append(name).Append('=').Append(value);
        }

        /// <summary>書き出して終わる。</summary>
        public static void EndSteps()
        {
            if (!Stepping) return;

            StepLine.Append(" 計=").Append((Now - _stepStart).ToString("0.0")).Append("ms")
                .Append(" GC=").Append(GC.CollectionCount(0) - _stepGen0).Append('/').Append(GC.CollectionCount(1) - _stepGen1).Append('/').Append(GC.CollectionCount(2) - _stepGen2);
            _stepStart = -1;

            try
            {
                File.AppendAllText(FilePath, StepLine.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 書けなくても動作は止めない。
            }
        }

        /// <summary>今の時計（ミリ秒）。かかった時間を測るときの始まりに使う。</summary>
        public static double Start()
        {
            return Enabled ? Now : 0;
        }

        public static double Since(double start)
        {
            return Now - start;
        }

        private static string Elapsed()
        {
            return (Now - _start).ToString("0.0");
        }
    }
}

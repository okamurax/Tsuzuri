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

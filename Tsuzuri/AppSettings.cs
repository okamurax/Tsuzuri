using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri
{
    /// <summary>
    /// exe と同階層の app.json（SPEC 2章）。持ち運べるように %AppData% は使わない。
    ///
    /// ランチャーの登録は利用者が明示的にしたものなので、
    /// ファイルが一時的に見つからなくても勝手に消さない。
    /// </summary>
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>ランチャーのマス（SPEC 4章）。登録のあるマスだけを持つ。</summary>
        [DataMember(Name = "launcher")]
        public List<LauncherTile> Tiles { get; set; }

        /// <summary>左サイドバーの幅。</summary>
        [DataMember(Name = "sidebarWidth")]
        public double SidebarWidth { get; set; }

        /// <summary>右のファイラーの幅（SPEC 9章）。</summary>
        [DataMember(Name = "filerWidth")]
        public double FilerWidth { get; set; }

        /// <summary>右の AI の欄の幅（SPEC 19章）。</summary>
        [DataMember(Name = "aiWidth")]
        public double AiWidth { get; set; }

        /// <summary>左の欄の、アウトラインと見出しの履歴の仕切り。上のアウトラインの高さの割合（2026-10-29）。0 なら未保存で半分ずつ。</summary>
        [DataMember(Name = "historySplit", EmitDefaultValue = false)]
        public double HistorySplit { get; set; }

        /// <summary>AI の欄を上下2段にしたときの、上の段の高さの割合（2026-10-28）。0 なら未保存で半分ずつ。</summary>
        [DataMember(Name = "aiSplit", EmitDefaultValue = false)]
        public double AiSplit { get; set; }
        /// <summary>ファイラーで最後に見ていたフォルダ。</summary>
        [DataMember(Name = "filerFolder")]
        public string FilerFolder { get; set; }

        /// <summary>
        /// ウィンドウの位置と大きさ（最大化する前のもの）。幅が 0 なら未保存で、既定の位置に出す。
        /// </summary>
        [DataMember(Name = "windowLeft")]
        public double WindowLeft { get; set; }

        [DataMember(Name = "windowTop")]
        public double WindowTop { get; set; }

        [DataMember(Name = "windowWidth")]
        public double WindowWidth { get; set; }

        [DataMember(Name = "windowHeight")]
        public double WindowHeight { get; set; }

        [DataMember(Name = "windowMaximized")]
        public bool WindowMaximized { get; set; }

        /// <summary>画像の行を、リンクの文字だけで見せるか。false（初め）なら画像だけ（SPEC 9章。2026-10-15）。</summary>
        [DataMember(Name = "imageLinkTextOnly")]
        public bool ImageLinkTextOnly { get; set; }

        /// <summary>終了したときに開いていたファイル（スタックのルート）。次の起動で開き直す（SPEC 4章）。無ければ null。</summary>
        [DataMember(Name = "lastFile", EmitDefaultValue = false)]
        public string LastFile { get; set; }

        /// <summary>
        /// 終了したときの画面の状態（開いていたページの並び・カーソル・ランチャー・AI の欄など。2026-10-25）。
        /// 次の起動でそのとおりに戻す。無ければ null（LastFile のルートを開く）。
        /// </summary>
        [DataMember(Name = "screen", EmitDefaultValue = false)]
        public ScreenState Screen { get; set; }

        /// <summary>アウトラインでたたんでいた見出し（たたみ状態の鍵。2026-10-25）。次の起動でも、たたんだまま出す。</summary>
        [DataMember(Name = "collapsed", EmitDefaultValue = false)]
        public List<string> Collapsed { get; set; }

        /// <summary>
        /// AI の欄で開いていた会話のタブ（2026-10-27）。次の起動で、それぞれの会話の続き（claude --resume）として開き直す。
        /// claude の会話が始まるたび（フック）とタブを閉じるたびに書く。落ちても残るように、閉じるときを待たない。無ければ null。
        /// </summary>
        [DataMember(Name = "aiTabs", EmitDefaultValue = false)]
        public List<AiTabState> AiTabs { get; set; }

        /// <summary>
        /// 増やした画面（2026-10-31）。作った順。最初の画面の分は上の Screen・Collapsed・AiTabs。
        /// 終了したときに開いていた画面を、次の起動で全部戻すため。画面を「終了」で閉じたら、その分は消す。
        /// </summary>
        [DataMember(Name = "windows", EmitDefaultValue = false)]
        public List<WindowSession> Windows { get; set; }

        // 前回のエクスポートの選び方は、文書ごとに .文書名/state.json に覚える（2026-10-16。前はここ）。

        public AppSettings()
        {
            Normalize();
        }

        /// <summary>
        /// 読み込み後に必ず通す。
        /// DataContract は逆シリアル化でコンストラクタを呼ばないため、
        /// 項目が無いと 0 や null のままになる。
        /// </summary>
        private void Normalize()
        {
            if (Tiles == null) Tiles = new List<LauncherTile>();

            // 壊れた項目と、マイナスの位置・重なったマスは捨てる。重なったら先のものを残す。
            var seen = new HashSet<string>();
            Tiles.RemoveAll(tile => tile == null || string.IsNullOrEmpty(tile.Path)
                || tile.Column < 0 || tile.Row < 0 || tile.Column > LauncherTile.Limit || tile.Row > LauncherTile.Limit
                || !seen.Add(tile.Cell));

            // 会話の番号は Guid の形のものだけ（コマンドに書き込むので、ほかのものは捨てる）。
            if (AiTabs != null) AiTabs.RemoveAll(tab => tab == null || !Guid.TryParse(tab.Session, out _));

            if (Windows == null) Windows = new List<WindowSession>();
            Windows.RemoveAll(window => window == null);
            foreach (var window in Windows)
            {
                if (window.AiTabs != null) window.AiTabs.RemoveAll(tab => tab == null || !Guid.TryParse(tab.Session, out _));
            }

            if (SidebarWidth < 110 || SidebarWidth > 2000) SidebarWidth = 190;
            if (FilerWidth < 180 || FilerWidth > 2000) FilerWidth = 300;
            if (AiWidth < 200 || AiWidth > 4000) AiWidth = 560;
            if (!(AiSplit >= 0.15 && AiSplit <= 0.85)) AiSplit = 0.5;   // NaN もここで直す
            if (!(HistorySplit >= 0.1 && HistorySplit <= 0.9)) HistorySplit = 0.5;

            // 壊れた値や小さすぎる大きさは「未保存」に戻す。最小サイズ（640×400）を下回るものは使わない。
            var sane = WindowWidth >= 640 && WindowHeight >= 400 && WindowWidth <= 20000 && WindowHeight <= 20000
                && !double.IsNaN(WindowLeft) && !double.IsNaN(WindowTop)
                && !double.IsInfinity(WindowLeft) && !double.IsInfinity(WindowTop);
            if (!sane)
            {
                WindowWidth = 0;
                WindowHeight = 0;
            }
        }

        public static string StorePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.json"); }
        }

        /// <summary>
        /// 起動の入口（App）で1回だけ読む。まだ無ければ初めの状態。読めなければ例外で、App は起動しない。
        /// アプリは1つしか動かない（App）ので、読んだあとは手元のものが正しい。保存の前に読み直さない。
        /// </summary>
        public static AppSettings Load()
        {
            if (!File.Exists(StorePath)) return new AppSettings();

            using (var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var settings = new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream) as AppSettings;
                if (settings == null) throw new InvalidDataException("中身が空です。");

                settings.Normalize();
                return settings;
            }
        }

        public void Save()
        {
            try
            {
                byte[] data;
                using (var buffer = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(buffer, this);
                    data = buffer.ToArray();
                }

                // 一時ファイルに書いてから置き換える。直接書くと、書き込み中に
                // 中断されたときに元の設定も失われる。
                PageDocument.WriteAtomically(StorePath, data);
            }
            catch (Exception)
            {
                // 書けなくても編集の邪魔はしない。
            }
        }

        /// <summary>そのマスの登録。無ければ null。</summary>
        public LauncherTile TileAt(int column, int row)
        {
            return Tiles.Find(tile => tile.Column == column && tile.Row == row);
        }

        /// <summary>マスに登録する。すでにあれば置き換える。path が空なら外す。</summary>
        public void SetTile(int column, int row, string path, string name)
        {
            Tiles.RemoveAll(tile => tile.Column == column && tile.Row == row);
            if (!string.IsNullOrEmpty(path))
                Tiles.Add(new LauncherTile { Column = column, Row = row, Path = path, Name = name });
        }

        /// <summary>2つのマスの中身を入れ替える。片方が空なら、移すのと同じ。</summary>
        public void SwapTiles(int column1, int row1, int column2, int row2)
        {
            var first = TileAt(column1, row1);
            var second = TileAt(column2, row2);

            if (first != null) { first.Column = column2; first.Row = row2; }
            if (second != null) { second.Column = column1; second.Row = row1; }
        }
    }

    /// <summary>ランチャーの1マス（SPEC 4章）。位置は列と段で持つ。マス目に決まった大きさは無い。</summary>
    [DataContract]
    public sealed class LauncherTile
    {
        /// <summary>列・段の上限。壊れた値で画面が果てしなく広がらないように。実際に使う分には十分大きい。</summary>
        public const int Limit = 999;

        /// <summary>左から何列目か（0 始まり）。</summary>
        [DataMember(Name = "col")]
        public int Column { get; set; }

        /// <summary>上から何段目か（0 始まり）。</summary>
        [DataMember(Name = "row")]
        public int Row { get; set; }

        /// <summary>起動するもの。ファイル・フォルダ・ショートカット（.lnk）・バッチなど。</summary>
        [DataMember(Name = "path")]
        public string Path { get; set; }

        /// <summary>表示名。空ならファイル名（拡張子なし）。改行を入れてよい（そこで折り返して出す）。</summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>
        /// 自分で選んだアイコンのファイル（.dll・.exe・.ico）。空なら、登録したものから Windows が決めるアイコン。
        /// %SystemRoot% などの環境変数は、展開せずにそのまま残す（別の PC でも同じ場所を指すように）。
        /// </summary>
        [DataMember(Name = "icon", EmitDefaultValue = false)]
        public string IconPath { get; set; }

        /// <summary>IconPath の中の何番目のアイコンか。</summary>
        [DataMember(Name = "iconIndex", EmitDefaultValue = false)]
        public int IconIndex { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;

                var trimmed = (Path ?? string.Empty).TrimEnd('\\', '/');
                var name = System.IO.Path.GetFileNameWithoutExtension(trimmed);
                return string.IsNullOrEmpty(name) ? Path : name;
            }
        }

        /// <summary>マスを見分ける文字列（"列,段"）。</summary>
        internal string Cell
        {
            get { return Column + "," + Row; }
        }
    }

    /// <summary>増やした画面1つの、次の起動で戻すもの（AppSettings.Windows。2026-10-31）。中身は最初の画面の Screen・Collapsed・AiTabs と同じ。</summary>
    [DataContract]
    public sealed class WindowSession
    {
        [DataMember(Name = "screen", EmitDefaultValue = false)]
        public ScreenState Screen { get; set; }

        [DataMember(Name = "collapsed", EmitDefaultValue = false)]
        public List<string> Collapsed { get; set; }

        [DataMember(Name = "aiTabs", EmitDefaultValue = false)]
        public List<AiTabState> AiTabs { get; set; }
    }

    /// <summary>AI の欄の会話のタブ1つ（AppSettings.AiTabs）。</summary>
    [DataContract]
    public sealed class AiTabState
    {
        /// <summary>claude の会話の番号（session_id）。</summary>
        [DataMember(Name = "session")]
        public string Session { get; set; }

        /// <summary>claude が動いていたフォルダ。会話はフォルダごとに記録されるので、同じフォルダで開き直す。</summary>
        [DataMember(Name = "folder", EmitDefaultValue = false)]
        public string Folder { get; set; }

        /// <summary>claude のセッションの名前（1・2…。タブの見出し。2026-11-01）。次の起動でも同じ名前にする。</summary>
        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; }
    }
}

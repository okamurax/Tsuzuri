using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AiLink
{
    /// <summary>
    /// exe と同じフォルダの ailink.json。持ち運べるように %AppData% は使わない（Tsuzuri の app.json と同じ考え）。
    /// ランチャーの登録は利用者が明示的にしたものなので、ファイルが一時的に見つからなくても勝手に消さない。
    /// </summary>
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>ランチャーのマス。登録のあるマスだけを持つ。</summary>
        [DataMember(Name = "launcher")]
        public List<LauncherTile> Tiles { get; set; }

        /// <summary>左の列（ランチャー・ファイラー・Excel の一覧）の幅。</summary>
        [DataMember(Name = "launcherWidth")]
        public double LauncherWidth { get; set; }

        /// <summary>左の列の上の段（ランチャーのマス）の高さ。</summary>
        [DataMember(Name = "launcherHeight")]
        public double LauncherHeight { get; set; }

        /// <summary>左の列の下の段（Excel の一覧）の高さ。</summary>
        [DataMember(Name = "excelListHeight")]
        public double ExcelListHeight { get; set; }

        /// <summary>AI の欄の共通ターミナルを出したときの、上の段（会話）の高さの割合。</summary>
        [DataMember(Name = "aiShellSplit", EmitDefaultValue = false)]
        public double AiShellSplit { get; set; }

        /// <summary>ファイラーで最後に見ていたフォルダ。</summary>
        [DataMember(Name = "filerFolder")]
        public string FilerFolder { get; set; }

        /// <summary>ウィンドウの位置と大きさ（最大化する前のもの）。幅が 0 なら未保存で、既定の位置に出す。</summary>
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

        /// <summary>画面をグラフィックス（GPU）を使わずに描くか。手で ailink.json に true と書いて使う（仮想マシンで窓がちらつくとき）。</summary>
        [DataMember(Name = "softwareRendering")]
        public bool SoftwareRendering { get; set; }

        /// <summary>
        /// AI の実行ファイルの場所（任意）。空なら AI のつなぎ口（IAgent）が決まった場所と PATH から探す。
        /// 会社の PC で入れ場所が違うときに、手で ailink.json に書く。
        /// </summary>
        [DataMember(Name = "agentExecutable", EmitDefaultValue = false)]
        public string AgentExecutable { get; set; }

        /// <summary>開いていた会話のタブ。次の起動で、それぞれの会話の続きとして開き直す。</summary>
        [DataMember(Name = "aiTabs", EmitDefaultValue = false)]
        public List<AiTabState> AiTabs { get; set; }

        public AppSettings()
        {
            Normalize();
        }

        /// <summary>読み込み後に必ず通す（DataContract は逆シリアル化でコンストラクタを呼ばないため）。</summary>
        private void Normalize()
        {
            if (Tiles == null) Tiles = new List<LauncherTile>();
            var seen = new HashSet<string>();
            Tiles.RemoveAll(tile => tile == null || string.IsNullOrEmpty(tile.Path)
                || tile.Column < 0 || tile.Row < 0 || tile.Column > LauncherTile.Limit || tile.Row > LauncherTile.Limit
                || !seen.Add(tile.Cell));

            // 会話の番号は Guid の形のものだけ（コマンドに書き込むので、ほかのものは捨てる）
            if (AiTabs != null) AiTabs.RemoveAll(tab => tab == null || !Guid.TryParse(tab.Session, out _));

            if (!(LauncherWidth >= 220 && LauncherWidth <= 3000)) LauncherWidth = 380;   // NaN もここで直す
            if (!(LauncherHeight >= 60 && LauncherHeight <= 3000)) LauncherHeight = 200;
            if (!(ExcelListHeight >= 60 && ExcelListHeight <= 3000)) ExcelListHeight = 180;
            if (!(AiShellSplit >= 0.15 && AiShellSplit <= 0.85)) AiShellSplit = 0.6;

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
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ailink.json"); }
        }

        /// <summary>起動の入口（App）で1回だけ読む。まだ無ければ初めの状態。読めなければ例外で、App は起動しない。</summary>
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
                // 一時ファイルに書いてから置き換える（書き込み中に止まっても、元の設定を失わない）
                var temp = StorePath + ".tmp";
                File.WriteAllBytes(temp, data);
                if (File.Exists(StorePath)) File.Replace(temp, StorePath, null);
                else File.Move(temp, StorePath);
            }
            catch (Exception)
            {
                // 書けなくても作業の邪魔はしない
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

    /// <summary>ランチャーの1マス。位置は列と段で持つ（Tsuzuri と同じ）。</summary>
    [DataContract]
    public sealed class LauncherTile
    {
        /// <summary>列・段の上限。壊れた値で画面が果てしなく広がらないように。</summary>
        public const int Limit = 999;

        [DataMember(Name = "col")]
        public int Column { get; set; }

        [DataMember(Name = "row")]
        public int Row { get; set; }

        /// <summary>起動するもの。ファイル・フォルダ・ショートカット（.lnk）・バッチなど。</summary>
        [DataMember(Name = "path")]
        public string Path { get; set; }

        /// <summary>表示名。空ならファイル名（拡張子なし）。</summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>自分で選んだアイコンのファイル（.dll・.exe・.ico）。環境変数は展開せずに残す。</summary>
        [DataMember(Name = "icon", EmitDefaultValue = false)]
        public string IconPath { get; set; }

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

    /// <summary>AI の欄の会話のタブ1つ（AppSettings.AiTabs）。</summary>
    [DataContract]
    public sealed class AiTabState
    {
        /// <summary>AI の会話の番号。</summary>
        [DataMember(Name = "session")]
        public string Session { get; set; }

        /// <summary>AI が動いていたフォルダ（同じフォルダで開き直す）。</summary>
        [DataMember(Name = "folder", EmitDefaultValue = false)]
        public string Folder { get; set; }

        /// <summary>タブの名前（#1・#2…）。次の起動でも同じ名前にする。</summary>
        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; }

        /// <summary>この会話のプロジェクトフォルダ。無ければ null。</summary>
        [DataMember(Name = "project", EmitDefaultValue = false)]
        public string Project { get; set; }

        /// <summary>この会話の題。無ければ null。</summary>
        [DataMember(Name = "topic", EmitDefaultValue = false)]
        public string Topic { get; set; }
    }
}

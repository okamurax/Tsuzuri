using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AiLink
{
    /// <summary>
    /// exe と同じフォルダの ailink.json。持ち運べるように %AppData% は使わない（Tsuzuri の app.json と同じ考え）。
    /// </summary>
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>左の列（開いている Excel の一覧）の幅。</summary>
        [DataMember(Name = "sidebarWidth")]
        public double SidebarWidth { get; set; }

        /// <summary>AI の欄の共通ターミナルを出したときの、上の段（会話）の高さの割合。</summary>
        [DataMember(Name = "aiShellSplit", EmitDefaultValue = false)]
        public double AiShellSplit { get; set; }

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
            // 会話の番号は Guid の形のものだけ（コマンドに書き込むので、ほかのものは捨てる）
            if (AiTabs != null) AiTabs.RemoveAll(tab => tab == null || !Guid.TryParse(tab.Session, out _));

            if (!(SidebarWidth >= 180 && SidebarWidth <= 3000)) SidebarWidth = 300;   // NaN もここで直す
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

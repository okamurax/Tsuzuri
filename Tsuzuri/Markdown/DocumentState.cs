using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// ファイルごとの状態（カーソルの行・見出しのブックマーク。SPEC 4章・7章）。
    /// 文書の添付フォルダ（.文書名/）の state.json に置き、文書と一緒に持ち運ぶ。
    /// 前は app.json に置いていたが、ファイルごとのものなので移した（2026-10-14。前の分は引き継がない）。
    /// </summary>
    [DataContract]
    public sealed class DocumentState
    {
        private const string FileName = "state.json";

        /// <summary>ルートのページのカーソルの行（1 始まり）。0 なら覚えていない。</summary>
        [DataMember(Name = "line")]
        public int Line { get; set; }

        // その文書を前回エクスポートしたときの選び方（SPEC 13章。2026-10-16。前は app.json で、全部の文書で共通）。
        // 項目が無い（初めて）ときに既定（全体・A4・縦・20mm・本文に戻す）になるよう、false・空・0 が既定になる形で持つ。

        /// <summary>
        /// 範囲を「現在のページ」にしたか（2026-10-19。前は覚えず毎回「全体」から）。
        /// 「現在のページ」はサブページを見ているときだけ選べるので、ルートを見ているときは「全体」になる。
        /// </summary>
        [DataMember(Name = "exportCurrentPage", EmitDefaultValue = false)]
        public bool ExportCurrentPage { get; set; }

        /// <summary>用紙。"A3" 以外は A4。</summary>
        [DataMember(Name = "exportPaper", EmitDefaultValue = false)]
        public string ExportPaper { get; set; }

        [DataMember(Name = "exportLandscape", EmitDefaultValue = false)]
        public bool ExportLandscape { get; set; }

        /// <summary>余白（mm）。10・15・20・25・30 のどれか。それ以外（0 など）は 20mm。</summary>
        [DataMember(Name = "exportMargin", EmitDefaultValue = false)]
        public int ExportMarginMm { get; set; }

        /// <summary>サブページを除外するか。false なら本文に戻す。</summary>
        [DataMember(Name = "exportExcludeSubpages", EmitDefaultValue = false)]
        public bool ExportExcludeSubpages { get; set; }

        /// <summary>見出しのブックマーク。新しいものが先頭。無ければ null（書き出さない）。</summary>
        [DataMember(Name = "headings", EmitDefaultValue = false)]
        public List<HeadingMark> Headings { get; set; }

        /// <summary>
        /// その文書の状態を読む。まだ無ければ空の状態。読めなければ例外で、そのファイルは開かない（MainWindow.OpenFile）。
        /// 開くときに1回だけ読み、あとは手元のものを書く。
        /// </summary>
        public static DocumentState Load(string documentPath)
        {
            var path = PathOf(documentPath);
            if (!File.Exists(path)) return new DocumentState();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var state = new DataContractJsonSerializer(typeof(DocumentState)).ReadObject(stream) as DocumentState;
                if (state == null) throw new InvalidDataException("中身が空です。");

                state.Normalize();
                return state;
            }
        }

        /// <summary>その文書の状態を書く。書けなければ例外。添付フォルダは開くときに必ず作ってある（MainWindow.OpenFile）。</summary>
        public void Save(string documentPath)
        {
            byte[] data;
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DocumentState)).WriteObject(buffer, this);
                data = buffer.ToArray();
            }

            PageDocument.WriteAtomically(PathOf(documentPath), data);
        }

        public static string PathOf(string documentPath)
        {
            return Path.Combine(Attachments.FolderOf(documentPath), FileName);
        }

        private void Normalize()
        {
            if (Headings == null) return;

            Headings.RemoveAll(mark => mark == null || string.IsNullOrEmpty(mark.Text));
            foreach (var mark in Headings)
            {
                if (mark.Page == null) mark.Page = string.Empty;
            }
            if (Headings.Count == 0) Headings = null;
        }
    }

    /// <summary>保存しておく見出しのブックマーク1つ（SPEC 7章）。行番号ではなく見出しの文字で覚える。</summary>
    [DataContract]
    public sealed class HeadingMark
    {
        /// <summary>ファイルの中のページ。空ならルート。</summary>
        [DataMember(Name = "page")]
        public string Page { get; set; }

        [DataMember(Name = "text")]
        public string Text { get; set; }

        /// <summary>最後に見た行（1 始まり）。同じ文字の見出しが複数あるときの手がかり。</summary>
        [DataMember(Name = "line")]
        public int Line { get; set; }

        /// <summary>登録したときの画面の状態（2026-10-25）。無ければ（前の版で登録したもの）見出しのページをそのまま開く。</summary>
        [DataMember(Name = "screen", EmitDefaultValue = false)]
        public ScreenState Screen { get; set; }
    }
}

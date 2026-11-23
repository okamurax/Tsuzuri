using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;

namespace DrawingEditor
{
    /// <summary>図エディタで保存したもの。描き直すための JSON と、本文に表示する画像（PNG）。</summary>
    public sealed class DrawingResult
    {
        public string Json { get; internal set; }
        public byte[] Png { get; internal set; }
    }

    /// <summary>
    /// エディタ本体（Tsuzuri）から図エディタを使う入口（SPEC 8.4.3。2026-10-18）。
    ///
    /// 図エディタそのものは本体の画面の中に重ねて出す部品（DrawingEditorView。2026-11-23。前は閉じるまで本体を触れない別の窓）。
    /// 図は文書の添付フォルダに、同じ名前の .json（描き直すため）と .png（本文に表示する・Word に載せる）で置く。置くのは本体の役目。
    /// </summary>
    public static class DrawingEditorHost
    {
        /// <summary>この JSON が図エディタの図か（同じ名前の .json が別の物なら、図として開かない）。</summary>
        public static bool IsDrawing(string json)
        {
            return DrawingFile.Read(json) != null;
        }

        /// <summary>
        /// 図エディタを開かずに、図（.json の中身）から本文に表示する画像（PNG）を作る（2026-11-01。AI が書いた図に使う。SPEC 8.4.3）。
        /// 線のつながり（startShape・endShape）は、相手の図形の位置に合わせてから描く（図エディタで開いたときと同じ見え方）。
        /// 作れなければ null を返し、error に何がいけないかを書く（AI が読んで直せるように、具体的に）。
        /// </summary>
        /// <param name="dpiScale">画面の拡大率（図エディタで保存したときと同じ画素の数にする）。</param>
        public static byte[] RenderPng(string json, double dpiScale, out string error)
        {
            var file = DrawingFile.Read(json);
            if (file == null)
            {
                error = "図エディタの図として読めません。" + WhyNotDrawing(json);
                return null;
            }

            if (file.Version > DrawingFile.SupportedVersion)
            {
                error = "version が " + file.Version + " です。この版で描ける図は version " + DrawingFile.SupportedVersion + " までです。";
                return null;
            }

            if (file.Dropped > 0)
            {
                error = "知らない type の図形が " + file.Dropped + " 個あります。type は line・rect・ellipse・text・dimension のどれかにしてください。";
                return null;
            }

            Geometry.UpdateConnections(file.Shapes);

            try
            {
                error = null;
                return Renderer.RenderPng(file.Shapes, file.Board, file.Unit, dpiScale);
            }
            catch (Exception ex)
            {
                error = "画像を作れませんでした: " + ex.Message;
                return null;
            }
        }

        /// <summary>図として読めなかったわけ。JSON の書き方の誤りなら、その知らせ（行・桁が入る）。</summary>
        private static string WhyNotDrawing(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "ファイルが空です。";

            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    new DataContractJsonSerializer(typeof(DrawingFile)).ReadObject(stream);
                }
            }
            catch (Exception ex)
            {
                return "JSON として読めません: " + ex.Message;
            }

            return "\"kind\": \"" + DrawingFile.Marker + "\" がありません。";
        }
    }
}

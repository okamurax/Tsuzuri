using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Tsuzuri.Views;

namespace Tsuzuri.Office
{
    /// <summary>
    /// Outlook（classic）を AI に扱わせるつなぎ（2026-11-26。利用者の考え。Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする）。
    /// ・一覧（OutlookListView）で選んだフォルダ（パスだけ）を覚え、今の状態の「Outlook」の段に書く（AppendContext）
    /// ・AI の台本 outlook.ps1 の頼み（outlook-folders・outlook-search・outlook-read・outlook-draft）に、パイプのスレッドで答える（Serve）
    /// できるのは、フォルダの一覧・探す・1通を読む・下書きに保存する、だけ。送信・削除・移動・既読にするなどの道は作らない（利用者の判断）。
    /// 結果は AI のフォルダのファイルに書いて、そのパスを返す（頼みごとに別の名前。半日より古いものは DeleteOld で消す）。
    /// </summary>
    public sealed class OutlookLink
    {
        private readonly string _aiFolder;
        private readonly string _listName;
        private readonly Action _changed;

        /// <summary>選んだフォルダのパス（選んだ順）。パイプのスレッドからも読むので、配列ごと入れ替える（中は変えない）。</summary>
        private volatile string[] _choices = new string[0];

        private List<OutlookBridge.Store> _stores;
        private DateTime _readAt = DateTime.MinValue;
        private bool _reading;

        /// <summary>一覧の表示。アプリが Excel の一覧の下に置く（OutlookListView.Stack）。</summary>
        public OutlookListView View { get; private set; }

        /// <param name="aiFolder">結果のファイルを書く AI のフォルダ（台本を置いた所）。</param>
        /// <param name="listName">今の状態で一覧を指す呼び名（Tsuzuri は「ランチャーの Outlook の一覧」、AiLink は「Outlook の一覧」）。</param>
        /// <param name="changed">選んだフォルダが変わった（下端の行を出し直す）。画面のスレッドで呼ぶ。</param>
        public OutlookLink(string aiFolder, string listName, Action changed)
        {
            _aiFolder = aiFolder;
            _listName = listName;
            _changed = changed;
            View = new OutlookListView();
            View.RefreshRequested += () => Refresh(true);
            View.RowClicked += Choose;
            View.EmptyClicked += () => SetChoices(new string[0]);
        }

        /// <summary>
        /// 一覧を裏で読み直す（画面のスレッドから）。force でなければ、まだ一度も読んでいないときだけ読む
        /// （フォルダの多いメールボックスでは時間がかかり、AI の頼みとぶつかるので、窓に戻るたびには読まない。あとは「更新」と -Folders のとき。2026-11-26 のレビュー）。
        /// </summary>
        public void Refresh(bool force)
        {
            if (_reading) return;
            // 前に読めなかったとき（Outlook を開く前に起動したなど）は、60 秒おきに読み直す
            if (!force && _readAt != DateTime.MinValue && (_stores != null || (DateTime.UtcNow - _readAt).TotalSeconds < 60)) return;
            _reading = true;
            _readAt = DateTime.UtcNow;
            var dispatcher = View.Dispatcher;
            Task.Run(() =>
            {
                string problem;
                var stores = OutlookBridge.ListStores(45000, out problem);
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _reading = false;
                    if (stores != null) Apply(stores);
                    else Show(problem);
                }));
            });
        }

        /// <summary>
        /// 読んだ一覧に入れ替え、無くなったフォルダを選びから外す。外すのは、全部を読めたストア（Complete）の中で一覧に無いものだけ
        /// （読めなかったストア・上限で切ったストアの選びは残す。一時的に切れた共有メールボックスなどで、選びを黙って外さないように）。
        /// </summary>
        private void Apply(List<OutlookBridge.Store> stores)
        {
            _stores = stores;
            var known = new HashSet<string>(stores.SelectMany(store => store.Folders).Select(folder => folder.Path), StringComparer.OrdinalIgnoreCase);
            var complete = stores.Where(store => store.Complete && store.Folders.Count > 0)
                                 .Select(store => Root(store.Folders[0].Path)).Where(root => root.Length > 0).ToList();
            var kept = _choices.Where(path => known.Contains(path) || !complete.Contains(Root(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (kept.Length != _choices.Length) SetChoices(kept);
            else Show(null);
        }

        /// <summary>パスのストアの部分（\\メールボックス）。</summary>
        private static string Root(string path)
        {
            if (!path.StartsWith("\\\\", StringComparison.Ordinal)) return string.Empty;
            var cut = path.IndexOf('\\', 2);
            return cut < 0 ? path : path.Substring(0, cut);
        }

        private void Show(string problem)
        {
            var chosen = new HashSet<string>(_choices, StringComparer.OrdinalIgnoreCase);
            // 一覧に出す知らせは利用者向けに（台本への答えは AI 向けの言い方なので、「開いてもらう」を言い換える）
            View.Show(_stores, path => chosen.Contains(path), _stores == null && problem != null ? problem.Replace("開いてもらう", "開いてください") : null);
        }

        /// <summary>行を押した: そのフォルダだけを選ぶ。Ctrl なら足す・外す。</summary>
        private void Choose(string path, bool ctrl)
        {
            var current = _choices;
            string[] next;
            if (ctrl)
            {
                next = current.Contains(path, StringComparer.OrdinalIgnoreCase)
                    ? current.Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : current.Concat(new[] { path }).ToArray();
            }
            else
            {
                next = new[] { path };
            }
            SetChoices(next);
        }

        private void SetChoices(string[] next)
        {
            _choices = next;
            Show(null);
            if (_changed != null) _changed();
        }

        /// <summary>今の状態の「Outlook」の段（1行）。</summary>
        public void AppendContext(StringBuilder text)
        {
            var choices = _choices;
            if (choices.Length == 0)
            {
                text.Append("Outlook: ").Append(_listName).Append("でフォルダを選んでいない（前に選んでいても、今は外されている）\n");
                return;
            }
            text.Append("Outlook: 利用者が").Append(_listName.Length > 0 && _listName[0] < 128 ? " " : string.Empty).Append(_listName).Append("で選んだフォルダ（").Append(choices.Length).Append(" 個。渡すのはフォルダのパスだけで、メールは渡していない）: ");
            text.Append(string.Join("、", choices.Select(path => "「" + path + "」"))).Append('\n');
        }

        /// <summary>AI の欄の下端の行に足す分（「  ・Outlook 受信トレイ」。2つ以上なら数）。</summary>
        public string SummaryPart()
        {
            var choices = _choices;
            if (choices.Length == 0) return string.Empty;
            if (choices.Length > 1) return "  ・Outlook " + choices.Length + " フォルダ";
            var path = choices[0].TrimEnd('\\');
            var cut = path.LastIndexOf('\\');
            return "  ・Outlook " + (cut >= 0 ? path.Substring(cut + 1) : path);
        }

        /// <summary>outlook.ps1 の頼みか。</summary>
        public static bool Handles(string request)
        {
            return request != null && request.StartsWith("outlook-", StringComparison.Ordinal);
        }

        /// <summary>outlook.ps1 の頼みに答える（パイプのスレッドから）。答えは「ok→文」か「error→わけ」の1行。</summary>
        public string Serve(string request)
        {
            var parts = request.Split('\t');
            // 終わりの印（送る途中で止められた頼みを、そろった頼みと取り違えない。excel.ps1 と同じ）
            if (parts.Length < 2 || parts[parts.Length - 1] != "end") return "error\t頼みが途中で切れていた。何もしていない";
            var fields = new List<string>();
            try
            {
                for (var i = 1; i < parts.Length - 1; i++) fields.Add(Encoding.UTF8.GetString(Convert.FromBase64String(parts[i])));
            }
            catch (FormatException)
            {
                return "error\t頼みを読めなかった。何もしていない";
            }

            switch (parts[0])
            {
                case "outlook-folders":
                    return fields.Count == 0 ? ServeFolders() : "error\t頼みの形が違う。何もしていない";
                case "outlook-search":
                    return fields.Count == 8 ? ServeSearch(fields) : "error\t頼みの形が違う。何もしていない";
                case "outlook-read":
                    return fields.Count == 2 ? ServeRead(fields[0].Trim(), fields[1].Trim()) : "error\t頼みの形が違う。何もしていない";
                case "outlook-draft":
                    return fields.Count == 4 ? ServeDraft(fields) : "error\t頼みの形が違う。何もしていない";
                default:
                    return "error\t知らない頼み。何もしていない";
            }
        }

        private string ServeFolders()
        {
            string problem;
            var stores = OutlookBridge.ListStores(45000, out problem);
            if (stores == null) return "error\t" + problem;
            View.Dispatcher.BeginInvoke(new Action(() =>
            {
                _readAt = DateTime.UtcNow;
                Apply(stores);
            }));

            var output = new StringBuilder();
            output.Append("# Outlook のメールのフォルダ（ストアごと。1行に1つのパス。メールの中身は含まない）\n");
            foreach (var store in stores)
            {
                output.Append("# ストア: ").Append(store.Name);
                if (!string.IsNullOrEmpty(store.Problem)) output.Append("（").Append(store.Problem).Append("）");
                output.Append('\n');
                foreach (var folder in store.Folders) output.Append(folder.Path).Append('\n');
            }
            return WriteResult("outlook-folders-", output.ToString(), "フォルダ " + stores.Sum(store => store.Folders.Count) + " 個");
        }

        private string ServeSearch(List<string> fields)
        {
            var folder = fields[0].Trim();
            DateTime? since, until;
            if (!ParseDate(fields[4].Trim(), out since) || !ParseDate(fields[5].Trim(), out until)) return "error\t日付は 2026-11-01 の形で付ける。何もしていない";
            int limit;
            if (!int.TryParse(fields[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 200)
                return "error\t-Limit は 1〜200。何もしていない";

            string problem;
            var found = OutlookBridge.Search(folder, fields[1].Trim(), fields[2].Trim(), fields[3].Trim(), since, until, fields[6].Trim() == "1", limit, 75000, out problem);
            if (found == null) return "error\t" + problem;
            return WriteResult("outlook-search-", found.Text, found.Summary);
        }

        private string ServeRead(string folder, string id)
        {
            string problem;
            var text = OutlookBridge.Read(folder, id, 60000, out problem);
            if (text == null) return "error\t" + problem;
            return WriteResult("outlook-read-", text, "1 通");
        }

        private string ServeDraft(List<string> fields)
        {
            string problem;
            var saved = OutlookBridge.SaveDraft(fields[0].Trim(), fields[1].Trim(), fields[2].Trim() == "1", fields[3], 60000, out problem);
            if (saved == null) return "error\t" + problem;
            return "ok\t" + saved;
        }

        private static bool ParseDate(string text, out DateTime? date)
        {
            date = null;
            if (text.Length == 0) return true;
            DateTime parsed;
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return false;
            date = parsed;
            return true;
        }

        private string WriteResult(string prefix, string content, string summary)
        {
            var file = Path.Combine(_aiFolder, prefix + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt");   // 頼みごとに別の名前（会話が2つ以上あっても上書きしない）
            try
            {
                Directory.CreateDirectory(_aiFolder);
                File.WriteAllText(file, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "error\t書き出せなかった: " + ex.Message.Replace("\r", " ").Replace("\n", " ");
            }
            return "ok\t" + file + "（" + summary + "）";
        }

        /// <summary>
        /// 古い結果のファイル（半日より古いもの）と、送ったあと消せずに残った下書きの作業ファイルを消す（起動したとき・会話を始めるとき）。
        /// workFolders は、AI が下書きの作業ファイルを書くフォルダ（Tsuzuri は AI のフォルダ、AiLink は会話のフォルダも）。
        /// </summary>
        public static void DeleteOld(string aiFolder, params string[] workFolders)
        {
            var limit = DateTime.UtcNow.AddHours(-12);
            Action<string, string> sweep = (folder, pattern) =>
            {
                try
                {
                    if (!Directory.Exists(folder)) return;
                    foreach (var file in Directory.GetFiles(folder, pattern))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                catch (Exception)
                {
                }
            };
            // 下書きの作業ファイルも、送らずに残ったものは古ければ消す（前の会話が書いたまま、同じ番号の新しい会話が送ってしまわないように）
            foreach (var pattern in new[] { "outlook-folders-*.txt", "outlook-search-*.txt", "outlook-read-*.txt", "outlook-draft-*.txt" }) sweep(aiFolder, pattern);
            foreach (var folder in workFolders ?? new string[0]) sweep(folder, "outlook-draft-*.txt");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>開いたあとに外でファイルが書き換わっていたので、保存をやめた。</summary>
    public sealed class ExternalChangeException : IOException
    {
        public ExternalChangeException(string message) : base(message)
        {
        }
    }

    /// <summary>ファイルの中の1ページぶん。Id が空ならルート。</summary>
    public sealed class PageRegion
    {
        public readonly string Id;
        public string Text;

        public PageRegion(string id, string text)
        {
            Id = id;
            Text = text;
        }
    }

    /// <summary>
    /// ファイル1つぶん。中に複数のページを持つ（SPEC 5章）。
    ///
    /// サブページを別ファイルにしない理由:
    /// 切り出しと戻しを日常的な操作にすると、ファイルの作成と削除が日常的になり、
    /// 名前の付け方・削除の扱い・孤立ファイル・リンク切れ・パス解決が全部ついてくる。
    /// 同じファイルの中の区画にすれば、切り出しは印を1行足すだけ、
    /// 戻すのは1行消すだけになり、その一切が発生しない。
    /// </summary>
    public sealed class PageDocument
    {
        /// <summary>ルートページの Id。</summary>
        public const string RootId = "";

        // 字下げは3つまで。4つ以上やタブは字下げコードの中身なので、印として読まない。
        private static readonly Regex MarkerRe = new Regex(
            @"^ {0,3}<!--page:[ \t]*([A-Za-z0-9_-]+)[ \t]*-->[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex FenceRe = new Regex(
            @"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.Compiled);

        private readonly List<PageRegion> _pages = new List<PageRegion>();

        private string _filePath;
        private string _newLine = "\n";
        private bool _hadBom;
        private DateTime _stamp;

        // 開いた（読み直した・保存した）ときのファイルの大きさ。時刻と合わせて「外で変わったか」を見る（2026-10-24）。
        // \\Mac\ 共有は時刻の細かさが粗く、書き換えても時刻が同じに見えることがあるため。
        private long _length;

        // 開いた（読み直した）ときに読んだバイト列・保存したときに書いたバイト列の SHA-256（2026-10-27）。
        // 時刻も大きさも同じまま中身だけ変わる書き換え（同じ秒のうちに同じ長さで書かれた等）を、保存の直前に見分けるため。
        private byte[] _hash;
        private bool _dirty;

        // このセッションで消したページの Id。使い回すと、元に戻したリンクが別のページを指してしまう。
        private readonly HashSet<string> _retired = new HashSet<string>(StringComparer.Ordinal);

        public string FilePath
        {
            get { return _filePath; }
        }

        public bool IsDirty
        {
            get { return _dirty; }
        }

        public IList<PageRegion> Pages
        {
            get { return _pages; }
        }

        /// <summary>保存するときと同じ形の、ファイル全体の文字（未保存の編集を含む）。</summary>
        public string ComposeText()
        {
            return Serialize();
        }

        // =====================================================================
        // 読み書き
        // =====================================================================

        public static PageDocument Load(string path)
        {
            DateTime stamp;
            long length;
            var bytes = ReadStable(path, out stamp, out length);

            return FromBytes(path, bytes, stamp, length);
        }

        /// <summary>
        /// 読む前と読んだ後で、時刻と大きさが同じで、読んだ量も大きさと同じになるまで読み直す（2026-10-24）。
        /// ほかのアプリ（AI 等）が書いている途中を読むと、切れた中身を最新と思い込み、
        /// その後の書き換えを見逃したうえ、切れた中身で上書き保存しかねないため。
        /// </summary>
        private static byte[] ReadStable(string path, out DateTime stamp, out long length)
        {
            byte[] bytes;
            for (var attempt = 0; ; attempt++)
            {
                Probe(path, out stamp, out length);
                bytes = File.ReadAllBytes(path);

                DateTime after;
                long afterLength;
                Probe(path, out after, out afterLength);

                if (after == stamp && afterLength == length && bytes.LongLength == length) return bytes;
                if (attempt >= 4) throw new IOException("ファイルが書き込みの途中のため、読めませんでした。少し置いてから開き直してください。");

                System.Threading.Thread.Sleep(150);
            }
        }

        /// <summary>
        /// 読み直す前に、ファイルが書き終わって落ち着くのを待つ（2026-10-27）。
        /// 中身を空にしてから書き直すアプリだと、空にした直後・書いている途中でも、読む前後で時刻と大きさが揃うことがあり、
        /// ReadStable だけでは切れた中身を「落ち着いた」と思い込む。最後に書かれてから 1 秒たつまで読まない。
        /// 共有（\\Mac\）の時計がこちらとずれていても待ち続けないよう、ずれているときは、こちらの時計で 1 秒、時刻と大きさが変わらないのを見届ける。
        /// 3 秒たっても落ち着かなければ投げる（呼び出し側は今の中身のまま、次のきっかけで読み直す）。
        /// </summary>
        private static void WaitUntilQuiet(string path)
        {
            var quiet = TimeSpan.FromSeconds(1);

            DateTime stamp;
            long length;
            Probe(path, out stamp, out length);

            var age = DateTime.UtcNow - stamp;
            if (age >= quiet) return;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var quietSince = TimeSpan.Zero;

            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                System.Threading.Thread.Sleep(200);

                DateTime now;
                long nowLength;
                Probe(path, out now, out nowLength);

                if (now != stamp || nowLength != length)
                {
                    stamp = now;
                    length = nowLength;
                    quietSince = watch.Elapsed;
                    continue;
                }

                if (DateTime.UtcNow - stamp >= quiet || watch.Elapsed - quietSince >= quiet) return;
            }

            throw new IOException("ファイルが書き込みの途中のようなので、読み直しを見送りました。");
        }

        private static PageDocument FromBytes(string path, byte[] bytes, DateTime stamp, long length)
        {
            var bom = HasUtf8Bom(bytes);
            var offset = bom ? 3 : 0;

            string text;
            try
            {
                // UTF-8 として読めないファイルは開かない。
                // 読めたことにすると不正なバイトが置換文字になり、保存でそれを書き戻して
                // 原本を壊す。元のバイト列は二度と戻せない（SPEC 10章は UTF-8 のみ）。
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                throw new IOException(
                    "UTF-8 として読めませんでした。このエディタは UTF-8 のファイルだけを扱います。");
            }

            var document = new PageDocument();
            document._filePath = path;
            document._hadBom = bom;
            document._newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            document._stamp = stamp;
            document._length = length;
            document._hash = HashOf(bytes);
            document.Parse(text);

            return document;
        }

        private static byte[] HashOf(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return sha.ComputeHash(bytes);
            }
        }

        private static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <param name="overwrite">外で書き換わっていても上書きする。利用者が「上書き保存」を選んだときだけ。</param>
        public void Save(bool overwrite = false)
        {
            // 開いたあとに外で書き換わっていたら、こちらの内容で塗り潰さない。
            // 消された・移されたときも時刻が変わる（無いファイルの時刻は 1601 年）ので、ここで止まる。
            // 止めたら、呼び出し側が上書きするか尋ねる（MainWindow.SaveDocument）。
            // 時刻と大きさが同じでも、中身を読んで比べる（2026-10-27。同じ秒・同じ長さの書き換えを見逃さないため）。
            if (!overwrite && IsContentChangedOnDisk())
                throw new ExternalChangeException("このファイルは開いたあとに外部で変更・移動・削除されています。上書きを中止しました。");

            var text = Serialize();

            // 書いたものを読み直して同じページの並びに戻るか確かめる。
            // 戻らないまま書くと、次に開いたときにページが割れたり混ざったりする。
            if (!SamePageIds(ParsePages(text), _pages))
                throw new IOException("ページの区切りを正しく書き出せないため、保存を中止しました。");

            var data = Encode(text, _hadBom);
            var written = HashOf(data);

            // \\Mac\ 共有は書くのが遅く、その間に外（AI 等）で書き換わることがある。置き換える直前にもう一度確かめる（2026-10-24）。
            // 隙間は狭くなるが、無くはならない。ここは隙間を広げないよう、時刻と大きさだけで見る。
            WriteAtomically(_filePath, data, () =>
            {
                if (!overwrite && IsChangedOnDisk)
                    throw new ExternalChangeException("保存している間に、外部でファイルが変更されました。上書きを中止しました。");
            });

            AdoptWritten(written);
        }

        /// <summary>
        /// 書いたあとの時刻と大きさを、自分の書いたものとして覚える（2026-10-27）。
        /// 前は書いたあとの時刻と大きさをそのまま覚えていたので、置き換えた直後に外（AI 等）が書いていると、
        /// その書き換えを自分のものと思い込み、以後見逃した。読み直して、書いたバイト列と同じときだけ覚える。
        /// 違えば、書いたあとに外で書き換わったので、未保存のままにして投げる（呼び出し側が上書きするか尋ねる。画面の内容は残る）。
        /// 読み直せなければ（共有が一瞬見えない等）、時刻と大きさだけ覚え、中身の比べは次の保存の直前に任せる（書いたバイト列の SHA-256 で比べる）。
        /// </summary>
        private void AdoptWritten(byte[] written)
        {
            DateTime stamp;
            long length;
            byte[] back;

            try
            {
                back = ReadStable(_filePath, out stamp, out length);
            }
            catch (Exception)
            {
                Probe(_filePath, out _stamp, out _length);
                _hash = written;
                _dirty = false;
                return;
            }

            if (!SameHash(HashOf(back), written))
            {
                // 読み直しで必ず「外で変わった」になるよう、ありえない値にしておく。
                _stamp = DateTime.MinValue;
                _length = -2;
                _hash = null;
                _dirty = true;
                throw new ExternalChangeException("保存した直後に、外部でファイルが書き換えられました。");
            }

            _stamp = stamp;
            _length = length;
            _hash = written;
            _dirty = false;
        }

        /// <summary>開いた（読み直した・保存した）あとに、外でファイルが書き換わったか。消された・移されたときも true。時刻と大きさだけで見る（速い）。</summary>
        public bool IsChangedOnDisk
        {
            get
            {
                DateTime stamp;
                long length;
                Probe(_filePath, out stamp, out length);
                return stamp != _stamp || length != _length;
            }
        }

        /// <summary>
        /// IsChangedOnDisk に加えて、時刻と大きさが同じなら中身を読んで SHA-256 を比べる（2026-10-27。保存の直前に使う）。
        /// 読めなければ投げる（保存できなかった扱い。画面の内容は残る）。
        /// </summary>
        public bool IsContentChangedOnDisk()
        {
            if (IsChangedOnDisk) return true;
            if (_hash == null) return true;

            DateTime stamp;
            long length;
            var bytes = ReadStable(_filePath, out stamp, out length);
            return !SameHash(HashOf(bytes), _hash);
        }

        /// <summary>今の画面の内容（全部のページを書き出したもの）の文字数。外部の変更を上書きするか尋ねるときに出す（2026-10-27）。</summary>
        public int TextLength
        {
            get { return Serialize().Length; }
        }

        /// <summary>ディスクにあるファイルの文字数。読めなければ -1（2026-10-27）。</summary>
        public int DiskTextLength()
        {
            try
            {
                var bytes = File.ReadAllBytes(_filePath);
                var offset = HasUtf8Bom(bytes) ? 3 : 0;
                return new UTF8Encoding(false, false).GetCharCount(bytes, offset, bytes.Length - offset);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>ファイルの時刻と大きさ。無ければ 1601 年と -1（開いたときの値と必ず違う）。</summary>
        private static void Probe(string path, out DateTime stamp, out long length)
        {
            var info = new FileInfo(path);
            stamp = info.LastWriteTimeUtc;
            length = info.Exists ? info.Length : -1;
        }

        /// <summary>
        /// ファイルを読み直して、中身をそっくり入れ替える（2026-10-24。AI が外で書き換えたとき）。
        /// 同じ文書を指しているページ（MarkdownView）はそのまま使えるよう、新しい文書は作らない。
        /// 読めなければ投げて、今の中身は変えない（書いている途中を読んだときなど。次のきっかけで読み直す）。
        /// 未保存の編集があるときに呼ぶと、それは消える。呼び出し側で確かめる。
        /// </summary>
        public void Reload()
        {
            // 書き終わって落ち着くまで待ってから読む（2026-10-27。WaitUntilQuiet）。
            WaitUntilQuiet(_filePath);
            var fresh = Load(_filePath);

            // 中身が空になった・半分より小さくなったときは、書いている途中（空にしてから書き直す途中）を読んだのかもしれない。
            // 少し待って、もう一度だけ読み直し、そちらを使う（2026-10-27）。前の中身が小さいとき（200 バイト未満）は見ない。
            if (_length >= 200 && fresh._length < _length / 2)
            {
                System.Threading.Thread.Sleep(1000);
                WaitUntilQuiet(_filePath);
                fresh = Load(_filePath);
            }

            _pages.Clear();
            _pages.AddRange(fresh._pages);
            _hadBom = fresh._hadBom;
            _newLine = fresh._newLine;
            _stamp = fresh._stamp;
            _length = fresh._length;
            _hash = fresh._hash;
            _dirty = false;

            // 消したページの Id は覚えたまま。読み直した中に無ければ、使い回さないことに変わりはない。
        }

        /// <summary>
        /// 書き出したときに、このページの1行目がファイルの何行目の手前に来るか（ページの n 行目はファイルの 戻り値+n 行目）。
        /// ルートは 0、それ以外は印の行の行番号。ページが無ければ -1。
        /// </summary>
        public int LinesBefore(string id)
        {
            if (string.IsNullOrEmpty(id)) return Contains(id) ? 0 : -1;

            var marker = "<!--page: " + id + "-->";
            var lines = Serialize().Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] == marker) return i + 1;
            }

            return -1;
        }

        // =====================================================================
        // ページの出し入れ
        // =====================================================================

        public bool Contains(string id)
        {
            return Find(id) != null;
        }

        public string TextOf(string id)
        {
            var page = Find(id);
            return page == null ? null : page.Text;
        }

        /// <summary>ページが無ければ false。呼び出し側は、その編集が行き場を失ったことを知らせる。</summary>
        public bool SetText(string id, string text)
        {
            var page = Find(id);
            if (page == null) return false;
            if (page.Text == text) return true;

            page.Text = text;
            _dirty = true;
            return true;
        }

        /// <summary>末尾に新しいページを足す。並び順に意味は無い。</summary>
        public string AddPage(string text)
        {
            var id = CreateId();

            _pages.Add(new PageRegion(id, text ?? string.Empty));
            _dirty = true;

            return id;
        }

        public void RemovePage(string id)
        {
            if (string.IsNullOrEmpty(id)) return;   // ルートは消せない

            var page = Find(id);
            if (page == null) return;

            _pages.Remove(page);
            _retired.Add(id);
            _dirty = true;
        }

        /// <summary>RemovePage で消したページを元の位置に戻す。元に戻す（Ctrl+Z）から使う。</summary>
        public void RestorePage(PageRegion page, int index)
        {
            if (page == null || Contains(page.Id)) return;

            _pages.Insert(Math.Max(0, Math.Min(index, _pages.Count)), page);
            _dirty = true;
        }

        /// <summary>ページの並びの中での位置。無ければ -1。</summary>
        public int IndexOf(string id)
        {
            var page = Find(id);
            return page == null ? -1 : _pages.IndexOf(page);
        }

        /// <summary>この Id のページそのもの。元に戻すために取っておく。</summary>
        public PageRegion PageOf(string id)
        {
            return Find(id);
        }

        /// <summary>
        /// この id を指すリンクを探す。見つかれば、そのリンクがあるページと位置を返す。
        /// 「戻す」は子のページで実行するので、親を逆に辿る必要がある。
        /// </summary>
        /// <param name="preferred">
        /// 先に探すページ。スタックで左隣にある、実際にたどってきた親。
        /// </param>
        public bool FindLinkTo(string id, string preferred, out string pageId, out int offset, out int length)
        {
            pageId = null;
            offset = -1;
            length = 0;

            if (string.IsNullOrEmpty(id)) return false;

            var pattern = LinkPattern(id);

            // 自分自身と、自分の中から辿れるページ（子・孫）は親にしない。
            // 自分の中の「↑」のようなリンクを親と取り違えると、本文を自分の中に戻してから自分を消し、
            // 中身が丸ごと失われる。子の「戻る」リンクを取ると、本文が孫に入って孤立する。
            var excluded = Descendants(id);
            excluded.Add(id);

            // 探す順: スタックの左隣 → ルートからの連鎖の上の親 → ほかのページを並び順に。
            var order = new List<string>();
            if (preferred != null) order.Add(preferred);

            var chain = PathTo(id);
            if (chain != null && chain.Count >= 2) order.Add(chain[chain.Count - 2]);

            foreach (var page in _pages) order.Add(page.Id);

            foreach (var candidate in order)
            {
                if (excluded.Contains(candidate)) continue;

                var page = Find(candidate);
                if (page == null) continue;

                var match = pattern.Match(page.Text);
                if (!match.Success) continue;

                pageId = page.Id;
                offset = match.Index;
                length = match.Length;

                return true;
            }

            return false;
        }

        /// <summary>
        /// この id を指すリンクの文字（[ ] の中）。見出しの履歴で、どのサブページの見出しかを示すのに使う（2026-10-30）。
        /// リンクが見つからなければ null。
        /// </summary>
        public string LinkTextTo(string id)
        {
            string pageId;
            int offset, length;
            if (!FindLinkTo(id, null, out pageId, out offset, out length)) return null;

            var link = Find(pageId).Text.Substring(offset, length);
            var close = link.IndexOf("](", StringComparison.Ordinal);
            if (close < 1) return null;

            var text = Regex.Replace(link.Substring(1, close - 1), @"\\(.)", "$1").Trim();
            return text.Length > 0 ? text : null;
        }

        /// <summary>このページの中から、リンクを辿って行けるページ全部（自分は含まない）。</summary>
        private HashSet<string> Descendants(string id)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<PageRegion>();

            var start = Find(id);
            if (start != null) pending.Enqueue(start);

            while (pending.Count > 0)
            {
                var from = pending.Dequeue();

                foreach (var page in _pages)
                {
                    // ルートは誰の子にもならないので辿らない。ルートの Id は空で、ReferencePattern("") は
                    // (#概要) のような日本語の見出しへのリンクにも当たってしまい、ルートと本当の親まで候補から外してしまう。
                    if (page.Id == id || page.Id == RootId || found.Contains(page.Id)) continue;
                    if (!ReferencePattern(page.Id).IsMatch(from.Text)) continue;

                    found.Add(page.Id);
                    pending.Enqueue(page);
                }
            }

            return found;
        }

        /// <summary>
        /// ルートページからリンクを辿っても行き着けないサブページ。
        /// 切り出したリンクを消すと生まれる。
        ///
        /// 行き着けないページ同士がリンクし合っていても、両方とも出す。
        /// どちらもファイルを開いたところからは開けないため。
        /// </summary>
        public List<PageRegion> FindUnreferenced()
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<PageRegion>();

            var root = Find(RootId);
            if (root != null)
            {
                reached.Add(root.Id);
                pending.Enqueue(root);
            }

            while (pending.Count > 0)
            {
                var from = pending.Dequeue();

                foreach (var page in _pages)
                {
                    if (reached.Contains(page.Id)) continue;
                    if (!ReferencePattern(page.Id).IsMatch(from.Text)) continue;

                    reached.Add(page.Id);
                    pending.Enqueue(page);
                }
            }

            var found = new List<PageRegion>();
            foreach (var page in _pages)
            {
                if (!reached.Contains(page.Id)) found.Add(page);
            }

            return found;
        }

        /// <summary>
        /// ルートからこのページまでのリンクの連鎖（ルートと自身を含む）。辿れなければ null。
        /// スタックは「隣り合うページはリンクでつながった親子」なので、
        /// サブページを直接開くには、この連鎖どおりに積む必要がある。
        /// </summary>
        public List<string> PathTo(string id)
        {
            var target = id ?? RootId;

            var root = Find(RootId);
            if (root == null || Find(target) == null) return null;

            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            parents[RootId] = null;

            var pending = new Queue<PageRegion>();
            pending.Enqueue(root);

            while (pending.Count > 0 && !parents.ContainsKey(target))
            {
                var from = pending.Dequeue();

                foreach (var page in _pages)
                {
                    if (parents.ContainsKey(page.Id)) continue;
                    if (!ReferencePattern(page.Id).IsMatch(from.Text)) continue;

                    parents[page.Id] = from.Id;
                    pending.Enqueue(page);
                }
            }

            if (!parents.ContainsKey(target)) return null;

            var path = new List<string>();
            for (var at = target; at != null; at = parents[at]) path.Insert(0, at);

            return path;
        }

        /// <summary>
        /// [名](#id) の形のリンク。「戻す」で置き換える範囲を決める。
        /// 名前の中の \] のような逃がした文字も受ける。切り出しは名前の [ ] を逃がして書くため。
        /// </summary>
        private static Regex LinkPattern(string id)
        {
            var target = Regex.Escape(id);

            return new Regex(@"\[(?:\\.|[^\]\\\r\n])*\]\([ \t]*(?:<#" + target + @">|#" + target + @")"
                + @"(?:[ \t]+(?:""[^""\r\n]*""|'[^'\r\n]*'))?[ \t]*\)");
        }

        /// <summary>
        /// このページを指していそうなもの全部。辿れるかどうかの判定に使う。
        /// 判定を外すと、リンクされているページを孤立と見なして削除を勧めてしまう。
        /// そのため緩めに取り、題名付き・&lt;&gt; 囲み・参照式のリンクも数える。
        /// </summary>
        private static Regex ReferencePattern(string id)
        {
            return new Regex(@"(?:\]\([ \t]*<?|^[ \t]{0,3}\[[^\]\r\n]+\]:[ \t]*<?)#"
                + Regex.Escape(id) + @"(?![A-Za-z0-9_-])", RegexOptions.Multiline);
        }

        /// <summary>開いていないページの一部を差し替える。差し替えられなければ false。</summary>
        public bool ReplaceIn(string pageId, int offset, int length, string text)
        {
            var page = Find(pageId);
            if (page == null) return false;
            if (offset < 0 || length < 0 || offset + length > page.Text.Length) return false;

            page.Text = page.Text.Substring(0, offset) + text + page.Text.Substring(offset + length);
            _dirty = true;
            return true;
        }

        private PageRegion Find(string id)
        {
            var key = id ?? RootId;

            foreach (var page in _pages)
            {
                if (string.Equals(page.Id, key, StringComparison.Ordinal)) return page;
            }

            return null;
        }

        /// <summary>使われていない一番小さい番号。読めるほうが追いやすい。</summary>
        private string CreateId()
        {
            for (var n = 1; ; n++)
            {
                var id = "p" + n;
                if (!Contains(id) && !_retired.Contains(id)) return id;
            }
        }

        // =====================================================================
        // ファイルの形（SPEC 5章）
        // =====================================================================

        private void Parse(string text)
        {
            _pages.Clear();
            _pages.AddRange(ParsePages(text));
        }

        /// <summary>
        /// コードフェンスの中の印の行は本文として扱う。
        /// この形を説明する文書（SPEC や README）を開いたときに、コード例のところで割れないように。
        ///
        /// そのフェンスが後ろで閉じないなら、印か本文か決められないので開かない（例外）。
        /// 以前の版は閉じていないフェンスのあとにもそのまま印を書いていた。手でフェンスを閉じてから開く。
        /// 今の版は印の前で必ずフェンスを閉じて書く（Serialize）ので、自分で書いたものはここに当たらない。
        /// </summary>
        private static List<PageRegion> ParsePages(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            var ids = new List<string>();
            var bodies = new List<List<string>>();
            var body = new List<string>();
            var fence = new FenceState();

            ids.Add(RootId);
            bodies.Add(body);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var marker = MarkerRe.Match(line);
                if (marker.Success && fence.IsOpen)
                {
                    if (!fence.ClosesIn(lines, i + 1))
                        throw new IOException((i + 1) + " 行目のページの区切りが、閉じていないコードブロックのあとにあります。" +
                            "コードブロックを閉じてから開いてください。");

                    marker = Match.Empty;
                }

                if (!marker.Success)
                {
                    fence.Feed(line);
                    body.Add(line);
                    continue;
                }

                body = new List<string>();
                ids.Add(marker.Groups[1].Value);
                bodies.Add(body);
                fence = new FenceState();
            }

            // 同じ Id が2つあると、リンクからは前のものしか開けず、書き出しからも黙って抜ける。
            // 後ろのものに空いている Id を付け直し、孤立ページとして見えるようにする。
            var taken = new HashSet<string>(ids, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pages = new List<PageRegion>();

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];

                if (!seen.Add(id))
                {
                    for (var n = 1; ; n++)
                    {
                        id = "p" + n;
                        if (taken.Add(id)) break;
                    }

                    seen.Add(id);
                }

                pages.Add(new PageRegion(id, string.Join("\n", bodies[i].ToArray())));
            }

            return pages;
        }

        private string Serialize()
        {
            var builder = new StringBuilder();

            for (var i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                var last = i == _pages.Count - 1;

                if (i > 0)
                {
                    builder.Append("<!--page: ").Append(page.Id).Append("-->").Append(_newLine);
                }

                // 単独の CR も改行にそろえる。読み込みと同じ扱いにしないと、読み直したときに行の区切りがずれる。
                var lines = page.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                var fence = new FenceState();
                var markerInFence = false;

                for (var j = 0; j < lines.Length; j++)
                {
                    var line = lines[j];

                    // 本文に印と同じ形の行があると、開き直したときにそこでページが割れる。
                    // 頭に \ を付けて、ただの文字として書く。
                    if (MarkerRe.IsMatch(line))
                    {
                        if (fence.IsOpen) markerInFence = true;
                        else line = "\\" + line.TrimStart(' ');
                    }

                    fence.Feed(line);
                    if (!fence.IsOpen) markerInFence = false;

                    builder.Append(line);
                    if (j < lines.Length - 1) builder.Append(_newLine);
                }

                // 閉じていないフェンスは閉じて書く。閉じないと、中にある印の形の行や
                // 次の印を、読み込みのときにどちらとも決められない。
                // 閉じていないフェンスはページの終わりで閉じるものとして表示されるので、見た目は変わらない。
                // 最後のページは後ろに印が無いので、中に印の形の行があるときだけでよい。
                if (fence.IsOpen && (!last || markerInFence)) builder.Append(_newLine).Append(fence.Closer);

                if (!last) builder.Append(_newLine);
            }

            return builder.ToString();
        }

        private static bool SamePageIds(List<PageRegion> a, List<PageRegion> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        /// <summary>行を順に読んで、コードフェンスの中かどうかを追う（CommonMark の規則の範囲で）。</summary>
        private sealed class FenceState
        {
            private char _mark;
            private int _length;

            public bool IsOpen
            {
                get { return _length > 0; }
            }

            /// <summary>今のフェンスを閉じる行。</summary>
            public string Closer
            {
                get { return new string(_mark, _length); }
            }

            /// <summary>lines[from] 以降に、今のフェンスを閉じる行があるか。</summary>
            public bool ClosesIn(string[] lines, int from)
            {
                for (var i = from; i < lines.Length; i++)
                {
                    if (Closes(lines[i])) return true;
                }

                return false;
            }

            private bool Closes(string line)
            {
                var match = FenceRe.Match(line);
                if (!match.Success) return false;

                var run = match.Groups[1].Value;
                return run[0] == _mark && run.Length >= _length && match.Groups[2].Value.Trim().Length == 0;
            }

            public void Feed(string line)
            {
                var match = FenceRe.Match(line);
                if (!match.Success) return;

                var run = match.Groups[1].Value;
                var rest = match.Groups[2].Value;

                if (IsOpen)
                {
                    if (Closes(line)) _length = 0;
                    return;
                }

                // ` のフェンスは情報文字列に ` を含められない。
                if (run[0] == '`' && rest.IndexOf('`') >= 0) return;

                _mark = run[0];
                _length = run.Length;
            }
        }

        // =====================================================================
        // 小物
        // =====================================================================

        /// <summary>
        /// 保存できないときの逃げ道。同じフォルダに別名で書き出し、そのパスを返す。
        /// 外部の変更で保存を止めたときも、こちらの編集はここに残る。書けなければ null。
        /// </summary>
        public string SaveRecoveryCopy()
        {
            return WriteRecovery(_filePath, ".recovered-", Encode(Serialize(), _hadBom));
        }

        /// <summary>
        /// 行き先の無くなったページの画面の内容を、別名で書き出す。書き出した場所を返す。書けなければ null。
        /// 元のファイルの隣に、だめならドキュメントの下に置く。
        /// </summary>
        public static string SaveLostText(string filePath, string text)
        {
            return WriteRecovery(filePath, ".lost-", Encode(text ?? string.Empty, false));
        }

        private static string WriteRecovery(string filePath, string tag, byte[] data)
        {
            var name = string.IsNullOrEmpty(filePath) ? "無題" : Path.GetFileNameWithoutExtension(filePath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var folders = new List<string>();
            if (!string.IsNullOrEmpty(filePath)) folders.Add(Path.GetDirectoryName(filePath));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Tsuzuri Recovered"));

            foreach (var folder in folders)
            {
                try
                {
                    Directory.CreateDirectory(folder);

                    var path = Path.Combine(folder, name + tag + stamp + ".md");
                    for (var n = 2; File.Exists(path); n++)
                        path = Path.Combine(folder, name + tag + stamp + "-" + n + ".md");

                    WriteAtomically(path, data);
                    return path;
                }
                catch (Exception)
                {
                    // 次の候補へ。
                }
            }

            return null;
        }

        private static byte[] Encode(string text, bool bom)
        {
            var encoding = new UTF8Encoding(bom);
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(text);

            var data = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, data, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, data, preamble.Length, body.Length);

            return data;
        }

        /// <summary>
        /// 一時ファイルに書いてから置き換える。
        /// 直接上書きすると、書き込み中に中断されたときに元の内容も失われる。
        /// 置き換える前にディスクまで書き切る。書き切る前に置き換えると、
        /// 電源が落ちたときに中身の無いファイルが残ることがある。
        /// 一時ファイルの名前が決まっていてよいのは、アプリが1つしか動かないから（App）。
        /// </summary>
        /// <param name="beforeReplace">一時ファイルを書き終えて、元のファイルを置き換える直前に呼ぶ。投げたら一時ファイルを消して置き換えない。</param>
        internal static void WriteAtomically(string path, byte[] data, Action beforeReplace = null)
        {
            var temp = path + ".saving";

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }

            if (beforeReplace != null)
            {
                try
                {
                    beforeReplace();
                }
                catch (Exception)
                {
                    TryDelete(temp);
                    throw;
                }
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception)
            {
                // \\Mac\ 共有は置き換えに対応していないので、上書きで代える（SPEC 10章）。この環境ではこれが普通の道。
                File.Copy(temp, path, true);
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 残っても本物には触れない一時ファイル。次の保存で上書きされる。
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Tsuzuri.Office
{
    /// <summary>
    /// メールの添付の ZIP を、AI が読むために展開する（2026-11-27。利用者の判断。Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする）。
    /// 外から届いたファイルなので、次を守る:
    /// ・展開先のフォルダの外へ書く名前（..\ や C:\ で始まるもの）は書かない
    /// ・中身の数・大きさに上限（実際に書いた字数で数える。ZIP に書いてある大きさは偽れるため）
    /// ・動かせる形のファイル（OutlookBridge.IsRunnable）は書かない
    /// ・パスワード付き（暗号化）の ZIP は展開しない（.NET Framework では解けない。利用者が自分で展開する）。暗号化の印を確かめられない形（ZIP64 など）も展開しない
    /// ・ZIP の中の ZIP は展開しない（そのまま書く）
    /// ・書いたファイルには「インターネットから来た」印（Zone.Identifier）を付ける
    /// ・時間に上限（台本が待つうちに答えを返す）。1つ読めなくても、残りは続ける
    /// 名前は、全部が UTF-8 として読めれば UTF-8（Mac の ZIP は印が無くても UTF-8）、そうでなければ日本の Windows で作られたものとして Shift_JIS で読む。
    /// </summary>
    public static class ZipUnpacker
    {
        private const int MaxEntries = 1000;
        private const long MaxTotal = 500L * 1024 * 1024;
        private const long MaxEntry = 200L * 1024 * 1024;

        public sealed class Result
        {
            /// <summary>書いたファイル（展開先からの相対パス）と大きさ。</summary>
            public List<KeyValuePair<string, long>> Written = new List<KeyValuePair<string, long>>();
            /// <summary>書かなかったものと、そのわけ。</summary>
            public List<string> Skipped = new List<string>();
            /// <summary>展開できなかったわけ（途中で止めたときも。全部書けたら null）。</summary>
            public string Problem;
        }

        /// <summary>zipPath を folder に展開する（folder は空の新しいフォルダ）。deadline を過ぎたら、そこで止める。</summary>
        public static Result Unpack(string zipPath, string folder, DateTime deadline)
        {
            var result = new Result();
            bool utf8;
            var look = Inspect(zipPath, out utf8);
            if (look == Look.Encrypted)
            {
                result.Problem = "パスワード付き（暗号化）の ZIP なので展開しなかった。ZIP のまま保存してある（展開は利用者が自分でする）";
                return result;
            }
            if (look == Look.Unknown)
            {
                result.Problem = "パスワード付きかどうかを確かめられない形の ZIP（ZIP64 など）なので展開しなかった。ZIP のまま保存してある";
                return result;
            }
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long total = 0;
            try
            {
                Directory.CreateDirectory(root);
                using (var stream = File.OpenRead(zipPath))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, utf8 ? new UTF8Encoding(false) : Encoding.GetEncoding(932)))
                {
                    var count = 0;
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName.Replace('/', '\\');
                        if (name.EndsWith("\\", StringComparison.Ordinal)) continue;   // フォルダの印
                        if (++count > MaxEntries)
                        {
                            result.Problem = "中のファイルが多すぎるので、" + MaxEntries + " 個で止めた";
                            break;
                        }
                        if (DateTime.UtcNow > deadline)
                        {
                            result.Problem = "時間がかかるので、途中で止めた（" + result.Written.Count + " 個まで展開した）";
                            break;
                        }
                        var shown = Shown(name);
                        var target = Target(root, name);
                        if (target == null)
                        {
                            result.Skipped.Add(shown + "（展開先の外を指す名前か、使えない名前）");
                            continue;
                        }
                        if (OutlookBridge.IsRunnable(name) || OutlookBridge.IsRunnable(Path.GetFileName(target)))
                        {
                            result.Skipped.Add(shown + "（動かせる形のファイル）");
                            continue;
                        }
                        if (File.Exists(target))
                        {
                            result.Skipped.Add(shown + "（同じ名前がもうある）");
                            continue;
                        }
                        long written = 0;
                        var tooBig = false;
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            using (var input = entry.Open())
                            using (var output = File.Create(target))
                            {
                                var buffer = new byte[81920];
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    written += read;
                                    if (DateTime.UtcNow > deadline) throw new TimeoutException();
                                    if (written > MaxEntry || total + written > MaxTotal)
                                    {
                                        tooBig = true;
                                        break;
                                    }
                                    output.Write(buffer, 0, read);
                                }
                            }
                        }
                        catch (TimeoutException)
                        {
                            TryDelete(target);
                            result.Problem = "時間がかかるので、途中で止めた（" + result.Written.Count + " 個まで展開した）";
                            break;
                        }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
                        {
                            // 1つ読めなくても（対応していない圧縮の形・壊れている・書けない名前）、書きかけを消して次へ
                            TryDelete(target);
                            result.Skipped.Add(shown + "（読めなかった: " + Shown(ex.Message) + "）");
                            continue;
                        }
                        if (tooBig)
                        {
                            TryDelete(target);
                            result.Skipped.Add(shown + "（大きすぎる）");
                            if (total + written > MaxTotal)
                            {
                                result.Problem = "中身が合わせて " + OutlookBridge.SizeText(MaxTotal) + " を超えるので、そこで止めた";
                                break;
                            }
                            continue;
                        }
                        total += written;
                        OutlookBridge.MarkFromInternet(target);
                        result.Written.Add(new KeyValuePair<string, long>(target.Substring(root.Length), written));
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                result.Problem = "ZIP を読めなかった（壊れているか、パスワード付き・対応していない形）: " + ex.Message;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                result.Problem = "展開できなかった: " + ex.Message;
            }
            return result;
        }

        /// <summary>結果のファイルに書く名前（改行・制御文字を空白にして、アプリの「#」の行を偽れないように。長ければ切る）。</summary>
        private static string Shown(string text)
        {
            var chars = (text ?? string.Empty).Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' || c == '\u0085' ? ' ' : c).ToArray();
            var shown = new string(chars).Trim();
            return shown.Length > 200 ? shown.Substring(0, 200) + "…" : shown;
        }

        /// <summary>展開先の中のパス。外を指す・使えない名前なら null。</summary>
        private static string Target(string root, string name)
        {
            if (name.Length == 0 || name.StartsWith("\\", StringComparison.Ordinal) || name.Contains(":")) return null;
            try
            {
                var parts = name.Split('\\');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "." || parts[i] == "..") return null;
                    parts[i] = OutlookBridge.SafeFileName(parts[i], "_");
                }
                var full = Path.GetFullPath(Path.Combine(root, string.Join("\\", parts)));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length < 240 ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private enum Look
        {
            Plain,
            Encrypted,
            Unknown,
        }

        /// <summary>
        /// 中央ディレクトリを見て、暗号化された中身があるか（各項目の汎用フラグの 0 ビット目）を返す。確かめられない形（ZIP64・壊れている）なら Unknown（展開しない側）。
        /// utf8 に、全部の名前が UTF-8 として読めるか（UTF-8 の印が付いているか、印が無くても全部が正しい UTF-8 なら true）。
        /// </summary>
        private static Look Inspect(string zipPath, out bool utf8)
        {
            utf8 = true;
            var strict = new UTF8Encoding(false, true);
            try
            {
                using (var stream = File.OpenRead(zipPath))
                {
                    var length = stream.Length;
                    var tail = (int)Math.Min(length, 65557);
                    if (tail < 22) return Look.Unknown;
                    var buffer = new byte[tail];
                    stream.Seek(length - tail, SeekOrigin.Begin);
                    ReadFull(stream, buffer, tail);
                    var end = -1;
                    for (var i = tail - 22; i >= 0; i--)
                        if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06) { end = i; break; }
                    if (end < 0) return Look.Unknown;
                    var entries = BitConverter.ToUInt16(buffer, end + 10);
                    var size = BitConverter.ToUInt32(buffer, end + 12);
                    var offset = BitConverter.ToUInt32(buffer, end + 16);
                    if (entries == 0xFFFF || offset == 0xFFFFFFFF || size == 0xFFFFFFFF || offset + (long)size > length || size > 64 * 1024 * 1024) return Look.Unknown;
                    var directory = new byte[size];
                    stream.Seek(offset, SeekOrigin.Begin);
                    ReadFull(stream, directory, (int)size);
                    var at = 0;
                    for (var n = 0; n < entries; n++)
                    {
                        if (at + 46 > directory.Length || BitConverter.ToUInt32(directory, at) != 0x02014B50) return Look.Unknown;
                        var flags = BitConverter.ToUInt16(directory, at + 8);
                        if ((flags & 1) != 0) return Look.Encrypted;
                        int nameLength = BitConverter.ToUInt16(directory, at + 28);
                        if (at + 46 + nameLength > directory.Length) return Look.Unknown;
                        if ((flags & 0x800) == 0 && utf8)
                        {
                            try
                            {
                                strict.GetString(directory, at + 46, nameLength);
                            }
                            catch (DecoderFallbackException)
                            {
                                utf8 = false;
                            }
                        }
                        at += 46 + nameLength + BitConverter.ToUInt16(directory, at + 30) + BitConverter.ToUInt16(directory, at + 32);
                    }
                    return Look.Plain;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return Look.Unknown;
            }
        }

        private static void ReadFull(Stream stream, byte[] buffer, int count)
        {
            var done = 0;
            while (done < count)
            {
                var read = stream.Read(buffer, done, count - done);
                if (read <= 0) throw new IOException("ZIP の終わりが欠けている");
                done += read;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}

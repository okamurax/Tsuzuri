using System;
using System.IO;

namespace AiLink
{
    /// <summary>git.exe の場所（ファイラーの git の印で使う。Tsuzuri の DocumentHistory.GitPath と同じ探し方）。</summary>
    public static class GitLocator
    {
        private static readonly object SearchLock = new object();
        private static bool _searched;
        private static string _git;

        /// <summary>exe の隣の git（MinGit を展開したもの）の、git.exe のあるフォルダ。</summary>
        public static string BundledGitFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "git", "cmd"); }
        }

        /// <summary>git.exe の場所。exe の隣の git、PATH の通った場所、Git for Windows の決まった入れ場所の順に探す。無ければ null。</summary>
        public static string GitPath
        {
            get
            {
                lock (SearchLock)   // ファイラーの裏のスレッドからも呼ぶ（探し終わる前に「無い」と答えないように）
                {
                    if (_searched) return _git;
                    _searched = true;
                    var candidates = new System.Collections.Generic.List<string> { BundledGitFolder };
                    foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
                    {
                        if (folder.Trim().Length > 0) candidates.Add(folder.Trim().Trim('"'));
                    }
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"));
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd"));
                    foreach (var folder in candidates)
                    {
                        try
                        {
                            var path = Path.Combine(folder, "git.exe");
                            if (File.Exists(path))
                            {
                                _git = path;
                                return _git;
                            }
                        }
                        catch (Exception)
                        {
                            // PATH に変な項目があっても飛ばす。
                        }
                    }
                    return null;
                }
            }
        }
    }
}

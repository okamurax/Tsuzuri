using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: 左の列のランチャー（上）とファイラー（Tsuzuri の MainWindow.Overlays.cs・Stack.cs・Links.cs から写したもの）。
    // Tsuzuri と違い、いつも出しておく（重ねて出し入れしない。利用者の判断）。
    public partial class MainWindow
    {
        private LauncherView _launcher;
        private FilerView _filer;

        private void CreateLauncher()
        {
            _launcher = new LauncherView(_settings, OpenFromLauncher, Status, folder =>
            {
                _filer.Navigate(folder);
                Status("ファイラーで開きました: " + folder);
            });
            LauncherSlot.Child = _launcher;
        }

        private void CreateFiler()
        {
            _filer = new FilerView(Status, RunScript, path => false, OnFilerRenamed);
            var folder = _settings.FilerFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _filer.Navigate(folder);
            _filer.FolderChanged += moved =>
            {
                if (string.Equals(moved, _settings.FilerFolder, StringComparison.OrdinalIgnoreCase)) return;
                _settings.FilerFolder = moved;
                _settings.Save();
            };
            FilerHost.Child = _filer;
        }

        /// <summary>ランチャーのマスを押した。フォルダはファイラーで開き、スクリプトは別の窓で動かし、ほかは Windows に任せて開く。</summary>
        private void OpenFromLauncher(string path)
        {
            if (Directory.Exists(path))
            {
                _filer.Navigate(path);
                Status("ファイラーで開きました: " + path);
                return;
            }
            if (FilerView.IsScript(path)) RunScript(path);
            else Launch(path);
        }

        /// <summary>スクリプトを別の窓で動かす。.ps1 は PowerShell で（既定のアプリに任せるとメモ帳が開くため）、ほかは Windows に任せる。</summary>
        private void RunScript(string script)
        {
            if (!string.Equals(Path.GetExtension(script), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                Launch(script);
                return;
            }
            try
            {
                var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(powershell, "-NoLogo -ExecutionPolicy Bypass -File \"" + script + "\"") { UseShellExecute = true };
                var folder = Path.GetDirectoryName(script);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) start.WorkingDirectory = folder;
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Status("動かせませんでした: " + ex.Message);
            }
        }

        /// <summary>Windows に任せて開く。作業フォルダはそのファイルの場所（バッチが隣のファイルを相対パスで探せるように）。</summary>
        private void Launch(string target)
        {
            try
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = true };
                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) start.WorkingDirectory = folder;
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Status("開けませんでした: " + ex.Message);
            }
        }

        /// <summary>ファイラーで名前を変えたあと。覚えているパス（ランチャーの登録・ファイラーのフォルダ）を新しい名前に直す。</summary>
        private string OnFilerRenamed(string oldPath, string newPath, bool isFolder)
        {
            var oldParent = Path.GetDirectoryName(oldPath.TrimEnd('\\', '/'));
            var from = string.IsNullOrEmpty(oldParent) ? oldPath
                : Path.Combine(ShellActions.CanonicalPath(oldParent), Path.GetFileName(oldPath.TrimEnd('\\', '/')).Normalize(System.Text.NormalizationForm.FormC));
            var to = ShellActions.CanonicalPath(newPath);
            Func<string, string> remap = path =>
            {
                if (string.IsNullOrEmpty(path)) return path;
                if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase)) return to;
                if (isFolder && path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) return to + path.Substring(from.Length);
                return path;
            };

            _settings.FilerFolder = remap(_settings.FilerFolder);
            var tiles = false;
            foreach (var tile in _settings.Tiles)
            {
                var moved = remap(tile.Path);
                if (moved == tile.Path) continue;
                tile.Path = moved;
                tiles = true;
            }
            OnAiPathRenamed(remap);
            _settings.Save();
            if (tiles && _launcher != null) _launcher.Refresh();
            return null;
        }
    }
}

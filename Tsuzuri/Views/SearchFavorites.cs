using System;
using System.Collections.Generic;
using System.Linq;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 検索語のお気に入り（2026-11-04。SPEC 12章）。どのページ・どの画面の検索欄からも同じ一覧を使う。
    /// app.json の searchFavorites に覚える（読み書きは App が Changed で受ける）。
    /// </summary>
    public static class SearchFavorites
    {
        private static readonly List<string> List = new List<string>();

        public static event EventHandler Changed;

        public static IReadOnlyList<string> Items
        {
            get { return List; }
        }

        public static void Load(IEnumerable<string> items)
        {
            List.Clear();
            if (items != null) List.AddRange(items.Where(item => !string.IsNullOrEmpty(item)).Distinct());
        }

        public static bool Contains(string text)
        {
            return List.Contains(text);
        }

        /// <summary>無ければ末尾に足し、あれば外す。</summary>
        public static void Toggle(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!List.Remove(text)) List.Add(text);
            Raise();
        }

        public static void Remove(string text)
        {
            if (List.Remove(text)) Raise();
        }

        private static void Raise()
        {
            var handler = Changed;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }
}

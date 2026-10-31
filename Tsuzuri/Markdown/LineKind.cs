namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 1行が何であるか。装飾を決めるためだけに使う。
    /// </summary>
    public enum LineKind
    {
        Blank,      // 空行
        Paragraph,  // 通常の段落
        Heading,    // # ～ ######
        Bullet,     // - * +
        Ordered,    // 1. 1)
        Task,       // - [ ] / - [x]
        Quote,      // >
        Rule,       // --- *** ___
        Fence,      // ``` ~~~ の行そのもの
        Code,       // コードブロックの内側
        Table,      // | a | b | の表
        Setext      // 下線式見出しの下線（=== や ---）
    }
}

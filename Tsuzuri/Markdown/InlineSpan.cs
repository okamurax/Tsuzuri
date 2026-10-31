using System.Collections.Generic;

namespace Tsuzuri.Markdown
{
    public enum SpanKind
    {
        Marker,     // 記号そのもの。淡色にするか、画面から消す
        Strong,     // **太字**
        Emphasis,   // *斜体*
        Strike,     // ~~打消し~~
        Code,       // `コード`
        LinkText,   // [ここ](先)
        LinkDest    // [名](ここ)
    }

    /// <summary>行内の装飾範囲。オフセットは行頭からの文字数。</summary>
    public sealed class InlineSpan
    {
        public readonly int Start;
        public readonly int Length;
        public readonly SpanKind Kind;

        public InlineSpan(int start, int length, SpanKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }
    }

    /// <summary>クリック判定用のリンク範囲。[名](先) 全体を覆う。</summary>
    public sealed class LinkSpan
    {
        public readonly int Start;
        public readonly int Length;
        public readonly string Label;
        public readonly string Target;
        public readonly bool IsImage;

        public LinkSpan(int start, int length, string label, string target, bool isImage)
        {
            Start = start;
            Length = length;
            Label = label;
            Target = target;
            IsImage = isImage;
        }
    }

    /// <summary>1行ぶんの装飾範囲とリンク。</summary>
    public sealed class InlineScan
    {
        public readonly List<InlineSpan> Spans = new List<InlineSpan>();
        public readonly List<LinkSpan> Links = new List<LinkSpan>();
    }
}

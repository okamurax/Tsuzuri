using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 行ごとに Markdown の装飾を載せる（SPEC 8章）。
    ///
    /// これは純粋な表示層で、文書そのものには一切触れない。
    /// ここにバグがあっても見た目が崩れるだけで、ファイルは壊れない。
    /// </summary>
    public sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownView _owner;

        public MarkdownColorizer(MarkdownView owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;

            var info = _owner.GetLineInfo(line.LineNumber);
            if (info == null) return;

            var start = line.Offset;
            var end = line.EndOffset;

            ApplyLineStyle(info, start, end);

            // フェンスの内側は一切解釈しない。
            if (info.Kind == LineKind.Code) return;

            // 解析結果は1手古い可能性がある。行をはみ出すと例外になるので必ず丸める。
            if (info.Marker.Length > 0)
                ChangeLinePart(start, Math.Min(end, start + info.Marker.Length), Faint);

            var scan = _owner.GetInline(line.LineNumber);
            if (scan == null) return;

            foreach (var span in scan.Spans)
            {
                var from = start + span.Start;
                var to = from + span.Length;
                if (from < start || to > end) continue;

                ApplySpan(span.Kind, from, to);
            }
        }

        private void ApplyLineStyle(LineInfo info, int start, int end)
        {
            switch (info.Kind)
            {
                case LineKind.Heading:
                    var size = Theme.HeadingSize(info.Level);
                    ChangeLinePart(start, end, element =>
                    {
                        element.TextRunProperties.SetFontRenderingEmSize(size);
                        element.TextRunProperties.SetForegroundBrush(Theme.Heading);
                        Restyle(element, null, FontWeights.Bold);
                    });
                    break;

                case LineKind.Fence:
                case LineKind.Code:
                    // 地はブロック全体に CodeBlockRenderer が塗る（2026-10-29。前はここで字の背景だけを塗っていた）。
                    ChangeLinePart(start, end, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(Theme.CodeFore);
                        SetFamily(element, Theme.CodeFont);
                    });
                    break;

                case LineKind.Table:
                    // 格子は描けないので、せめて打ったとおりに桁が揃うよう等幅にする。
                    ChangeLinePart(start, end, element => SetFamily(element, Theme.CodeFont));
                    DimTableRules(start, end);
                    break;

                case LineKind.Setext:
                    ChangeLinePart(start, end, element =>
                        element.TextRunProperties.SetForegroundBrush(Theme.MarkerFaint));
                    break;

                case LineKind.Quote:
                    ChangeLinePart(start, end, element =>
                        element.TextRunProperties.SetForegroundBrush(Theme.Muted));
                    break;

                case LineKind.Rule:
                    ChangeLinePart(start, end, element =>
                        element.TextRunProperties.SetForegroundBrush(Theme.RuleLine));
                    break;
            }
        }

        private void ApplySpan(SpanKind kind, int from, int to)
        {
            switch (kind)
            {
                case SpanKind.Marker:
                    ChangeLinePart(from, to, Faint);
                    break;

                case SpanKind.Strong:
                    ChangeLinePart(from, to, element => Restyle(element, null, FontWeights.Bold));
                    break;

                case SpanKind.Emphasis:
                    ChangeLinePart(from, to, element => Restyle(element, FontStyles.Italic, null));
                    break;

                case SpanKind.Strike:
                    ChangeLinePart(from, to, element =>
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
                    break;

                case SpanKind.Code:
                    ChangeLinePart(from, to, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(Theme.CodeFore);
                        element.TextRunProperties.SetBackgroundBrush(Theme.CodeBack);
                        SetFamily(element, Theme.CodeFont);
                    });
                    break;

                case SpanKind.LinkText:
                    ChangeLinePart(from, to, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(Theme.Link);
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    });
                    break;

                case SpanKind.LinkDest:
                    ChangeLinePart(from, to, Faint);
                    break;
            }
        }

        /// <summary>罫線を淡色にして中身を浮かせる。区切り行はまるごと淡色にする。</summary>
        private void DimTableRules(int start, int end)
        {
            var text = _owner.Document.GetText(start, end - start);

            if (IsRuleRow(text))
            {
                ChangeLinePart(start, end, Faint);
                return;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '|') continue;
                ChangeLinePart(start + i, start + i + 1, Faint);
            }
        }

        /// <summary>|---|:--:| のような区切り行か。</summary>
        private static bool IsRuleRow(string text)
        {
            var seen = false;

            foreach (var c in text)
            {
                if (c == '-' || c == ':') seen = true;
                else if (c != '|' && c != ' ' && c != '\t') return false;
            }

            return seen;
        }

        private static void Faint(VisualLineElement element)
        {
            element.TextRunProperties.SetForegroundBrush(Theme.MarkerFaint);
        }

        /// <summary>太字と斜体を両立させるため、今の書体を土台に差分だけ載せる。</summary>
        private static void Restyle(VisualLineElement element, FontStyle? style, FontWeight? weight)
        {
            var current = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(
                current.FontFamily,
                style ?? current.Style,
                weight ?? current.Weight,
                current.Stretch));
        }

        private static void SetFamily(VisualLineElement element, FontFamily family)
        {
            var current = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(
                family, current.Style, current.Weight, current.Stretch));
        }

    }
}

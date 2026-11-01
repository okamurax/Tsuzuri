using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace DrawingEditor
{
    /// <summary>図形の種類（JSON の type）。</summary>
    internal static class Kinds
    {
        public const string Line = "line";
        public const string Rect = "rect";
        public const string Ellipse = "ellipse";
        public const string Text = "text";
        public const string Dimension = "dimension";

        /// <summary>貼り付けた画像（2026-10-25）。四角と同じく、(X1, Y1) と (X2, Y2) が向かい合う角。中身は Image（PNG）。</summary>
        public const string Image = "image";

        public static bool IsKnown(string type)
        {
            return type == Line || type == Rect || type == Ellipse || type == Text || type == Dimension || type == Image;
        }
    }

    /// <summary>
    /// 図形1つ（SPEC 8.4.3）。寸法はすべて mm。y は下向き（画面と同じ）。
    /// ・直線・寸法線: (X1, Y1) が始点、(X2, Y2) が終点
    /// ・四角・丸: (X1, Y1) と (X2, Y2) が向かい合う角（丸は、その四角に収まる楕円）
    /// ・文字: (X1, Y1) が左上。大きさは Size（pt）。X2・Y2 は使わない
    /// ・寸法線: 数値の大きさは Size（pt）。0 なら既定（9pt）
    /// ・四角・丸: Text は中に書く文字（draw.io と同じ。真ん中にそろえ、幅で折り返す）。大きさは Size（pt）。0 なら既定（10.5pt）
    /// ・直線・寸法線の端は、ほかの図形の点につないでおける（StartShape・EndShape にその図形の Id）。つないだ図形を動かすと、端が付いてくる（2026-10-19）。
    ///   つなぐ位置は U・V: 四角・丸なら枠の中の割合（0〜1。左上が 0,0）、直線・寸法線なら始点から終点への道のりの割合（U。V は -1）
    /// ・直線は途中に折れ目を持てる（Points。x, y, x, y… の並び。始点から順に）。線の途中のダブルクリックで足し、折れ目のダブルクリックで消す（2026-10-19）
    /// ・寸法線: Offset は、測る2点から寸法線までの離れ（始点から終点へ向かって左側が正。左から右へ測ると上が正）
    /// </summary>
    [DataContract]
    internal sealed class Shape
    {
        [DataMember(Name = "type", Order = 0)] public string Type;
        [DataMember(Name = "x1", Order = 1)] public double X1;
        [DataMember(Name = "y1", Order = 2)] public double Y1;
        [DataMember(Name = "x2", Order = 3)] public double X2;
        [DataMember(Name = "y2", Order = 4)] public double Y2;
        [DataMember(Name = "text", Order = 5, EmitDefaultValue = false)] public string Text;
        [DataMember(Name = "size", Order = 6, EmitDefaultValue = false)] public double Size;
        [DataMember(Name = "offset", Order = 7, EmitDefaultValue = false)] public double Offset;

        [DataMember(Name = "id", Order = 8, EmitDefaultValue = false)] public string Id;
        [DataMember(Name = "startShape", Order = 9, EmitDefaultValue = false)] public string StartShape;
        [DataMember(Name = "startU", Order = 10, EmitDefaultValue = false)] public double StartU;
        [DataMember(Name = "startV", Order = 11, EmitDefaultValue = false)] public double StartV;
        [DataMember(Name = "endShape", Order = 12, EmitDefaultValue = false)] public string EndShape;
        [DataMember(Name = "endU", Order = 13, EmitDefaultValue = false)] public double EndU;
        [DataMember(Name = "endV", Order = 14, EmitDefaultValue = false)] public double EndV;
        [DataMember(Name = "points", Order = 15, EmitDefaultValue = false)] public List<double> Points;

        /// <summary>線の太さ（mm。文字の大きさと同じく、基準の大きさ。DrawingFile.Unit）。0 なら既定（0.25mm）。2026-10-19。</summary>
        [DataMember(Name = "lineWidth", Order = 16, EmitDefaultValue = false)] public double LineWidth;

        /// <summary>四角・丸の塗り（"#RRGGBB"。Fills の色のどれか）。null なら塗らない。2026-10-20。</summary>
        [DataMember(Name = "fill", Order = 17, EmitDefaultValue = false)] public string Fill;

        /// <summary>画像の中身（PNG を Base64 にしたもの）。画像だけが持つ。図のファイルだけで完結させるため、JSON に埋め込む（2026-10-25）。</summary>
        [DataMember(Name = "image", Order = 18, EmitDefaultValue = false)] public string Image;

        public Shape Clone()
        {
            var copy = (Shape)MemberwiseClone();
            if (Points != null) copy.Points = new List<double>(Points);   // 折れ目は別の並びにする（同じ並びを指すと、写した線と一緒に動いてしまう）
            return copy;
        }

        /// <summary>折れ目の数（直線だけ）。</summary>
        public int WaypointCount
        {
            get { return Points == null ? 0 : Points.Count / 2; }
        }

        public Point GetWaypoint(int index)
        {
            return new Point(Points[index * 2], Points[index * 2 + 1]);
        }

        public void SetWaypoint(int index, Point point)
        {
            Points[index * 2] = point.X;
            Points[index * 2 + 1] = point.Y;
        }

        public void InsertWaypoint(int index, Point point)
        {
            if (Points == null) Points = new List<double>();
            Points.InsertRange(index * 2, new[] { point.X, point.Y });
        }

        public void RemoveWaypoint(int index)
        {
            Points.RemoveRange(index * 2, 2);
            if (Points.Count == 0) Points = null;
        }

        /// <summary>線の点の並び（始点・折れ目・終点）。直線・寸法線以外は使わない。</summary>
        public List<Point> Vertices()
        {
            var points = new List<Point>(WaypointCount + 2) { Start };
            for (var i = 0; i < WaypointCount; i++) points.Add(GetWaypoint(i));
            points.Add(End);
            return points;
        }

        /// <summary>位置だけを from から写して、(dx, dy) ずらす（つながりは写さない）。動かしている間に使う。</summary>
        public void MoveFrom(Shape from, double dx, double dy)
        {
            X1 = from.X1 + dx;
            Y1 = from.Y1 + dy;
            X2 = from.X2 + dx;
            Y2 = from.Y2 + dy;

            if (from.Points == null)
            {
                Points = null;
                return;
            }

            Points = new List<double>(from.Points.Count);
            for (var i = 0; i < from.Points.Count; i += 2)
            {
                Points.Add(from.Points[i] + dx);
                Points.Add(from.Points[i + 1] + dy);
            }
        }

        /// <summary>中身をすべて写す（Id も）。つかむのをやめたときに、元に戻すため。</summary>
        public void Assign(Shape from)
        {
            Type = from.Type;
            X1 = from.X1;
            Y1 = from.Y1;
            X2 = from.X2;
            Y2 = from.Y2;
            Text = from.Text;
            Size = from.Size;
            Offset = from.Offset;
            Id = from.Id;
            StartShape = from.StartShape;
            StartU = from.StartU;
            StartV = from.StartV;
            EndShape = from.EndShape;
            EndU = from.EndU;
            EndV = from.EndV;
            Points = from.Points == null ? null : new List<double>(from.Points);
            LineWidth = from.LineWidth;
            Fill = from.Fill;
            Image = from.Image;
        }

        /// <summary>新しい Id（つなぐ相手を指すため。図の中で重ならなければよい）。</summary>
        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>四角か丸（8つのつまみで大きさを変える図形）。</summary>
        public bool IsBox
        {
            get { return Type == Kinds.Rect || Type == Kinds.Ellipse; }
        }

        /// <summary>
        /// 枠で囲まれた図形（四角・丸・画像）。8つのつまみで大きさを変え、中を押しても選べる。
        /// 塗りと中の文字は四角・丸だけ（IsBox）。
        /// </summary>
        public bool IsFrame
        {
            get { return IsBox || Type == Kinds.Image; }
        }

        /// <summary>2点で決まる図形（直線・寸法線）。</summary>
        public bool IsSegment
        {
            get { return Type == Kinds.Line || Type == Kinds.Dimension; }
        }

        public Point Start
        {
            get { return new Point(X1, Y1); }
            set { X1 = value.X; Y1 = value.Y; }
        }

        public Point End
        {
            get { return new Point(X2, Y2); }
            set { X2 = value.X; Y2 = value.Y; }
        }

        public void Move(double dx, double dy)
        {
            X1 += dx;
            Y1 += dy;
            X2 += dx;
            Y2 += dy;

            if (Points == null) return;
            for (var i = 0; i < Points.Count; i += 2)
            {
                Points[i] += dx;
                Points[i + 1] += dy;
            }
        }
    }

    /// <summary>
    /// 図のファイル（.文書名/図 ….json）。描き直すための図形の並び。見るための画像（同じ名前の .png）は別に書き出す。
    /// kind で、このエディタの図かを見分ける（同じ名前の .json が別の物なら、図として開かない）。
    /// </summary>
    [DataContract]
    internal sealed class DrawingFile
    {
        // 名前を Tsuzuri に変えた（2026-10-31）あとも、この印は変えない。描いた図のファイルに書き込まれていて、変えると今までの図が開けなくなる。
        public const string Marker = "markdown-editor-drawing";

        /// <summary>この版が読み書きできる図の版（2026-10-27）。これより新しい版の図は開かない（DrawingEditorHost）。</summary>
        public const int SupportedVersion = 1;

        [DataMember(Name = "kind", Order = 0)] public string Kind = Marker;
        [DataMember(Name = "version", Order = 1)] public int Version = SupportedVersion;

        /// <summary>
        /// 読んだときに捨てた、知らない種類の図形の数（2026-10-27。ファイルには書かない）。
        /// 新しい版で足した図形をこの版で読むと捨てるしかなく、そのまま保存するとファイルから消えてしまう。0 でなければ開かない（DrawingEditorHost）。
        /// </summary>
        public int Dropped;

        // ボード（書き出す範囲。mm）。新しい図は 160×120mm（本文・Word に入れる幅と同じ。紙の上と同じ大きさで描ける。2026-10-19。前は 297×210mm）。
        // 実寸で描くときは、ボードを広げてから描く。
        [DataMember(Name = "boardX", Order = 2)] public double BoardX;
        [DataMember(Name = "boardY", Order = 3)] public double BoardY;
        [DataMember(Name = "boardWidth", Order = 4)] public double BoardWidth = Geometry.OutputWidth;
        [DataMember(Name = "boardHeight", Order = 5)] public double BoardHeight = 120;

        /// <summary>
        /// 文字の大きさ・線の太さの基準: 紙（基準）の 1mm が図の何 mm か（2026-10-19）。ボードの大きさを変えても変わらない
        /// （前は「ボードの幅 ÷ 160」で、ボードを変えると文字・線の見え方まで変わった）。
        /// 何も描いていない間は、ボードの大きさに合わせて変わる（DrawingCanvas）。無い図（前の版）は、開いたときのボードから決める。
        /// </summary>
        [DataMember(Name = "unit", Order = 7, EmitDefaultValue = false)] public double Unit = 1;

        [DataMember(Name = "shapes", Order = 6)] public List<Shape> Shapes = new List<Shape>();

        public Rect Board
        {
            get { return new Rect(BoardX, BoardY, BoardWidth, BoardHeight); }
            set
            {
                BoardX = value.X;
                BoardY = value.Y;
                BoardWidth = value.Width;
                BoardHeight = value.Height;
            }
        }

        /// <summary>読む。このエディタの図でなければ・読めなければ null。</summary>
        public static DrawingFile Read(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            DrawingFile file;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    file = new DataContractJsonSerializer(typeof(DrawingFile)).ReadObject(stream) as DrawingFile;
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (file == null || file.Kind != Marker) return null;

            // 読み込みではコンストラクタが走らないので、無い物を埋め、知らない種類の図形は捨てる（捨てた数を覚えておく。2026-10-27）。
            var shapes = new List<Shape>();
            file.Dropped = 0;
            if (file.Shapes != null)
            {
                foreach (var shape in file.Shapes)
                {
                    if (shape == null) continue;
                    if (!Kinds.IsKnown(shape.Type))
                    {
                        file.Dropped++;
                        continue;
                    }
                    if (shape.Type == Kinds.Image && string.IsNullOrEmpty(shape.Image)) continue;   // 中身の無い画像は描けない
                    if (shape.Type == Kinds.Text && shape.Size <= 0) shape.Size = Geometry.DefaultTextSize;
                    if (string.IsNullOrEmpty(shape.Id)) shape.Id = Shape.NewId();   // Id の無い図形（前の版）
                    if (shape.Points != null && (shape.Type != Kinds.Line || shape.Points.Count % 2 != 0 || shape.Points.Count == 0)) shape.Points = null;
                    shapes.Add(shape);
                }
            }

            file.Shapes = shapes;

            // ボードの無い図（ボードを足す前の版で描いたもの）は、図形のある範囲に 5mm の余白を足した範囲にする。
            // 手で書き換えた JSON などで、数でない値（NaN・∞）のときも同じ（2026-10-27）。
            if (!(file.BoardWidth > 0) || !(file.BoardHeight > 0) || !Geometry.IsFinite(file.BoardWidth) || !Geometry.IsFinite(file.BoardHeight)
                || !Geometry.IsFinite(file.BoardX) || !Geometry.IsFinite(file.BoardY))
            {
                var bounds = Rect.Empty;
                foreach (var shape in shapes) bounds.Union(Geometry.Bounds(shape, 1));
                if (bounds.IsEmpty || !Geometry.IsFinite(bounds.Width) || !Geometry.IsFinite(bounds.Height)) bounds = new Rect(0, 0, Geometry.OutputWidth, 120);
                bounds.Inflate(5, 5);
                file.Board = bounds;
            }

            // ボードは決まった大きさの中に収める（大きすぎると画像を作れない。2026-10-27）。
            file.Board = Geometry.ClampBoard(file.Board);

            // 基準の無い図（前の版）は、ボードの幅から（前と同じ見え方）。
            if (!(file.Unit > 0) || !Geometry.IsFinite(file.Unit)) file.Unit = file.BoardWidth / Geometry.OutputWidth;

            return file;
        }

        public string Write()
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DrawingFile)).WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    /// <summary>吸い付く点と、つなぐ位置（U・V。Geometry.AnchorPoint）。</summary>
    internal struct Anchor
    {
        public readonly Point Point;
        public readonly double U;
        public readonly double V;

        public Anchor(Point point, double u, double v)
        {
            Point = point;
            U = u;
            V = v;
        }
    }

    /// <summary>寸法線の形（測る2点・寸法線の両端・向き・長さ・数値の置き場所）。</summary>
    internal struct DimensionShape
    {
        public Point A;          // 測る点（始点）
        public Point B;          // 測る点（終点）
        public Point LineA;      // 寸法線の始点側の端
        public Point LineB;      // 寸法線の終点側の端
        public Vector Direction; // 始点から終点への向き（長さ 1）
        public Vector Normal;    // 離れの向き（長さ 1。Offset が正の側）
        public Vector Outward;   // 数値を置く側（測る点から離れる向き）
        public double Length;
        public string Label;
        public double TextSize;  // 数値の大きさ（紙の上の pt）
    }

    /// <summary>図形の大きさ・形の計算。寸法はすべて mm。</summary>
    internal static class Geometry
    {
        /// <summary>1mm の画面の大きさ（拡大 100% のとき。WPF の 1 は 1/96 インチ）。</summary>
        public const double PxPerMm = 96.0 / 25.4;

        /// <summary>
        /// 本文・Word に入れる図の幅（mm）。ボードがどの大きさでも、この幅に縮めて（広げて）書き出す（2026-10-18）。
        /// A4 縦・余白 20mm の本文の幅（170mm）に収まる。
        /// </summary>
        public const double OutputWidth = 160;

        /// <summary>
        /// ボードの大きさの限り（2026-10-27）。辺は 10mm 〜 100m、高さは幅の 10 倍まで。
        /// 書き出す画像は幅 160mm にそろえるので、縦に長いほど画素が増える。限りが無いと、画像を作れず（RenderTargetBitmap が投げる）、描いた図を保存できなかった。
        /// </summary>
        public const double MinBoardSize = 10;
        public const double MaxBoardSize = 100000;
        public const double MaxBoardAspect = 10;

        /// <summary>右の欄に打てる値の大きさの限り（mm・度・pt。2026-10-27）。これを超える値と、数でない値（NaN・∞）は入れない。</summary>
        public const double MaxFieldValue = 100000;

        /// <summary>数か（NaN・∞ でないか）。.NET Framework 4.8 には double.IsFinite が無いので自前で。</summary>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>ボードを限りの中に収める（左上は動かさない。縦に長すぎれば高さを縮める）。</summary>
        public static Rect ClampBoard(Rect board)
        {
            var x = IsFinite(board.X) ? board.X : 0;
            var y = IsFinite(board.Y) ? board.Y : 0;
            var width = IsFinite(board.Width) ? Math.Max(MinBoardSize, Math.Min(MaxBoardSize, board.Width)) : OutputWidth;
            var height = IsFinite(board.Height) ? Math.Max(MinBoardSize, Math.Min(MaxBoardSize, board.Height)) : 120;
            height = Math.Min(height, width * MaxBoardAspect);
            return new Rect(x, y, width, height);
        }

        /// <summary>
        /// 線の太さの既定（mm）。ここから下の文字・寸法の大きさは、基準（DrawingFile.Unit）の上での大きさ。
        /// 図形は実寸で描くので、描く面では基準（unit。紙の 1mm が図の何 mm か）を掛けて使う。大きなボードに描くときも、基準を合わせれば読める。
        /// </summary>
        public const double LineWidth = 0.25;

        /// <summary>図形の線の太さ（基準の mm）。持っていなければ既定。</summary>
        public static double LineWidthOf(Shape shape)
        {
            return shape.LineWidth > 0 ? shape.LineWidth : LineWidth;
        }

        /// <summary>文字の大きさの既定（pt）。</summary>
        public const double DefaultTextSize = 10.5;

        /// <summary>寸法の数値の大きさの既定（pt）。寸法線ごとに右の欄で変えられる。</summary>
        public const double DimensionTextSize = 9;

        /// <summary>寸法線の離れの既定（紙の上の mm）。</summary>
        public const double DefaultOffset = 8;

        public static readonly FontFamily Font = new FontFamily("Yu Gothic UI");
        public static readonly Typeface Face = new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static double PtToMm(double pt)
        {
            return pt * 25.4 / 72;
        }

        /// <summary>
        /// 文字の形（大きさは図の mm の単位で作る。描くときに図と一緒に拡大する）。sizePt は紙の上の大きさ、unit は紙の 1mm が図の何 mm か。
        /// </summary>
        public static FormattedText MakeText(string text, double sizePt, Brush brush, double unit)
        {
            return new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face, Math.Max(0.01, PtToMm(sizePt) * unit), brush, 1.0);
        }

        /// <summary>図形を囲む四角（mm）。unit は紙の 1mm が図の何 mm か（文字・寸法の数値の大きさに使う）。</summary>
        public static Rect Bounds(Shape shape, double unit)
        {
            switch (shape.Type)
            {
                case Kinds.Text:
                    {
                        var text = MakeText(shape.Text, shape.Size, Brushes.Black, unit);
                        var size = PtToMm(shape.Size) * unit;
                        var height = Math.Max(text.Height, size * 1.3);
                        return new Rect(shape.X1, shape.Y1, Math.Max(text.WidthIncludingTrailingWhitespace, size), height);
                    }

                case Kinds.Dimension:
                    {
                        var dimension = Dimension(shape);
                        var rect = new Rect(dimension.A, dimension.B);
                        rect.Union(dimension.LineA);
                        rect.Union(dimension.LineB);
                        rect.Union(LabelRect(dimension, unit));
                        return rect;
                    }

                default:
                    {
                        var rect = new Rect(shape.Start, shape.End);
                        for (var i = 0; i < shape.WaypointCount; i++) rect.Union(shape.GetWaypoint(i));
                        return rect;
                    }
            }
        }

        /// <summary>寸法線の形を出す。</summary>
        public static DimensionShape Dimension(Shape shape)
        {
            var result = new DimensionShape();
            result.A = shape.Start;
            result.B = shape.End;

            var delta = result.B - result.A;
            result.Length = delta.Length;
            result.Direction = result.Length > 1e-9 ? delta / result.Length : new Vector(1, 0);
            result.Normal = new Vector(result.Direction.Y, -result.Direction.X);   // 進む向きの左（y は下向きなので、左から右なら上）
            result.LineA = result.A + result.Normal * shape.Offset;
            result.LineB = result.B + result.Normal * shape.Offset;
            result.Outward = shape.Offset >= 0 ? result.Normal : -result.Normal;
            result.Label = FormatLength(result.Length);
            result.TextSize = shape.Size > 0 ? shape.Size : DimensionTextSize;   // 大きさを持たない寸法線（前の版）は既定の大きさ
            return result;
        }

        /// <summary>寸法の数値。整数ならそのまま、そうでなければ小数1桁。</summary>
        public static string FormatLength(double length)
        {
            var rounded = Math.Round(length, 1);
            return Math.Abs(rounded - Math.Round(rounded)) < 1e-9
                ? Math.Round(rounded).ToString("0", CultureInfo.InvariantCulture)
                : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>寸法の数値の真ん中（寸法線の真ん中から、外側へ字の高さの半分と少し）。</summary>
        public static Point LabelCenter(DimensionShape dimension, double textHeight, double unit)
        {
            var middle = dimension.LineA + (dimension.LineB - dimension.LineA) / 2;
            return middle + dimension.Outward * (textHeight / 2 + 0.6 * unit);
        }

        /// <summary>寸法の数値の傾き（度）。逆さにならないよう、左向きなら 180 度回す。</summary>
        public static double LabelAngle(DimensionShape dimension)
        {
            var angle = Math.Atan2(dimension.Direction.Y, dimension.Direction.X) * 180 / Math.PI;
            if (angle > 90.0001) angle -= 180;
            if (angle < -90.0001) angle += 180;
            return angle;
        }

        /// <summary>寸法の数値をおおまかに囲む四角（回っていても収まるよう、長い方の辺で囲む）。</summary>
        public static Rect LabelRect(DimensionShape dimension, double unit)
        {
            var text = MakeText(dimension.Label, dimension.TextSize, Brushes.Black, unit);
            var center = LabelCenter(dimension, text.Height, unit);
            var half = Math.Max(text.Width, text.Height) / 2;
            return new Rect(center.X - half, center.Y - half, half * 2, half * 2);
        }

        /// <summary>四角の吸い付く点（枠の中の割合）: 4つの角と、4つの辺の中点。</summary>
        private static readonly double[][] RectAnchors =
        {
            new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 1.0 },
            new[] { 0.5, 0.0 }, new[] { 1.0, 0.5 }, new[] { 0.5, 1.0 }, new[] { 0.0, 0.5 },
        };

        /// <summary>丸の吸い付く点（枠の中の割合）: 上下左右。</summary>
        private static readonly double[][] EllipseAnchors =
        {
            new[] { 0.5, 0.0 }, new[] { 1.0, 0.5 }, new[] { 0.5, 1.0 }, new[] { 0.0, 0.5 },
        };

        /// <summary>
        /// 吸い付く点と、つなぐ位置（U・V）。KeyPoints と同じ点。線をつなぐときに、どの図形のどこかを覚えるのに使う。
        /// </summary>
        public static IEnumerable<Anchor> Anchors(Shape shape)
        {
            switch (shape.Type)
            {
                case Kinds.Line:
                case Kinds.Dimension:
                    {
                        // 始点・終点・折れ目と、各区間の中点。位置は始点からの道のりの割合。
                        var points = shape.Type == Kinds.Line ? shape.Vertices() : new List<Point> { shape.Start, shape.End };
                        var total = PathLength(points);
                        var walked = 0.0;

                        yield return new Anchor(points[0], 0, -1);
                        for (var i = 1; i < points.Count; i++)
                        {
                            var length = (points[i] - points[i - 1]).Length;
                            yield return new Anchor(points[i - 1] + (points[i] - points[i - 1]) / 2, Fraction(walked + length / 2, total), -1);
                            walked += length;
                            yield return new Anchor(points[i], i == points.Count - 1 ? 1 : Fraction(walked, total), -1);
                        }
                    }
                    break;

                case Kinds.Rect:
                case Kinds.Image:
                    foreach (var uv in RectAnchors) yield return new Anchor(AnchorPoint(shape, uv[0], uv[1]), uv[0], uv[1]);
                    break;

                case Kinds.Ellipse:
                    foreach (var uv in EllipseAnchors) yield return new Anchor(AnchorPoint(shape, uv[0], uv[1]), uv[0], uv[1]);
                    break;
            }
        }

        private static double PathLength(List<Point> points)
        {
            var total = 0.0;
            for (var i = 1; i < points.Count; i++) total += (points[i] - points[i - 1]).Length;
            return total;
        }

        private static double Fraction(double walked, double total)
        {
            return total > 1e-12 ? walked / total : 0;
        }

        /// <summary>つないだ線の端を、相手の図形の今の位置へ動かす。相手が無くなっていたら、つながりを外す（線はその場に残す）。
        /// 描く面（DrawingCanvas）と、画面を出さずに画像を作るとき（DrawingEditorHost.RenderPng。2026-11-01）で使う。</summary>
        public static void UpdateConnections(IList<Shape> shapes)
        {
            var byId = new Dictionary<string, Shape>();
            foreach (var shape in shapes)
            {
                if (!string.IsNullOrEmpty(shape.Id) && !byId.ContainsKey(shape.Id)) byId[shape.Id] = shape;
            }

            // 線の端に線をつないでいることもあるので、2回まわして追いつかせる。
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var shape in shapes)
                {
                    if (!shape.IsSegment) continue;

                    Shape target;
                    if (shape.StartShape != null)
                    {
                        if (byId.TryGetValue(shape.StartShape, out target) && !ReferenceEquals(target, shape))
                            shape.Start = Geometry.AnchorPoint(target, shape.StartU, shape.StartV);
                        else
                            shape.StartShape = null;
                    }

                    if (shape.EndShape != null)
                    {
                        if (byId.TryGetValue(shape.EndShape, out target) && !ReferenceEquals(target, shape))
                            shape.End = Geometry.AnchorPoint(target, shape.EndU, shape.EndV);
                        else
                            shape.EndShape = null;
                    }
                }
            }
        }

        /// <summary>つないだ位置（U・V）の今の点。四角・丸は枠の中の割合、直線・寸法線は始点からの道のりの割合（折れ目をたどる）。</summary>
        public static Point AnchorPoint(Shape shape, double u, double v)
        {
            if (shape.IsSegment)
            {
                var points = shape.Type == Kinds.Line ? shape.Vertices() : new List<Point> { shape.Start, shape.End };
                var target = PathLength(points) * Math.Max(0, Math.Min(1, u));
                for (var i = 1; i < points.Count; i++)
                {
                    var length = (points[i] - points[i - 1]).Length;
                    if (target <= length || i == points.Count - 1)
                        return length > 1e-12 ? points[i - 1] + (points[i] - points[i - 1]) * Math.Min(1, target / length) : points[i];
                    target -= length;
                }

                return points[points.Count - 1];
            }

            var r = Bounds(shape, 1);
            return new Point(r.Left + r.Width * u, r.Top + r.Height * v);
        }

        /// <summary>
        /// 吸い付く点。図形の端（線の端・四角の角・丸の上下左右）と、直線の部分の真ん中（線の中点・四角の辺の中点）。
        /// 中心には吸い付かない（2026-10-18。AutoCAD のように、端と中点だけにした）。
        /// </summary>
        public static IEnumerable<Point> KeyPoints(Shape shape)
        {
            return Anchors(shape).Select(anchor => anchor.Point);
        }

        /// <summary>その点が図形の上か（tolerance は mm）。線だけの図形は、線の近くだけ。</summary>
        public static bool Hits(Shape shape, Point point, double tolerance, double unit)
        {
            switch (shape.Type)
            {
                case Kinds.Line:
                    {
                        var points = shape.Vertices();
                        for (var i = 1; i < points.Count; i++)
                        {
                            if (DistanceToSegment(point, points[i - 1], points[i]) <= tolerance) return true;
                        }
                        return false;
                    }

                case Kinds.Rect:
                case Kinds.Image:
                    {
                        // 中を押しても選べる（draw.io と同じ。中に文字を書くため。2026-10-19。前は線の近くだけ）。
                        var outer = Bounds(shape, unit);
                        outer.Inflate(tolerance, tolerance);
                        return outer.Contains(point);
                    }

                case Kinds.Ellipse:
                    {
                        var r = Bounds(shape, unit);
                        var rx = r.Width / 2;
                        var ry = r.Height / 2;
                        if (rx < tolerance || ry < tolerance)
                        {
                            var outer = r;
                            outer.Inflate(tolerance, tolerance);
                            return outer.Contains(point);
                        }

                        var dx = (point.X - (r.Left + rx)) / rx;
                        var dy = (point.Y - (r.Top + ry)) / ry;
                        var distance = Math.Sqrt(dx * dx + dy * dy);
                        return distance <= 1 || (distance - 1) * Math.Min(rx, ry) <= tolerance;   // 中も選べる
                    }

                case Kinds.Text:
                    {
                        var r = Bounds(shape, unit);
                        r.Inflate(tolerance, tolerance);
                        return r.Contains(point);
                    }

                case Kinds.Dimension:
                    {
                        var dimension = Dimension(shape);
                        if (DistanceToSegment(point, dimension.LineA, dimension.LineB) <= tolerance) return true;
                        if (DistanceToSegment(point, dimension.A, dimension.LineA) <= tolerance) return true;
                        if (DistanceToSegment(point, dimension.B, dimension.LineB) <= tolerance) return true;
                        return LabelRect(dimension, unit).Contains(point);
                    }
            }

            return false;
        }

        /// <summary>図形の直線の部分（交点を求めるため）。直線はそのもの、四角は4つの辺。丸・文字・寸法線は持たない。</summary>
        public static IEnumerable<KeyValuePair<Point, Point>> Segments(Shape shape)
        {
            if (shape.Type == Kinds.Line)
            {
                var points = shape.Vertices();
                for (var i = 1; i < points.Count; i++) yield return new KeyValuePair<Point, Point>(points[i - 1], points[i]);
            }
            else if (shape.Type == Kinds.Rect || shape.Type == Kinds.Image)
            {
                var r = Bounds(shape, 1);
                yield return new KeyValuePair<Point, Point>(r.TopLeft, r.TopRight);
                yield return new KeyValuePair<Point, Point>(r.TopRight, r.BottomRight);
                yield return new KeyValuePair<Point, Point>(r.BottomRight, r.BottomLeft);
                yield return new KeyValuePair<Point, Point>(r.BottomLeft, r.TopLeft);
            }
        }

        /// <summary>
        /// 2本の線の交点。2本目はいつも線分（端まで）。1本目は firstInfinite なら両側へ延ばした直線として、そうでなければ線分として扱う。
        /// 平行なら・線分の外なら false。
        /// </summary>
        public static bool Intersect(Point a1, Point a2, Point b1, Point b2, bool firstInfinite, out Point point)
        {
            point = new Point();

            var r = a2 - a1;
            var s = b2 - b1;
            var denominator = Vector.CrossProduct(r, s);
            if (Math.Abs(denominator) < 1e-12 || r.LengthSquared < 1e-12 || s.LengthSquared < 1e-12) return false;

            var t = Vector.CrossProduct(b1 - a1, s) / denominator;   // 1本目の上の位置
            var u = Vector.CrossProduct(b1 - a1, r) / denominator;   // 2本目の上の位置

            const double Slack = 1e-9;
            if (u < -Slack || u > 1 + Slack) return false;
            if (!firstInfinite && (t < -Slack || t > 1 + Slack)) return false;

            point = a1 + r * t;
            return true;
        }

        public static double DistanceToSegment(Point point, Point a, Point b)
        {
            var segment = b - a;
            var lengthSquared = segment.LengthSquared;
            if (lengthSquared < 1e-12) return (point - a).Length;

            var t = Math.Max(0, Math.Min(1, Vector.Multiply(point - a, segment) / lengthSquared));
            return (point - (a + segment * t)).Length;
        }
    }

    /// <summary>
    /// 四角・丸の塗りの色（2026-10-20）。中の文字（黒）が読めて、文書に載せても浮かない薄い色だけにする。
    /// JSON には色そのもの（"#RRGGBB"）を書く。並びを変えても前の図の色は変わらない。
    /// </summary>
    internal static class Fills
    {
        /// <summary>右の欄に並べる色（名前と色）。先頭の「なし」は null。</summary>
        public static readonly KeyValuePair<string, string>[] Choices =
        {
            new KeyValuePair<string, string>("なし", null),
            new KeyValuePair<string, string>("白", "#FFFFFF"),
            new KeyValuePair<string, string>("灰", "#E4E6EA"),
            new KeyValuePair<string, string>("青", "#DCE8F8"),
            new KeyValuePair<string, string>("緑", "#DDF0DC"),
            new KeyValuePair<string, string>("黄", "#FFF3C4"),
            new KeyValuePair<string, string>("赤", "#FBDEDE"),
        };

        private static readonly Dictionary<string, Brush> Cache = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        /// <summary>塗りの色の筆。塗らない・読めない色なら null（枠だけ）。</summary>
        public static Brush BrushOf(string fill)
        {
            if (string.IsNullOrEmpty(fill)) return null;

            Brush brush;
            if (Cache.TryGetValue(fill, out brush)) return brush;

            try
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill));
                brush.Freeze();
            }
            catch (Exception)
            {
                brush = null;   // 手で書き換えた JSON など。枠だけ描く
            }

            Cache[fill] = brush;
            return brush;
        }
    }

    /// <summary>
    /// 図形を描く。画面（エディタ）と画像（書き出し）で同じ描き方を使う。
    /// 呼ぶ側が mm から画面への拡大を DrawingContext に入れておく（線の太さ・字の大きさは mm）。
    /// </summary>
    internal static class Renderer
    {
        /// <param name="unit">紙の 1mm が図の何 mm か（線の太さ・文字・寸法の大きさに掛ける）。</param>
        public static void Draw(DrawingContext dc, IList<Shape> shapes, Brush ink, Shape skip, double unit)
        {
            foreach (var shape in shapes)
            {
                // 打っている途中の図形は、その字だけ描かない（打つ欄が上に重なる）。四角・丸は枠は描く。
                var editing = ReferenceEquals(shape, skip);
                if (editing && shape.Type == Kinds.Text) continue;
                DrawShape(dc, shape, ink, MakePen(ink, Geometry.LineWidthOf(shape) * unit), unit, !editing);
            }
        }

        /// <summary>線（太さは図の mm）。端と折れ目は丸くする。</summary>
        public static Pen MakePen(Brush ink, double width)
        {
            var pen = new Pen(ink, width);
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
            pen.Freeze();
            return pen;
        }

        public static void DrawShape(DrawingContext dc, Shape shape, Brush ink, Pen pen, double unit, bool label = true)
        {
            switch (shape.Type)
            {
                case Kinds.Line:
                    if (shape.WaypointCount == 0)
                    {
                        dc.DrawLine(pen, shape.Start, shape.End);
                    }
                    else
                    {
                        // 折れ線。折れ目は丸くつなぐ。
                        var geometry = new StreamGeometry();
                        using (var context = geometry.Open())
                        {
                            var points = shape.Vertices();
                            context.BeginFigure(points[0], false, false);
                            for (var i = 1; i < points.Count; i++) context.LineTo(points[i], true, true);
                        }
                        geometry.Freeze();
                        dc.DrawGeometry(null, pen, geometry);
                    }
                    break;

                case Kinds.Rect:
                    dc.DrawRectangle(Fills.BrushOf(shape.Fill), pen, Geometry.Bounds(shape, unit));
                    if (label) DrawLabel(dc, shape, ink, unit);
                    break;

                case Kinds.Ellipse:
                    {
                        var r = Geometry.Bounds(shape, unit);
                        dc.DrawEllipse(Fills.BrushOf(shape.Fill), pen, new Point(r.Left + r.Width / 2, r.Top + r.Height / 2), r.Width / 2, r.Height / 2);
                        if (label) DrawLabel(dc, shape, ink, unit);
                    }
                    break;

                case Kinds.Text:
                    dc.DrawText(Geometry.MakeText(shape.Text, shape.Size, ink, unit), shape.Start);
                    break;

                case Kinds.Dimension:
                    DrawDimension(dc, shape, ink, pen, unit);
                    break;

                case Kinds.Image:
                    {
                        var image = ImageOf(shape.Image);
                        var r = Geometry.Bounds(shape, unit);
                        if (image != null) dc.DrawImage(image, r);
                        else dc.DrawRectangle(null, pen, r);   // 読めない画像は枠だけ
                    }
                    break;
            }
        }

        /// <summary>読んだ画像（読めなかったときは Image が null）。ConditionalWeakTable は null を持てないので包む。</summary>
        private sealed class ImageEntry
        {
            public ImageSource Image;
        }

        /// <summary>
        /// 読んだ画像の覚え（2026-10-27）。中身の文字列そのもの（参照）で引く。前は中身の文字列をキーにした Dictionary で、
        /// 描くたびに数 MB の文字列のハッシュを計算し、消した画像の分も閉じるまで持ち続けた。
        /// 写した図形（Clone・Assign・元に戻すの覚え）は同じ文字列を指すので、そのまま当たる。図形が無くなり文字列が捨てられれば、覚えも消える。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, ImageEntry> Images =
            new System.Runtime.CompilerServices.ConditionalWeakTable<string, ImageEntry>();

        /// <summary>画像の中身（PNG の Base64）を描ける形にする。同じ中身は使い回す。読めなければ null。</summary>
        public static ImageSource ImageOf(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            return Images.GetValue(data, LoadImage).Image;
        }

        private static ImageEntry LoadImage(string data)
        {
            var entry = new ImageEntry();
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(data));
                bitmap.EndInit();
                bitmap.Freeze();   // 別のスレッドからも描けるように・変わらないので固める
                entry.Image = bitmap;
            }
            catch (Exception)
            {
                entry.Image = null;   // 手で書き換えた JSON など
            }

            return entry;
        }

        /// <summary>四角・丸の中の文字の余白（紙の上の mm）。</summary>
        public const double LabelPadding = 1.5;

        /// <summary>四角・丸の中の文字（draw.io と同じ）。真ん中にそろえ、枠の幅で折り返し、縦も真ん中に置く。</summary>
        private static void DrawLabel(DrawingContext dc, Shape shape, Brush ink, double unit)
        {
            if (string.IsNullOrEmpty(shape.Text)) return;

            var r = Geometry.Bounds(shape, unit);
            var text = Geometry.MakeText(shape.Text, LabelSize(shape), ink, unit);
            text.MaxTextWidth = Math.Max(0.1, r.Width - LabelPadding * 2 * unit);
            text.TextAlignment = TextAlignment.Center;
            dc.DrawText(text, new Point(r.Left + LabelPadding * unit, r.Top + (r.Height - text.Height) / 2));
        }

        /// <summary>四角・丸の中の文字の大きさ（pt）。持っていなければ既定。</summary>
        public static double LabelSize(Shape shape)
        {
            return shape.Size > 0 ? shape.Size : Geometry.DefaultTextSize;
        }

        /// <summary>
        /// 寸法線: 測る2点から出る補助線（点から少し離して始め、寸法線より少し先まで）、両端に矢印の付いた寸法線、真ん中に長さ（mm）。
        /// </summary>
        private static void DrawDimension(DrawingContext dc, Shape shape, Brush ink, Pen pen, double unit)
        {
            // 大きさはどれも紙の上の mm。
            var Gap = 1.0 * unit;          // 測る点から補助線までの空き
            var Overshoot = 1.5 * unit;    // 補助線が寸法線を越える長さ
            var ArrowLength = 2.5 * unit;
            var ArrowHalfWidth = 0.6 * unit;

            var d = Geometry.Dimension(shape);
            if (d.Length < 1e-6) return;

            if (Math.Abs(shape.Offset) > Gap)
            {
                var side = shape.Offset >= 0 ? d.Normal : -d.Normal;
                dc.DrawLine(pen, d.A + side * Gap, d.LineA + side * Overshoot);
                dc.DrawLine(pen, d.B + side * Gap, d.LineB + side * Overshoot);
            }

            dc.DrawLine(pen, d.LineA, d.LineB);

            // 矢印は寸法線の内側へ向けて置く（先が寸法線の端）。寸法線が短すぎるときは描かない。
            if (d.Length > ArrowLength * 2)
            {
                DrawArrow(dc, ink, d.LineA, d.Direction, d.Normal, ArrowLength, ArrowHalfWidth);
                DrawArrow(dc, ink, d.LineB, -d.Direction, d.Normal, ArrowLength, ArrowHalfWidth);
            }

            var text = Geometry.MakeText(d.Label, d.TextSize, ink, unit);
            var center = Geometry.LabelCenter(d, text.Height, unit);
            dc.PushTransform(new RotateTransform(Geometry.LabelAngle(d), center.X, center.Y));
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            dc.Pop();
        }

        private static void DrawArrow(DrawingContext dc, Brush ink, Point tip, Vector inward, Vector normal, double length, double halfWidth)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(tip, true, true);
                context.LineTo(tip + inward * length + normal * halfWidth, true, false);
                context.LineTo(tip + inward * length - normal * halfWidth, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(ink, null, geometry);
        }

        /// <summary>
        /// 画像（PNG）にする。白地に黒、ボードの範囲（外の図形は切る）を、幅 OutputWidth（160mm）に縮めて（広げて）描く。高さはボードの縦横の比のまま。
        /// 文字・線は、図形と一緒に縮む（描く面と同じ見え方。unit は文字・線の大きさの基準）。
        /// 画素の数は「mm × 画面の 1mm の大きさ × 画面の拡大率」。本体は画素の数を画面の拡大率で割って表示し、
        /// Word にも同じ考えで入れるので、どちらでも幅 160mm で出る（拡大率の高い画面でも、にじまない）。
        /// </summary>
        public static byte[] RenderPng(IList<Shape> shapes, Rect board, double unit, double dpiScale)
        {
            var scale = Geometry.PxPerMm * Geometry.OutputWidth / board.Width;   // 図の 1mm が、書き出す図の何 DIP か
            var widthDip = board.Width * scale;
            var heightDip = board.Height * scale;

            var visual = new DrawingVisual();

            // 貼った画像を縮めて書き出すとき、粗くならないように（2026-10-27。既定は線形で、大きく縮めるとギザギザになる）。
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, widthDip, heightDip));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, widthDip, heightDip)));
                dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -board.Left * scale, -board.Top * scale));
                Draw(dc, shapes, Brushes.Black, null, unit);
                dc.Pop();
                dc.Pop();
            }

            // 画素の数に限りを付ける（2026-10-27）。ボードの大きさにも限りがあるが（Geometry.MaxBoardAspect）、拡大率の高い画面で縦に長い図だと、
            // まだ大きすぎることがある。超えるときは、拡大率を下げて作る（本文では少しにじむが、保存できないよりよい）。
            const double MaxPixels = 40000000;
            if (!Geometry.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
            var pixels = widthDip * dpiScale * heightDip * dpiScale;
            if (pixels > MaxPixels) dpiScale *= Math.Sqrt(MaxPixels / pixels);

            var pixelWidth = Math.Max(1, (int)Math.Ceiling(widthDip * dpiScale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(heightDip * dpiScale));

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }
    }
}

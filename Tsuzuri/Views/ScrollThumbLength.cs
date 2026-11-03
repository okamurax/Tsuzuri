using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Tsuzuri.Views
{
    /// <summary>
    /// スクロールバーのつまみを、短くなりすぎないようにする（2026-11-03。SPEC 16章）。App.xaml のスクロールバーの Track.ViewportSize に使う。
    ///
    /// WPF の Track は、つまみの長さを「見えている量 ÷ 全体」で決め、下限は Windows のボタンの高さの半分（約 8px）しかない。
    /// Thumb の MinHeight は長さの計算に使われない。行の多い文書では、つまみが余白（上下 2px）を引いて 4px ほどになり、掴めなかった。
    /// そこで Track にだけ、見えている量を大きく見せる。つまみの位置もこの値で計算されるので、つまみの動きと中身の位置はずれない。
    /// ページ送り（つまみの外を押す）の量は ScrollBar の本当の値を使うので変わらない。
    ///
    /// 値: ViewportSize・Minimum・Maximum・ActualHeight・ActualWidth・Orientation（どれも ScrollBar のもの）。
    /// </summary>
    public sealed class ScrollThumbLength : IMultiValueConverter
    {
        /// <summary>つまみの長さの下限（px）。</summary>
        public double MinLength { get; set; } = 40;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 6) return 0.0;
            if (!(values[0] is double) || !(values[1] is double) || !(values[2] is double) || !(values[3] is double) || !(values[4] is double))
                return values.Length > 0 && values[0] is double ? values[0] : 0.0;

            var viewport = (double)values[0];
            var range = (double)values[2] - (double)values[1];
            var vertical = !(values[5] is Orientation) || (Orientation)values[5] == Orientation.Vertical;
            var length = vertical ? (double)values[3] : (double)values[4];

            // つまみの長さ = length × viewport ÷ (range + viewport)。これが MinLength 以上になる viewport を求める。
            if (double.IsNaN(viewport) || range <= 0 || length <= MinLength * 2) return viewport;

            var least = MinLength * range / (length - MinLength);
            return Math.Max(viewport, least);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

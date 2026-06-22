using System;
using System.Globalization;
using System.Windows.Data;

namespace PieceSolver
{
    // Remaps a slider value within [min, max] to a 0-100% readout string. Reusable across sliders:
    // bind a readout TextBlock to a MultiBinding of the slider's {Value, Minimum, Maximum}. Keeping
    // the readout as a percentage of the actual range means the displayed scale never drifts from
    // the slider's bounds.
    public sealed class RangeToPercentConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3 || values[0] is not double v || values[1] is not double min || values[2] is not double max)
                return "";
            double t = max > min ? (v - min) / (max - min) : 0.0;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return (int)Math.Round(t * 100) + "%";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // Shows a fraction directly as a percent (value × 100), e.g. 1.0 -> "100%", 3.0 -> "300%". For
    // sliders whose value IS the fraction (Strength, Flow, Softness, deCraze) and may exceed 100%.
    public sealed class FractionToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? (int)Math.Round(d * 100) + "%" : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

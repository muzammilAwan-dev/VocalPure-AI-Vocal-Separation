using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VocalPure.Converters
{
    /// <summary>
    /// Converts boolean values to Visibility enum values.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public bool Collapse { get; set; } = true;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;
            if (Invert) boolValue = !boolValue;

            return boolValue
                ? Visibility.Visible
                : (Collapse ? Visibility.Collapsed : Visibility.Hidden);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                bool result = visibility == Visibility.Visible;
                return Invert ? !result : result;
            }
            return false;
        }
    }

    /// <summary>
    /// Converts boolean values to their inverse.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(bool))]
    public sealed class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }
    }

    /// <summary>
    /// Converts a phase to visibility (visible when active processing).
    /// </summary>
    [ValueConversion(typeof(ProcessingPhase), typeof(Visibility))]
    public sealed class PhaseToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Models.ProcessingPhase phase)
            {
                bool isActive = phase != Models.ProcessingPhase.Idle && !phase.IsTerminal();
                return isActive ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Converts a progress value (0-1) to percentage string.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public sealed class ProgressToPercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double progress)
            {
                return $"{progress * 100:F0}%";
            }
            return "0%";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Converts time span to formatted string (HH:mm:ss).
    /// </summary>
    [ValueConversion(typeof(TimeSpan), typeof(string))]
    public sealed class TimeSpanToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TimeSpan ts)
            {
                if (ts.TotalHours >= 1)
                    return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
                return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
            if (value is null)
                return "--:--";
            return value.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Converts a processing phase to a status color brush.
    /// </summary>
    [ValueConversion(typeof(Models.ProcessingPhase), typeof(System.Windows.Media.Brush))]
    public sealed class PhaseToBrushConverter : IValueConverter
    {
        public System.Windows.Media.Brush ActiveBrush { get; set; } = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DodgerBlue);
        public System.Windows.Media.Brush SuccessBrush { get; set; } = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LimeGreen);
        public System.Windows.Media.Brush ErrorBrush { get; set; } = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Crimson);
        public System.Windows.Media.Brush CancelledBrush { get; set; } = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
        public System.Windows.Media.Brush IdleBrush { get; set; } = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Models.ProcessingPhase phase)
            {
                return phase switch
                {
                    Models.ProcessingPhase.Completed => SuccessBrush,
                    Models.ProcessingPhase.Error => ErrorBrush,
                    Models.ProcessingPhase.Cancelled => CancelledBrush,
                    Models.ProcessingPhase.Idle => IdleBrush,
                    _ => ActiveBrush
                };
            }
            return IdleBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

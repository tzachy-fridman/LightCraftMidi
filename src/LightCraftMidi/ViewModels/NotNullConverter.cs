using System.Globalization;
using System.Windows.Data;

namespace LightCraftMidi.ViewModels;

public sealed class NotNullConverter : IValueConverter
{
    public static NotNullConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value != null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

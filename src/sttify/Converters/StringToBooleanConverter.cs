using System.Globalization;
using System.Windows.Data;

namespace Sttify.Converters;

public class StringToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string stringValue && parameter is string parameterValue)
        {
            return string.Equals(stringValue, parameterValue, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter is string parameterValue)
        {
            return parameterValue;
        }
        return System.Windows.Data.Binding.DoNothing;
    }
}

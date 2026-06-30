using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AudioMixerWin.Core.Converters;

public class SelectedToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected =
        new(Color.FromArgb(0xFF, 0x46, 0xC2, 0x8E));
    private static readonly SolidColorBrush None =
        new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Selected : None;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public class SelectedToBorderThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? new Thickness(2) : new Thickness(0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

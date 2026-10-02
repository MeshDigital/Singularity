using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Singularity.ViewModels;

namespace Singularity.Views.Avalonia.Converters;

/// <summary>
/// Converts RepeatMode to color for visual indication.
/// Off = Gray, One/All = Green (Spotify-like)
/// </summary>
public class RepeatModeColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RepeatMode mode)
        {
            return mode == RepeatMode.Off ? "#666666" : "#1DB954";
        }
        return "#666666";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

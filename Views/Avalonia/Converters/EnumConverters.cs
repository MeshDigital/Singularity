using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Singularity.Models;

namespace Singularity.Views.Avalonia.Converters
{
    public static class EnumConverters
    {
        public static IValueConverter BouncerModeEquals { get; } =
            new FuncValueConverter<BouncerMode, BouncerMode, bool>((value, param) => value == param);

        public static EnumToBooleanConverter Instance { get; } = new();
        public static EnumToBooleanConverter NegatedInstance { get; } = new() { Negate = true };
    }
}

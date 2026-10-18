using System;
using System.Globalization;

namespace DataPieCore;

public static class NumericDisplay
{
    // Format actual numeric values only: text identifiers and edit drafts retain their precision.
    public static string Format(object value, IFormatProvider provider) => value switch
    {
        float or double or decimal
            => ((IFormattable)value).ToString("F2", provider),
        _ => null
    };
}

using System.Globalization;

namespace RaidReplay.Core.Parsing;

/// <summary>Allocation-free parsers for the numeric formats found in ACT network logs.</summary>
public static class Utf8Num
{
    /// <summary>Parses up to 8 hex digits. Returns 0 for empty or malformed input.</summary>
    public static uint Hex(ReadOnlySpan<byte> s)
    {
        uint value = 0;
        var n = 0;
        foreach (var c in s)
        {
            uint d;
            if (c >= (byte)'0' && c <= (byte)'9')
                d = (uint)(c - '0');
            else if (c >= (byte)'A' && c <= (byte)'F')
                d = (uint)(c - 'A' + 10);
            else if (c >= (byte)'a' && c <= (byte)'f')
                d = (uint)(c - 'a' + 10);
            else
                return 0;
            value = (value << 4) | d;
            if (++n > 8)
                return 0;
        }

        return value;
    }

    public static bool TryHex(ReadOnlySpan<byte> s, out uint value)
    {
        value = 0;
        if (s.IsEmpty || s.Length > 8)
            return false;
        foreach (var c in s)
        {
            uint d;
            if (c >= (byte)'0' && c <= (byte)'9')
                d = (uint)(c - '0');
            else if (c >= (byte)'A' && c <= (byte)'F')
                d = (uint)(c - 'A' + 10);
            else if (c >= (byte)'a' && c <= (byte)'f')
                d = (uint)(c - 'a' + 10);
            else
                return false;
            value = (value << 4) | d;
        }

        return true;
    }

    /// <summary>Parses a signed decimal integer. Returns 0 for empty or malformed input.</summary>
    public static long Long(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty)
            return 0;
        var neg = s[0] == (byte)'-';
        var i = neg ? 1 : 0;
        long value = 0;
        for (; i < s.Length; i++)
        {
            var d = s[i] - (byte)'0';
            if ((uint)d > 9)
                return 0;
            value = (value * 10) + d;
        }

        return neg ? -value : value;
    }

    public static int Int(ReadOnlySpan<byte> s)
    {
        var v = Long(s);
        return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
    }

    /// <summary>
    /// Parses a decimal float such as "-123.4567". Falls back to the invariant-culture parser for
    /// exponent forms. Returns NaN for empty or malformed input.
    /// </summary>
    public static float Float(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty)
            return float.NaN;
        var neg = s[0] == (byte)'-';
        var i = neg ? 1 : 0;
        double value = 0;
        var digits = 0;
        for (; i < s.Length; i++)
        {
            var d = s[i] - (byte)'0';
            if ((uint)d > 9)
                break;
            value = (value * 10) + d;
            digits++;
        }

        if (i < s.Length && s[i] == (byte)'.')
        {
            i++;
            var scale = 0.1;
            for (; i < s.Length; i++)
            {
                var d = s[i] - (byte)'0';
                if ((uint)d > 9)
                    break;
                value += d * scale;
                scale *= 0.1;
                digits++;
            }
        }

        if (i != s.Length || digits == 0)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                       ? parsed
                       : float.NaN;
        }

        return (float)(neg ? -value : value);
    }

    /// <summary>
    /// Parses "yyyy-MM-ddTHH:mm:ss.fffffff±hh:mm" into UTC ticks. Returns 0 if malformed.
    /// </summary>
    public static long TimestampUtcTicks(ReadOnlySpan<byte> s)
    {
        if (s.Length < 19 || s[4] != (byte)'-' || s[7] != (byte)'-' || s[10] != (byte)'T' || s[13] != (byte)':' ||
            s[16] != (byte)':')
            return Fallback(s);

        var year = (D2(s, 0) * 100) + D2(s, 2);
        var month = D2(s, 5);
        var day = D2(s, 8);
        var hour = D2(s, 11);
        var minute = D2(s, 14);
        var second = D2(s, 17);
        if (year is < 2000 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
            return Fallback(s);

        var p = 19;
        long frac = 0;
        if (p < s.Length && s[p] == (byte)'.')
        {
            p++;
            var n = 0;
            while (p < s.Length && (uint)(s[p] - '0') <= 9)
            {
                if (n < 7)
                {
                    frac = (frac * 10) + (s[p] - '0');
                    n++;
                }

                p++;
            }

            for (; n < 7; n++)
                frac *= 10;
        }

        long offsetTicks = 0;
        if (p < s.Length)
        {
            var sign = s[p];
            if (sign == (byte)'Z')
            {
                offsetTicks = 0;
            }
            else if ((sign == (byte)'+' || sign == (byte)'-') && p + 6 <= s.Length && s[p + 3] == (byte)':')
            {
                var oh = D2(s, p + 1);
                var om = D2(s, p + 4);
                offsetTicks = ((oh * 60L) + om) * TimeSpan.TicksPerMinute;
                if (sign == (byte)'-')
                    offsetTicks = -offsetTicks;
            }
            else
            {
                return Fallback(s);
            }
        }

        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).Ticks + frac;
        return local - offsetTicks;
    }

    private static int D2(ReadOnlySpan<byte> s, int i) => ((s[i] - '0') * 10) + (s[i + 1] - '0');

    private static long Fallback(ReadOnlySpan<byte> s)
    {
        Span<char> chars = stackalloc char[64];
        if (s.Length > chars.Length)
            return 0;
        for (var i = 0; i < s.Length; i++)
            chars[i] = (char)s[i];
        return DateTimeOffset.TryParse(chars[..s.Length], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                   ? dto.UtcTicks
                   : 0;
    }

    /// <summary>Converts a 21/22 rotation field (0..FFFF) to a heading in radians (-π..π).</summary>
    public static float RotationToHeading(uint rot) => (float)((rot / 65535.0 * 2 * Math.PI) - Math.PI);
}

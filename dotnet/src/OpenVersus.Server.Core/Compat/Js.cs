using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Compat;

/// <summary>JavaScript behaviour the TS server's rules depend on, where .NET's nearest equivalent differs.</summary>
public static class Js
{
    /// <summary>
    /// String.prototype.trim: JavaScript's white space and line terminators, which are not quite .NET's
    /// (U+FEFF is one there and not here; U+0085 is one here and not there).
    /// </summary>
    public static string Trim(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start]))
        {
            start++;
        }

        while (end > start && IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>
    /// parseInt(text) with no radix: leading white space, a sign, then decimal digits (or hex after 0x); NaN when there
    /// are none (null reads as NaN, as parseInt(undefined) does).
    /// </summary>
    public static double ParseInt(string? text)
    {
        if (text is null)
        {
            return double.NaN;
        }

        int i = 0;
        while (i < text.Length && IsSpace(text[i]))
        {
            i++;
        }

        double sign = 1;
        if (i < text.Length && text[i] is '+' or '-')
        {
            sign = text[i] == '-' ? -1 : 1;
            i++;
        }

        bool hex = i + 1 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X';
        if (hex)
        {
            i += 2;
        }

        int start = i;
        while (i < text.Length && (hex ? char.IsAsciiHexDigit(text[i]) : char.IsAsciiDigit(text[i])))
        {
            i++;
        }

        if (i == start)
        {
            return double.NaN;
        }

        string digits = text[start..i];
        double value = hex
            ? digits.Aggregate(0.0, (n, d) => n * 16 + Convert.ToInt32(d.ToString(), 16))
            : double.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return sign * value;
    }

    /// <summary>
    /// Number(text) for a string, as a Redis hash field reaches the TS server: surrounding white space ignored, "" is 0,
    /// a decimal literal (with sign, fraction, exponent), Infinity, or 0x / 0o / 0b digits (no sign); anything else NaN.
    /// Null (a missing field, undefined there) is NaN.
    /// </summary>
    public static double Number(string? text)
    {
        if (text is null)
        {
            return double.NaN;
        }

        string s = Trim(text);
        if (s.Length == 0)
        {
            return 0;
        }

        if (s.Length > 2 && s[0] == '0' && char.ToLowerInvariant(s[1]) is 'x' or 'o' or 'b')
        {
            int radix = char.ToLowerInvariant(s[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            double value = 0;
            foreach (char c in s[2..])
            {
                int digit = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) - 'a' + 10 : 99;
                if (digit >= radix)
                {
                    return double.NaN;
                }

                value = value * radix + digit;
            }

            return value;
        }

        string unsigned = s[0] is '+' or '-' ? s[1..] : s;
        if (unsigned == "Infinity")
        {
            return s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(unsigned, @"^(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")
            ? double.Parse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
            : double.NaN;
    }

    /// <summary>The index range Array.prototype.slice(start, end) takes from a list of <paramref name="length"/>.</summary>
    public static (int From, int To) SliceBounds(int length, double start, double end)
    {
        int Relative(double n)
        {
            double integer = double.IsNaN(n) ? 0 : Math.Truncate(n);
            return integer < 0 ? (int)Math.Max(length + integer, 0) : (int)Math.Min(integer, length);
        }

        int from = Relative(start), to = Relative(end);
        return (from, Math.Max(from, to));
    }

    private static bool IsSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\uFEFF' or '\u2028' or '\u2029'
        || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>
    /// JSON.stringify of a JSON tree, byte for byte: only the quote, the backslash, control characters and lone
    /// surrogates are escaped (\b \f \n \r \t, else \u00xx in lowercase hex); everything else, emoji included, is
    /// written as itself. System.Text.Json escapes more, even with its relaxed encoder.
    /// </summary>
    public static string Stringify(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in obj)
                {
                    sb.Append(first ? "" : ",");
                    first = false;
                    Quote(sb, key);
                    sb.Append(':');
                    Write(sb, value);
                }

                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ",");
                    Write(sb, array[i]);
                }

                sb.Append(']');
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                Quote(sb, text);
                break;
            default:
                // Numbers and booleans: the integers the TS server sends read the same in both.
                sb.Append(node.ToJsonString());
                break;
        }
    }

    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    bool lone = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
                        : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
                    if (c < ' ' || lone)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}

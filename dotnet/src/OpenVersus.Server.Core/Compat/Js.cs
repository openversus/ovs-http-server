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

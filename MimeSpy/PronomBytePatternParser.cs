using System.Globalization;

namespace MimeSpy;

// Parses PRONOM's "extended hex" byte-pattern syntax. The DROID file uses plain hex
// pairs and [XX:YY] brackets; the container file adds whitespace between tokens,
// 'quoted ASCII', ['6'-'7'] ranges and (HH|HH) alternation. One parser handles both.
internal static class PronomBytePatternParser
{
    public static PronomBytePattern Parse(string raw)
    {
        var tokens = new List<PronomByteToken>();
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                var close = raw.IndexOf('\'', i + 1);
                for (var j = i + 1; j < close; j++)
                {
                    tokens.Add(new PronomLiteralToken((byte)raw[j]));
                }

                i = close + 1;
            }
            else if (c == '[')
            {
                var close = raw.IndexOf(']', i);
                tokens.Add(ParseBracketToken(raw.Substring(i + 1, close - i - 1)));
                i = close + 1;
            }
            else if (c == '(')
            {
                var close = raw.IndexOf(')', i);
                tokens.Add(ParseParenToken(raw.Substring(i + 1, close - i - 1)));
                i = close + 1;
            }
            else
            {
                // A contiguous run of hex digits: two characters per literal byte,
                // e.g. "10" (one byte) or "5300630065" (five bytes, no separators).
                var j = i;
                while (j < raw.Length && Uri.IsHexDigit(raw[j]))
                {
                    j++;
                }

                foreach (var b in ParseHexRun(raw.Substring(i, j - i)))
                {
                    tokens.Add(new PronomLiteralToken(b));
                }

                i = j;
            }
        }

        return new PronomBytePattern(tokens);
    }

    private static PronomByteToken ParseBracketToken(string inner)
    {
        if (inner.StartsWith("!&", StringComparison.Ordinal))
        {
            return new PronomBitmaskToken(byte.Parse(inner.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Negated: true);
        }

        if (inner.StartsWith("&", StringComparison.Ordinal))
        {
            return new PronomBitmaskToken(byte.Parse(inner.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Negated: false);
        }

        if (inner.StartsWith("!", StringComparison.Ordinal))
        {
            return new PronomNegatedValueToken(ParseHexRun(inner.Substring(1)));
        }

        // A range's bounds are either quoted chars (['6'-'7'], dash-delimited) or
        // plain hex ([30:39], colon-delimited, or occasionally [30-39], dash-delimited).
        if (inner[0] == '\'')
        {
            var lowEnd = inner.IndexOf('\'', 1);
            var low = (byte)inner[1];
            var highStart = inner.IndexOf('\'', lowEnd + 1) + 1;
            var high = (byte)inner[highStart];
            return new PronomRangeToken([low], [high]);
        }

        var delimiter = inner.IndexOf(':');
        if (delimiter < 0)
        {
            delimiter = inner.IndexOf('-');
        }

        return new PronomRangeToken(ParseHexRun(inner.Substring(0, delimiter)), ParseHexRun(inner.Substring(delimiter + 1)));
    }

    // (HH|HH|...) - a short list of fixed-value alternatives, all the same length.
    private static PronomByteToken ParseParenToken(string inner)
    {
        var alternatives = inner.Split('|').Select(ParseHexRun).ToList();
        return new PronomByteSetToken(alternatives);
    }

    private static byte[] ParseHexRun(string raw)
    {
        var bytes = new byte[raw.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(raw.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}

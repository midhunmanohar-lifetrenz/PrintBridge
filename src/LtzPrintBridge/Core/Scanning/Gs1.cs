using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>What a GS1 code (medicine DataMatrix, GS1-128, GS1 QR) says, split into the fields billing cares about.</summary>
public sealed record Gs1Data(
    string? Gtin,
    string? Batch,
    DateOnly? Expiry,
    string? Serial,
    DateOnly? Manufactured,
    IReadOnlyList<KeyValuePair<string, string>> Elements)
{
    public bool IsExpired(DateOnly today) => Expiry is { } e && e < today;

    /// <summary>The code as digits only, without brackets, with a GS separator after each variable-length field except the last.</summary>
    public string Compact()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Elements.Count; i++)
        {
            var (ai, v) = (Elements[i].Key, Elements[i].Value);
            sb.Append(ai).Append(v);
            if (i < Elements.Count - 1 && Gs1.FixedLength(ai) == null) sb.Append(Gs1.GroupSeparator);
        }
        return sb.ToString();
    }

    /// <summary>The code as printed under the symbol: (01)…(17)…(10)….</summary>
    public string Bracketed() => string.Concat(Elements.Select(e => $"({e.Key}){e.Value}"));
}

public static class Gs1
{
    public const char GroupSeparator = '\u001d';

    static readonly Regex Hri = new(@"\((\d{2,4})\)([^(]*)", RegexOptions.Compiled);

    /// <summary>
    /// Reads "(01)08901234567890(17)271231(10)AB12C" or the raw form "010890123456789017271231" + "10AB12C&lt;GS&gt;21…",
    /// with or without a symbology prefix such as "]d2". Returns null when the text is not GS1.
    /// </summary>
    public static Gs1Data? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var elements = s.StartsWith('(') ? ParseBracketed(s) : ParseRaw(s);
        if (elements == null || elements.Count == 0) return null;

        string? Get(string ai) => elements.FirstOrDefault(e => e.Key == ai).Value;
        return new Gs1Data(
            Get("01") ?? Get("02"),
            Get("10"),
            Date(Get("17")),
            Get("21"),
            Date(Get("11")),
            elements);
    }

    static List<KeyValuePair<string, string>>? ParseBracketed(string s)
    {
        var list = new List<KeyValuePair<string, string>>();
        int consumed = 0;
        foreach (Match m in Hri.Matches(s))
        {
            if (m.Index != consumed) return null;
            list.Add(new(m.Groups[1].Value, m.Groups[2].Value));
            consumed = m.Index + m.Length;
        }
        return consumed == s.Length && Valid(list) ? list : null;
    }

    static List<KeyValuePair<string, string>>? ParseRaw(string s)
    {
        // Symbology prefixes that mark GS1 content: DataMatrix ]d2, QR ]Q3, Code 128 ]C1, DataBar ]e0.
        foreach (var p in new[] { "]d2", "]Q3", "]C1", "]e0" })
            if (s.StartsWith(p, StringComparison.Ordinal)) { s = s[p.Length..]; break; }
        s = s.TrimStart(GroupSeparator);
        if (s.Length < 4 || !char.IsAsciiDigit(s[0]) || !char.IsAsciiDigit(s[1])) return null;

        var list = new List<KeyValuePair<string, string>>();
        int i = 0;
        while (i < s.Length)
        {
            int aiLen = AiLength(s, i);
            if (aiLen == 0 || i + aiLen > s.Length) return null;
            var ai = s.Substring(i, aiLen);
            i += aiLen;
            string value;
            if (FixedLength(ai) is { } n)
            {
                if (i + n > s.Length) return null;
                value = s.Substring(i, n);
                i += n;
            }
            else
            {
                int end = s.IndexOf(GroupSeparator, i);
                if (end < 0) end = s.Length;
                value = s[i..end];
                i = end;
            }
            if (i < s.Length && s[i] == GroupSeparator) i++;
            list.Add(new(ai, value));
        }
        return Valid(list) ? list : null;
    }

    /// <summary>Length of the application identifier starting at <paramref name="i"/>, or 0 when there is none.</summary>
    static int AiLength(string s, int i)
    {
        if (i + 2 > s.Length || !char.IsAsciiDigit(s[i]) || !char.IsAsciiDigit(s[i + 1])) return 0;
        int two = (s[i] - '0') * 10 + (s[i + 1] - '0');
        return two switch
        {
            <= 2 or (>= 10 and <= 22) or 30 or 37 or (>= 90 and <= 99) => 2,
            (>= 23 and <= 25) or (>= 40 and <= 42) => 3,
            (>= 31 and <= 36) or 39 or 70 or 71 or 80 or 81 => 4,
            _ => 0,
        };
    }

    /// <summary>Fixed data length for an identifier, or null when the field runs to a GS separator or the end.</summary>
    public static int? FixedLength(string ai) => ai switch
    {
        "00" => 18,
        "01" or "02" or "03" => 14,
        "11" or "12" or "13" or "15" or "16" or "17" => 6,
        "20" => 2,
        _ when ai.Length == 4 && ai[0] == '3' && ai[1] is >= '1' and <= '6' => 6,
        _ when ai.Length == 3 && ai.StartsWith("41") && ai[2] is >= '0' and <= '7' => 13,
        "7003" => 10,
        _ => null,
    };

    /// <summary>A GS1 element string must carry a product code or at least one known field, with digits where digits are required.</summary>
    static bool Valid(List<KeyValuePair<string, string>> list)
    {
        if (list.Count == 0) return false;
        foreach (var (ai, v) in list)
        {
            if (FixedLength(ai) is { } n && (v.Length != n || !v.All(char.IsAsciiDigit))) return false;
            if (FixedLength(ai) == null && (v.Length == 0 || v.Length > 90)) return false;
        }
        return list.Any(e => e.Key is "01" or "02" or "10" or "17" or "21" or "00");
    }

    /// <summary>GS1 dates are YYMMDD; a day of 00 means the last day of that month.</summary>
    static DateOnly? Date(string? yymmdd)
    {
        if (yymmdd is not { Length: 6 } || !int.TryParse(yymmdd, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        int yy = int.Parse(yymmdd[..2], CultureInfo.InvariantCulture), mm = int.Parse(yymmdd[2..4], CultureInfo.InvariantCulture), dd = int.Parse(yymmdd[4..], CultureInfo.InvariantCulture);
        if (mm is < 1 or > 12) return null;
        int year = 2000 + yy;
        if (dd == 0) dd = DateTime.DaysInMonth(year, mm);
        return dd <= DateTime.DaysInMonth(year, mm) ? new DateOnly(year, mm, dd) : null;
    }
}

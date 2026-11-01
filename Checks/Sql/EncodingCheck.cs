using System.Text;

namespace ReleaseChecker.Checks.Sql;

/// <summary>"fileEncoding" section inside "sql" in rules.json.</summary>
public sealed class FileEncodingRules
{
    /// <summary>Scripts must be valid UTF-8 (plain ASCII counts as UTF-8).</summary>
    public bool RequireUtf8 { get; set; } = true;

    /// <summary>"Any" (default), "Required" (must start with the UTF-8 BOM) or "Forbidden" (must not).</summary>
    public string Utf8Bom { get; set; } = "Any";
}

/// <summary>Detects a script's encoding from its bytes, without trusting the file extension or the editor.</summary>
internal static class EncodingCheck
{
    internal sealed record Result(
        bool IsUtf8,
        bool HasUtf8Bom,
        string Description,     // e.g. "UTF-16 LE", "not valid UTF-8 - probably ANSI / Windows-1252"
        int? InvalidLine,       // first line with a non-UTF-8 byte
        Encoding ReadAs);       // how to decode the file so it can still be parsed

    public static Result Inspect(byte[] b)
    {
        // BOMs: UTF-32 LE starts with the UTF-16 LE BOM, so check it first
        if (Starts(b, 0xFF, 0xFE, 0x00, 0x00)) return new(false, false, "UTF-32 LE", null, Encoding.UTF32);
        if (Starts(b, 0x00, 0x00, 0xFE, 0xFF)) return new(false, false, "UTF-32 BE", null, new UTF32Encoding(bigEndian: true, byteOrderMark: true));
        if (Starts(b, 0xFF, 0xFE)) return new(false, false, "UTF-16 LE (Unicode)", null, Encoding.Unicode);
        if (Starts(b, 0xFE, 0xFF)) return new(false, false, "UTF-16 BE", null, Encoding.BigEndianUnicode);

        bool bom = Starts(b, 0xEF, 0xBB, 0xBF);
        int bad = FirstInvalidUtf8(b, bom ? 3 : 0);
        if (bad < 0)
            return new(true, bom, bom ? "UTF-8 with BOM" : "UTF-8", null, Encoding.UTF8);

        return new(false, bom, "not valid UTF-8 - probably ANSI / Windows-1252", LineOf(b, bad), Encoding.Latin1);
    }

    private static bool Starts(byte[] b, params byte[] prefix) =>
        b.Length >= prefix.Length && prefix.Select((p, i) => b[i] == p).All(x => x);

    /// <summary>Byte offset of the first invalid UTF-8 sequence, or -1 when the whole file is valid.</summary>
    private static int FirstInvalidUtf8(byte[] b, int start)
    {
        int i = start;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }

            int n, min;
            if ((c & 0xE0) == 0xC0) { n = 1; min = 0x80; }
            else if ((c & 0xF0) == 0xE0) { n = 2; min = 0x800; }
            else if ((c & 0xF8) == 0xF0) { n = 3; min = 0x10000; }
            else return i;

            if (i + n >= b.Length) return i;

            int cp = c & (0x3F >> n);
            for (int k = 1; k <= n; k++)
            {
                if ((b[i + k] & 0xC0) != 0x80) return i;
                cp = (cp << 6) | (b[i + k] & 0x3F);
            }

            // overlong forms, surrogates and out-of-range code points are not valid UTF-8
            if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return i;
            i += n + 1;
        }
        return -1;
    }

    private static int LineOf(byte[] b, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset; i++)
            if (b[i] == (byte)'\n') line++;
        return line;
    }
}

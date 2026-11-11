using System.Text;

namespace DbDataKit.Tools.XmlaForwarder;

/// <summary>
/// Finds the readable text in a recorded stream, the way <c>strings</c> does: runs of printable characters, as UTF-8 (ASCII) and as UTF-16 little endian,
/// which is what XML in an Analysis Services message looks like when it is not compressed. A message the protocol compressed shows no runs worth reading;
/// that is a finding about the traffic, and the raw <c>.bin</c> file still has it all.
/// </summary>
public static class StreamText
{
    /// <summary>The shortest run listed (in characters).</summary>
    public const int MinimumRun = 8;

    /// <summary>One line per run: <c>@offset utf8|utf16 text</c>, in the order the runs start; line breaks inside a run are written as <c>\n</c>.</summary>
    public static string Describe(byte[] bytes)
    {
        var runs = new List<(int Offset, string Kind, string Text)>();
        Utf8Runs(bytes, runs);
        Utf16Runs(bytes, runs);
        var text = new StringBuilder();
        text.Append("# ").Append(bytes.Length).Append(" bytes; readable text of at least ").Append(MinimumRun).Append(" characters, as UTF-8 and UTF-16LE\n");
        foreach (var (offset, kind, run) in runs.OrderBy(r => r.Offset).ThenBy(r => r.Kind, StringComparer.Ordinal))
            text.Append('@').Append(offset).Append(' ').Append(kind).Append(' ').Append(run.Replace("\r", "").Replace("\n", "\\n")).Append('\n');
        if (runs.Count == 0) text.Append("# none: the stream may be compressed or binary\n");
        return text.ToString();
    }

    /// <summary>The text of the runs only, in order (for a test or a search).</summary>
    public static IReadOnlyList<string> Runs(byte[] bytes)
    {
        var runs = new List<(int, string, string)>();
        Utf8Runs(bytes, runs);
        Utf16Runs(bytes, runs);
        return runs.OrderBy(r => r.Item1).Select(r => r.Item3).ToList();
    }

    private static bool Printable(int c) => c is '\t' or '\n' or '\r' || (c >= 0x20 && c < 0x7F);

    private static void Utf8Runs(byte[] b, List<(int, string, string)> runs)
    {
        int start = -1;
        for (var i = 0; i <= b.Length; i++)
        {
            if (i < b.Length && Printable(b[i])) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= MinimumRun) runs.Add((start, "utf8", Encoding.ASCII.GetString(b, start, i - start)));
            start = -1;
        }
    }

    private static void Utf16Runs(byte[] b, List<(int, string, string)> runs)
    {
        // a UTF-16LE character of the ASCII range is the byte then a zero; try both alignments
        for (var alignment = 0; alignment < 2; alignment++)
        {
            int start = -1, count = 0;
            var sb = new StringBuilder();
            for (var i = alignment; i + 1 < b.Length; i += 2)
            {
                if (Printable(b[i]) && b[i + 1] == 0) { if (start < 0) start = i; sb.Append((char)b[i]); count++; continue; }
                if (start >= 0 && count >= MinimumRun) runs.Add((start, "utf16", sb.ToString()));
                start = -1; count = 0; sb.Clear();
            }
            if (start >= 0 && count >= MinimumRun) runs.Add((start, "utf16", sb.ToString()));
        }
    }
}

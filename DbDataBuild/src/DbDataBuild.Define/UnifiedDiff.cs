using DbDataBuild.Core;
using System.Text;

namespace DbDataBuild.Define;

/// <summary>A unified diff of two texts (three lines of context), shown before anything is written. Empty when the texts are equal.</summary>
public static class UnifiedDiff
{
    private const int Context = 3;
    private const char NoNewline = '\u0001';

    public static string Create(string? oldText, string newText, string path)
    {
        if (oldText == newText) return "";
        var (a, _) = Lines(oldText ?? "");
        var (b, _) = Lines(newText);
        var ops = Edits(a, b);

        var sb = new StringBuilder();
        sb.Append("--- ").AppendLineLf(oldText == null ? "/dev/null" : "a/" + path);
        sb.Append("+++ b/").AppendLineLf(path);

        // group changes into hunks with context
        var changeIdx = Enumerable.Range(0, ops.Count).Where(i => ops[i].Kind != ' ').ToList();
        var i0 = 0;
        while (i0 < changeIdx.Count)
        {
            var first = changeIdx[i0];
            var last = first;
            var j = i0 + 1;
            while (j < changeIdx.Count && changeIdx[j] - last <= 2 * Context + 1) { last = changeIdx[j]; j++; }
            var from = Math.Max(0, first - Context);
            var to = Math.Min(ops.Count - 1, last + Context);

            var oldStart = ops.Take(from).Count(o => o.Kind != '+');
            var newStart = ops.Take(from).Count(o => o.Kind != '-');
            var slice = ops.Skip(from).Take(to - from + 1).ToList();
            var oldCount = slice.Count(o => o.Kind != '+');
            var newCount = slice.Count(o => o.Kind != '-');
            sb.AppendLineLf($"@@ -{Range(oldStart, oldCount)} +{Range(newStart, newCount)} @@");
            foreach (var op in slice)
            {
                var noNl = op.Text.EndsWith(NoNewline);
                sb.Append(op.Kind).AppendLineLf(noNl ? op.Text[..^1] : op.Text);
                if (noNl) sb.AppendLineLf("\\ No newline at end of file");
            }
            i0 = j;
        }
        return sb.ToString();
    }

    private static string Range(int start, int count) => count == 0 ? $"{start},0" : count == 1 ? $"{start + 1}" : $"{start + 1},{count}";

    private static (List<string> Lines, bool NoFinalNewline) Lines(string text)
    {
        if (text.Length == 0) return ([], false);
        var normalized = text.Replace("\r\n", "\n");
        var noNl = !normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (!noNl) lines.RemoveAt(lines.Count - 1);
        else lines[^1] += NoNewline;   // makes "b\n" and "b" different last lines, as diff treats them
        return (lines, noNl);
    }

    private readonly record struct Op(char Kind, string Text, int OldIndex, int NewIndex);

    private static List<Op> Edits(List<string> a, List<string> b)
    {
        // longest common subsequence on lines; files here are small definitions
        var n = a.Count; var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(new(' ', a[x], x, y)); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(new('-', a[x], x, -1)); x++; }
            else { ops.Add(new('+', b[y], -1, y)); y++; }
        }
        while (x < n) { ops.Add(new('-', a[x], x, -1)); x++; }
        while (y < m) { ops.Add(new('+', b[y], -1, y)); y++; }
        return ops;
    }
}

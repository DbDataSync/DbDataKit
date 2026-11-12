using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Views.Export;

/// <summary>
/// What a PDF says about how it was made (plan 32 DBR16): the browser's build in <c>Keywords</c> and the fonts it embeds, and <c>Creator</c> naming the product. The browser writes the document information (title, <c>Producer</c> with
/// its Skia milestone) and a classic cross-reference table; this adds an <b>incremental update</b> after it (a new copy of the information object, a section of the table that points at it, a trailer that names the old one in <c>/Prev</c>),
/// so nothing the browser wrote is rewritten and any reader sees the new values. A document the stamp cannot read (a cross-reference stream, an unexpected trailer) is returned as it came.
/// </summary>
public static partial class PdfMetadata
{
    public const string Creator = "DbDataKit Views";

    [GeneratedRegex(@"startxref\s+(\d+)\s+%%EOF\s*$")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"trailer\s*<<(?<body>.*?)>>\s*startxref", RegexOptions.Singleline)]
    private static partial Regex Trailer();

    /// <summary>The PDF with its information object carrying <paramref name="creator"/> and <paramref name="keywords"/> (the other entries, the title and the producer included, kept as they were).</summary>
    public static byte[] Stamp(byte[] pdf, string keywords, string creator = Creator)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var tail = text.Length > 2048 ? text[^2048..] : text;
        if (StartXref().Match(tail) is not { Success: true } start) return pdf;
        var trailer = Trailer().Match(tail);
        if (!trailer.Success) return pdf;
        var body = trailer.Groups["body"].Value;
        if (Regex.Match(body, @"/Size\s+(\d+)") is not { Success: true } size || Regex.Match(body, @"/Info\s+(\d+)\s+(\d+)\s+R") is not { Success: true } info) return pdf;
        if (body.Contains("/Encrypt", StringComparison.Ordinal)) return pdf;
        var number = int.Parse(info.Groups[1].Value, CultureInfo.InvariantCulture);
        var generation = info.Groups[2].Value;
        // the old information object: its entries are kept, but Creator and Keywords are ours
        var old = Regex.Match(text, $@"(?<![0-9]){number}\s+{generation}\s+obj\s*<<(?<d>.*?)>>\s*endobj", RegexOptions.Singleline);
        if (!old.Success) return pdf;
        var kept = Regex.Replace(old.Groups["d"].Value, @"/(Creator|Keywords)\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>)", "");
        var entry = $"{number} {generation} obj\n<<{kept.TrimEnd()}\n/Creator {Literal(creator)}\n/Keywords {Literal(keywords)}>>\nendobj\n";
        var offset = pdf.Length;
        var prefix = pdf.Length > 0 && pdf[^1] != (byte)'\n' ? "\n" : "";
        offset += prefix.Length;
        var sb = new StringBuilder(prefix).Append(entry);
        var xrefAt = offset + Encoding.Latin1.GetByteCount(entry);
        sb.Append("xref\n").Append(number.ToString(CultureInfo.InvariantCulture)).Append(" 1\n")
          .Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ').Append(int.Parse(generation, CultureInfo.InvariantCulture).ToString("D5", CultureInfo.InvariantCulture)).Append(" n \n");
        var root = Regex.Match(body, @"/Root\s+\d+\s+\d+\s+R").Value;
        if (root.Length == 0) return pdf;
        sb.Append("trailer\n<</Size ").Append(size.Groups[1].Value).Append(' ').Append(root).Append(" /Info ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(generation)
          .Append(" R /Prev ").Append(start.Groups[1].Value).Append(">>\nstartxref\n").Append(xrefAt.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        var added = Encoding.Latin1.GetBytes(sb.ToString());
        var stamped = new byte[pdf.Length + added.Length];
        pdf.CopyTo(stamped, 0);
        added.CopyTo(stamped, pdf.Length);
        return stamped;
    }

    /// <summary>A PDF string: a literal of printable ASCII, else UTF-16 with its byte order mark in hexadecimal.</summary>
    private static string Literal(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
            return "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
    }
}

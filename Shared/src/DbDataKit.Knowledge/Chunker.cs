using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Knowledge;

/// <summary>
/// One piece of an item as it is embedded and found (plan 27a KN04, plan 27 section 2.8). <see cref="Text"/> is what is stored and shown; <see cref="EmbeddedText"/> is what is embedded: the same text prefixed with the item's title and heading path.
/// </summary>
/// <param name="ItemId">The item the chunk belongs to.</param>
/// <param name="Title">The item's title.</param>
/// <param name="Text">What is stored and shown.</param>
/// <param name="EmbeddedText">What is embedded: the title and heading path, then the text.</param>
/// <param name="Id">From the item and the heading path (<c>howto.md#choosing-the-range</c>); a section split into pieces has <c>~2</c>, <c>~3</c> after the first.</param>
/// <param name="HeadingPath">The headings above the chunk, outermost first, not including the title.</param>
/// <param name="Hash">sha256 of <see cref="EmbeddedText"/>: a chunk is embedded again only when this changes.</param>
/// <param name="StartLine">The line of the item's file where the chunk starts, for citing.</param>
public sealed record Chunk(string Id, string ItemId, string Title, IReadOnlyList<string> HeadingPath, string Text, string EmbeddedText, string Hash, int StartLine);

/// <summary>How big a chunk may be: a share of the embedding model's largest input, at most <see cref="MaximumTokens"/> (plan 27 section 2.8 point 2).</summary>
/// <param name="TargetTokens">The size a chunk aims at.</param>
/// <param name="CountTokens">Counts the tokens of a text; the default is the usual four characters to a token.</param>
public sealed record ChunkerOptions(int TargetTokens = ChunkerOptions.MaximumTokens, Func<string, int>? CountTokens = null)
{
    /// <summary>The most a chunk aims at, whatever the model can take: smaller chunks match questions more precisely.</summary>
    public const int MaximumTokens = 500;

    /// <summary>About four characters of English to a token.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    public int Tokens(string text) => (CountTokens ?? EstimateTokens)(text);

    /// <summary>Chunks that aim at half of the model's largest input and at most <see cref="MaximumTokens"/>.</summary>
    public static ChunkerOptions ForModel(int largestInputTokens, Func<string, int>? countTokens = null) =>
        new(Math.Clamp(largestInputTokens / 2, 16, MaximumTokens), countTokens);
}

/// <summary>
/// Splits an item into chunks (plan 27a KN04): structure first (Markdown headings; a section that is still too long splits at paragraphs, then sentences; a table or a code block is never cut), size from the embedding model, one sentence of overlap
/// between the pieces of one section, the title and heading path prefixed before embedding. Instructions and guardrails are never split, nor is a short reference (the item is the chunk), nor an item with <c>chunking: none</c>.
/// </summary>
public static class Chunker
{
    private static readonly Regex HeadingLine = new(@"^ {0,3}(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^ {0,3}(```|~~~)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum BlockKind { Heading, Paragraph, Table, Code }

    private sealed record Block(BlockKind Kind, string Text, int Line, int Level = 0, string HeadingText = "");

    private sealed class Section(IReadOnlyList<string> path, int line)
    {
        public IReadOnlyList<string> Path { get; } = path;

        public int Line { get; } = line;

        public List<Block> Blocks { get; } = [];
    }

    public static IReadOnlyList<Chunk> Split(KnowledgeItem item, ChunkerOptions? options = null)
    {
        options ??= new ChunkerOptions();
        var raw = item.Body.Replace("\r\n", "\n");
        var firstLine = item.BodyLine + (raw.Length - raw.TrimStart('\n').Length);   // leading blank lines are not text, but they are lines
        var body = raw.Trim('\n');
        if (body.Trim().Length == 0) return [];
        if (item.Role != KnowledgeRole.Reference || item.Chunking.Mode == ChunkingMode.None || options.Tokens(body) <= options.TargetTokens)
            return [Make(item, [], body, firstLine, 1, new HashSet<string>())];

        var splitLevel = item.Chunking.Mode == ChunkingMode.HeadingLevel ? item.Chunking.HeadingLevel : 6;
        var sections = Sections(Blocks(body, firstLine), splitLevel);
        var chunks = new List<Chunk>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var pieces = Pieces(section, options);
            for (var i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Text.Trim().Length == 0) continue;
                chunks.Add(Make(item, section.Path, pieces[i].Text, pieces[i].Line, i + 1, used));
            }
        }
        return chunks;
    }

    private static Chunk Make(KnowledgeItem item, IReadOnlyList<string> path, string text, int line, int piece, HashSet<string> used)
    {
        var slug = Slug(path.Count == 0 ? "" : string.Join(" ", path));
        var id = $"{item.Id}#{slug}" + (piece > 1 ? $"~{piece}" : "");
        for (var n = 2; !used.Add(id); n++) id = $"{item.Id}#{slug}-{n}" + (piece > 1 ? $"~{piece}" : "");   // two sections with one heading
        var prefix = string.Join(" > ", path.Count > 0 && string.Equals(path[0], item.Title, StringComparison.Ordinal) ? path : [item.Title, .. path]);
        var embedded = prefix + "\n\n" + text;
        return new Chunk(id, item.Id, item.Title, path, text, embedded, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embedded))).ToLowerInvariant(), line);
    }

    private static string Slug(string heading)
    {
        var sb = new StringBuilder();
        foreach (var c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    // the body as blocks: headings, paragraphs, tables and fenced code (kept whole, blank lines inside and all)
    private static List<Block> Blocks(string body, int firstLine)
    {
        var blocks = new List<Block>();
        var current = new List<string>();
        var kind = BlockKind.Paragraph;
        var start = firstLine;
        string? fence = null;
        var line = firstLine;

        void Flush()
        {
            if (current.Count > 0) blocks.Add(new Block(kind, string.Join('\n', current), start));
            current.Clear();
        }

        foreach (var text in body.Split('\n'))
        {
            if (fence is not null)
            {
                current.Add(text);
                if (text.TrimStart().StartsWith(fence, StringComparison.Ordinal) && current.Count > 1) { Flush(); fence = null; }
            }
            else if (Fence.Match(text) is { Success: true } f)
            {
                Flush();
                fence = f.Groups[1].Value;
                kind = BlockKind.Code;
                start = line;
                current.Add(text);
            }
            else if (HeadingLine.Match(text) is { Success: true } h)
            {
                Flush();
                blocks.Add(new Block(BlockKind.Heading, text.Trim(), line, h.Groups[1].Length, h.Groups[2].Value.Trim()));
            }
            else if (text.Trim().Length == 0) Flush();
            else
            {
                var isTable = text.TrimStart().StartsWith('|');
                var wanted = isTable ? BlockKind.Table : BlockKind.Paragraph;
                if (current.Count > 0 && kind != wanted) Flush();
                if (current.Count == 0) { kind = wanted; start = line; }
                current.Add(text);
            }
            line++;
        }
        Flush();
        return blocks;
    }

    // headings of the split level or above start a section; a deeper heading is part of the text above it
    private static List<Section> Sections(List<Block> blocks, int splitLevel)
    {
        var sections = new List<Section>();
        var stack = new List<(int Level, string Text)>();
        var current = new Section([], blocks.Count > 0 ? blocks[0].Line : 1);
        sections.Add(current);
        foreach (var block in blocks)
        {
            if (block.Kind == BlockKind.Heading)
            {
                while (stack.Count > 0 && stack[^1].Level >= block.Level) stack.RemoveAt(stack.Count - 1);
                stack.Add((block.Level, block.HeadingText));
                if (block.Level <= splitLevel)
                {
                    current = new Section([.. stack.Select(s => s.Text)], block.Line);
                    sections.Add(current);
                }
            }
            current.Blocks.Add(block);
        }
        // a first section with no text (the file starts with a heading) is not a chunk; a section that is only its heading has no text to find
        return [.. sections.Where(s => s.Blocks.Any(b => b.Kind != BlockKind.Heading))];
    }

    private sealed record Piece(string Text, int Line);

    // a section as pieces of about the target size: paragraphs are packed, a long paragraph splits at sentences, a table or code block stays whole
    private static List<Piece> Pieces(Section section, ChunkerOptions options)
    {
        var units = new List<Block>();
        foreach (var block in section.Blocks)
        {
            if (block.Kind == BlockKind.Paragraph && options.Tokens(block.Text) > options.TargetTokens)
                units.AddRange(SplitSentences(block, options));
            else units.Add(block);
        }

        var pieces = new List<Piece>();
        var current = new List<Block>();
        var tokens = 0;
        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Join("\n\n", current.Select(b => b.Text));
            if (pieces.Count > 0 && Overlap(pieces[^1].Text) is { } carried && current[0].Kind == BlockKind.Paragraph) text = carried + "\n\n" + text;
            pieces.Add(new Piece(text, current[0].Line));
            current.Clear();
            tokens = 0;
        }
        foreach (var unit in units)
        {
            var t = options.Tokens(unit.Text);
            if (unit.Kind == BlockKind.Heading && current.Any(b => b.Kind != BlockKind.Heading)) Flush();   // a heading starts a piece, it is never left at the end of one
            if (current.Any(b => b.Kind != BlockKind.Heading) && tokens + t > options.TargetTokens) Flush();   // a heading is never left alone at the end of a piece
            current.Add(unit);
            tokens += t;
        }
        Flush();
        return pieces;
    }

    // a paragraph too long for one piece, as groups of whole sentences
    private static IEnumerable<Block> SplitSentences(Block paragraph, ChunkerOptions options)
    {
        var sentences = SentenceEnd.Split(paragraph.Text.Replace('\n', ' ')).Where(s => s.Length > 0).ToList();
        var group = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (group.Length > 0 && options.Tokens(group + " " + sentence) > options.TargetTokens)
            {
                yield return new Block(BlockKind.Paragraph, group.ToString(), paragraph.Line);
                group.Clear();
            }
            if (group.Length > 0) group.Append(' ');
            group.Append(sentence);
        }
        if (group.Length > 0) yield return new Block(BlockKind.Paragraph, group.ToString(), paragraph.Line);
    }

    // the last sentence of the previous piece, when it ends in prose, so a sentence cut at the boundary is found from both sides
    private static string? Overlap(string previous)
    {
        var lastBlock = previous.Split("\n\n")[^1];
        if (lastBlock.TrimStart().StartsWith('|') || lastBlock.TrimStart().StartsWith("```", StringComparison.Ordinal) || lastBlock.TrimStart().StartsWith("~~~", StringComparison.Ordinal) || HeadingLine.IsMatch(lastBlock)) return null;
        var sentences = SentenceEnd.Split(lastBlock.Replace('\n', ' ')).Where(s => s.Length > 0).ToList();
        return sentences.Count > 1 ? sentences[^1] : null;   // one sentence alone was the whole block: nothing is cut
    }

    /// <summary>The chunks that need embedding when an item changed: those whose hash is not already known (the hashes of the chunks the index holds for the item).</summary>
    public static IReadOnlyList<Chunk> Changed(IEnumerable<Chunk> chunks, IReadOnlySet<string> knownHashes) => [.. chunks.Where(c => !knownHashes.Contains(c.Hash))];

}

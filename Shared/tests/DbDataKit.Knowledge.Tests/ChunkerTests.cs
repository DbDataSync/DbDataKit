using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN04: chunking by structure first, then size.</summary>
public sealed class ChunkerTests
{
    private static KnowledgeItem Item(string body, string role = "reference", string extra = "", string title = "Backfilling an incremental model")
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {title}\nrole: {role}\nowner: data\n{extra}---\n{body}", "howto.md", "howto.md");
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static string Filler(string word, int sentences) => string.Join(' ', Enumerable.Range(0, sentences).Select(i => $"This is sentence {i} about {word} and what to do with it."));

    private static readonly string Howto = $"""
        # Backfilling an incremental model

        Intro text. {Filler("backfills", 3)}

        ## Choosing the range

        {Filler("the range", 12)}

        ## Running it

        {Filler("running", 12)}

        | step | what |
        | --- | --- |
        | 1 | stop the schedule |
        | 2 | run the range |

        ```sql
        select 1;

        select 2;
        ```

        ## Checking the result

        {Filler("checking", 12)}
        """;

    private static readonly ChunkerOptions Small = new(TargetTokens: 100);

    [Fact]
    public void A_long_how_to_splits_at_headings_with_the_path_of_each()
    {
        var chunks = Chunker.Split(Item(Howto), Small);

        Assert.Equal(["Backfilling an incremental model", "Backfilling an incremental model > Choosing the range"], chunks.Take(2).Select(c => string.Join(" > ", c.HeadingPath)));
        Assert.Contains(chunks, c => c.HeadingPath.SequenceEqual(["Backfilling an incremental model", "Running it"]));
        Assert.Contains(chunks, c => c.HeadingPath.SequenceEqual(["Backfilling an incremental model", "Checking the result"]));
        Assert.All(chunks, c => Assert.Equal("howto.md", c.ItemId));
        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.Contains("howto.md#backfilling-an-incremental-model-choosing-the-range", chunks.Select(c => c.Id));
    }

    [Fact]
    public void A_table_and_a_code_block_are_never_cut_even_over_the_size()
    {
        var chunks = Chunker.Split(Item(Howto), new ChunkerOptions(TargetTokens: 20));

        var table = Assert.Single(chunks, c => c.Text.Contains("| 1 | stop the schedule |"));
        Assert.Contains("| 2 | run the range |", table.Text);
        var code = Assert.Single(chunks, c => c.Text.Contains("select 1;"));
        Assert.Contains("select 2;", code.Text);       // the blank line inside the fence did not end the block
    }

    [Fact]
    public void A_paragraph_too_long_splits_at_sentences_with_one_sentence_of_overlap()
    {
        var item = Item("# T\n\n" + Filler("long", 40), title: "T");

        var chunks = Chunker.Split(item, new ChunkerOptions(TargetTokens: 100));

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text));                      // whole sentences only
        var firstLast = chunks[0].Text.Split(". ").Last();
        Assert.Contains(firstLast.TrimEnd('.'), chunks[1].Text);                    // the sentence cut at the boundary is in both
        Assert.Contains(chunks, c => c.Id.EndsWith("~2"));
        Assert.Contains(chunks, c => c.Id.EndsWith("~3"));
    }

    [Fact]
    public void Editing_one_section_changes_one_chunk_hash_and_no_other()
    {
        var before = Chunker.Split(Item(Howto), Small);
        var after = Chunker.Split(Item(Howto.Replace("sentence 3 about the range", "sentence 3 about the RANGE")), Small);

        Assert.Equal(before.Select(c => c.Id), after.Select(c => c.Id));
        var changed = before.Zip(after).Where(p => p.First.Hash != p.Second.Hash).ToList();
        Assert.Single(changed);
        Assert.Equal(["Backfilling an incremental model", "Choosing the range"], changed[0].First.HeadingPath);
        Assert.Single(Chunker.Changed(after, before.Select(c => c.Hash).ToHashSet()));
    }

    [Fact]
    public void Instructions_guardrails_and_short_references_are_never_split()
    {
        var instruction = Chunker.Split(Item(Howto, role: "instruction"), Small);
        var guardrail = Chunker.Split(Item(Howto, role: "guardrail"), Small);
        var shortReference = Chunker.Split(Item("# Short\n\nOne screen of text."), Small);

        Assert.Single(instruction);
        Assert.Single(guardrail);
        Assert.Single(shortReference);
        Assert.Contains("## Running it", instruction[0].Text);                      // all of it, whole
    }

    [Fact]
    public void Chunking_none_keeps_a_long_reference_whole_and_a_heading_level_splits_only_there()
    {
        Assert.Single(Chunker.Split(Item(Howto, extra: "chunking: none\n"), Small));

        var level2 = Chunker.Split(Item(Howto + "\n\n### Detail\n\n" + Filler("detail", 12), extra: "chunking: { split: heading-level-2 }\n"), Small);

        Assert.DoesNotContain(level2, c => c.HeadingPath.Contains("Detail"));       // a level-3 heading is part of the text above it
        Assert.Contains(level2, c => c.Text.Contains("### Detail"));
        Assert.Contains(level2, c => c.HeadingPath.SequenceEqual(["Backfilling an incremental model", "Choosing the range"]));
    }

    [Fact]
    public void The_prefix_is_in_the_embedded_text_and_not_in_the_stored_one()
    {
        var chunk = Chunker.Split(Item(Howto), Small).First(c => c.HeadingPath.Contains("Choosing the range"));

        Assert.StartsWith("Backfilling an incremental model > Choosing the range\n\n", chunk.EmbeddedText);
        Assert.DoesNotContain("Backfilling an incremental model >", chunk.Text);
        Assert.EndsWith(chunk.Text, chunk.EmbeddedText);
    }

    [Fact]
    public void The_prefix_names_the_title_when_the_text_has_no_heading_of_that_name()
    {
        var chunks = Chunker.Split(Item("Intro.\n\n## Part one\n\n" + Filler("one", 12) + "\n\n## Part two\n\n" + Filler("two", 12), title: "The title"), Small);

        Assert.StartsWith("The title\n\n", chunks[0].EmbeddedText);                  // text before any heading
        Assert.StartsWith("The title > Part one\n\n", chunks[1].EmbeddedText);
    }

    [Fact]
    public void The_size_follows_the_embedding_model_half_of_its_largest_input_and_at_most_500()
    {
        Assert.Equal(500, ChunkerOptions.ForModel(8192).TargetTokens);
        Assert.Equal(128, ChunkerOptions.ForModel(256).TargetTokens);
        var small = Chunker.Split(Item(Howto), ChunkerOptions.ForModel(256));
        var large = Chunker.Split(Item(Howto), ChunkerOptions.ForModel(8192));

        Assert.True(small.Count > large.Count);
    }

    [Fact]
    public void Two_sections_with_one_heading_get_two_ids_and_an_item_with_no_text_has_no_chunk()
    {
        var chunks = Chunker.Split(Item("## Same\n\n" + Filler("a", 12) + "\n\n## Same\n\n" + Filler("b", 12)), Small);

        Assert.Equal(chunks.Count, chunks.Select(c => c.Id).Distinct().Count());
        Assert.Empty(Chunker.Split(Item("\n", extra: "")));
    }

    [Fact]
    public void A_chunk_knows_the_line_it_starts_on()
    {
        var item = Item(Howto);
        var chunks = Chunker.Split(item, Small);

        var running = chunks.First(c => c.HeadingPath.Contains("Running it"));
        var lines = $"---\nknowledge: 1\ntitle: x\nrole: reference\nowner: data\n---\n{Howto}".Split('\n');
        Assert.Equal("## Running it", lines[running.StartLine - 1].Trim());
    }
}

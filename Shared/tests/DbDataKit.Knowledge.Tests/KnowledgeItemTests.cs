using System.Text.Json;
using Json.Schema;
using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN01: the strict reader of <c>knowledge/1</c> and of <c>_knowledge.yml</c>.</summary>
public sealed class KnowledgeItemTests
{
    private const string Complete = """
        ---
        knowledge: 1
        title: Revenue means net revenue
        role: instruction
        about:
          - semantic-model: Sales
            measure: revenue
          - product: [build, sync]
          - build-project: sales-warehouse
            folder: models/marts
          - tag: finance
          - property: tier=1
        for:
          audience: [front-end, back-end]
          agent: sales-helper
        owner: finance-data
        ---
        Revenue in the Sales model means **net revenue**: gross sales minus returns minus discounts.
        """;

    private static KnowledgeItemResult Load(string text, params FolderDefaults[] defaults) => KnowledgeItemLoader.Load(text, "revenue.md", "knowledge/revenue.md", defaults);

    [Fact]
    public void A_complete_item_loads_with_every_field()
    {
        var result = Load(Complete);

        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        var item = result.Item!;
        Assert.Equal("Revenue means net revenue", item.Title);
        Assert.Equal(KnowledgeRole.Instruction, item.Role);
        Assert.Equal("finance-data", item.Owner);
        Assert.Equal(["front-end", "back-end"], item.For.Audience);
        Assert.Equal(["sales-helper"], item.For.Agents);
        Assert.Equal(["semantic-model:Sales/measure:revenue", "product:build,sync", "build-project:sales-warehouse/folder:models/marts", "tag:finance", "property:tier=1"], item.About.Select(s => s.Key));
        Assert.StartsWith("Revenue in the Sales model", item.Body);
        Assert.Equal(18, item.BodyLine);
        Assert.Equal(64, item.Hash.Length);
        Assert.False(item.AboutImplied);
        Assert.Equal(ChunkingSetting.None, item.Chunking);   // an instruction is never split
    }

    [Fact]
    public void The_version_of_an_item_is_the_hash_of_its_text_and_ignores_line_endings()
    {
        Assert.Equal(Load(Complete).Item!.Hash, Load(Complete.Replace("\n", "\r\n")).Item!.Hash);
        Assert.NotEqual(Load(Complete).Item!.Hash, Load(Complete + " ").Item!.Hash);
    }

    [Theory]
    [InlineData("knowledge: 1\nrole: reference\nbogus: x", 4, "bogus")]
    [InlineData("knowledge: 1\nrole: policy", 3, "not a role")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - cube: Sales", 5, "no subject type")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - semantic-model: Sales\n    folder: x", 6, "does not narrow")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - semantic-model: Sales\n    measure: a\n    dimension: b", 7, "not several")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - product: warehouse", 5, "not a product")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - property: tier", 5, "name=value")]
    [InlineData("knowledge: 1\nrole: reference\nabout:\n  - install: false", 5, "install")]
    [InlineData("knowledge: 1\nrole: reference\nfor:\n  audience: [middle]", 5, "not a value")]
    [InlineData("knowledge: 1\nrole: reference\nchunking: whole", 4, "chunking")]
    [InlineData("knowledge: 1\nrole: reference\nkind: dashboard", 4, "kind")]
    public void A_bad_part_is_refused_with_file_line_and_a_suggestion(string frontMatter, int line, string words)
    {
        var result = Load($"---\n{frontMatter}\n---\n# A title\ntext");

        Assert.False(result.Ok);
        Assert.Null(result.Item);
        var error = result.Diagnostics.First(d => d.Severity == ConfigSeverity.Error);
        Assert.Equal("knowledge/revenue.md", error.Path);
        Assert.Equal(line, error.Line);
        Assert.Contains(words, error.Message + " " + error.Fix);
    }

    [Fact]
    public void An_unknown_key_suggests_the_nearest_one()
    {
        var result = Load("---\nknowledge: 1\nrole: reference\nowners: x\n---\n# T");

        var error = Assert.Single(result.Diagnostics);
        Assert.Contains("Did you mean `owner`?", error.Message);
    }

    [Fact]
    public void A_missing_front_matter_missing_format_missing_role_and_missing_title_are_each_refused()
    {
        Assert.Equal(KnowledgeItemLoader.FrontMatter, Load("Just text").Diagnostics.Single().Code);
        Assert.Equal(KnowledgeItemLoader.FrontMatter, Load("---\nknowledge: 1\n").Diagnostics.Single().Code);
        Assert.Equal(KnowledgeItemLoader.FormatNumber, Load("---\nrole: reference\n---\n# T").Diagnostics.Single().Code);
        Assert.Contains("role", Load("---\nknowledge: 1\n---\n# T").Diagnostics.Single().Message);
        Assert.Contains("title", Load("---\nknowledge: 1\nrole: reference\n---\nno heading").Diagnostics.Single().Message);
    }

    [Fact]
    public void A_title_may_come_from_the_first_heading()
    {
        var result = Load("---\nknowledge: 1\nrole: reference\n---\n\n# How we backfill\n\nSet it to the first missing day.");

        Assert.Equal("How we backfill", result.Item!.Title);
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_file_and_both_numbers()
    {
        var error = Load("---\nknowledge: 2\nrole: reference\n---\n# T").Diagnostics.Single();

        Assert.Equal(KnowledgeItemLoader.FormatNumber, error.Code);
        Assert.Contains("knowledge/revenue.md", error.Message);
        Assert.Contains("format 2", error.Message);
        Assert.Contains("format 1", error.Message);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void Folder_defaults_fill_what_the_item_leaves_out_and_the_nearest_wins()
    {
        var root = KnowledgeItemLoader.LoadFolderFile("knowledge: 1\ndefaults:\n  role: instruction\n  owner: administrators\n  about:\n    - install: true\n", "_knowledge.yml").Defaults!;
        var near = KnowledgeItemLoader.LoadFolderFile("knowledge: 1\ndefaults:\n  owner: build-team\n  about:\n    - product: build\n", "build/_knowledge.yml").Defaults!;

        var item = Load("---\nknowledge: 1\ntitle: Naming\n---\ntext", near, root).Item!;

        Assert.Equal(KnowledgeRole.Instruction, item.Role);          // only the root gives it
        Assert.Equal("build-team", item.Owner);                       // the nearer file wins
        Assert.Equal(["product:build"], item.About.Select(s => s.Key));   // a list is replaced, not merged
        Assert.False(item.AboutImplied);
        var own = Load("---\nknowledge: 1\ntitle: Naming\nrole: guardrail\nowner: me\n---\ntext", near, root).Item!;
        Assert.Equal(KnowledgeRole.Guardrail, own.Role);
        Assert.Equal("me", own.Owner);
    }

    [Fact]
    public void A_file_in_the_project_it_is_about_needs_no_about()
    {
        var project = new KnowledgeSubject("build-project", ["sales-warehouse"]);

        var implied = KnowledgeItemLoader.Load("---\nknowledge: 1\nrole: instruction\nowner: data\n---\n# Facts are named fct_", "naming.md", "naming.md", null, project).Item!;
        var explicitAbout = KnowledgeItemLoader.Load("---\nknowledge: 1\nrole: instruction\nowner: data\nabout:\n  - tag: x\n---\n# T", "a.md", "a.md", null, project).Item!;

        Assert.True(implied.AboutImplied);
        Assert.Equal(["build-project:sales-warehouse"], implied.About.Select(s => s.Key));
        Assert.False(explicitAbout.AboutImplied);
        Assert.Equal(["tag:x"], explicitAbout.About.Select(s => s.Key));
    }

    [Fact]
    public void An_instruction_with_no_owner_is_a_warning_and_a_chunking_on_it_is_ignored_with_a_warning()
    {
        var result = Load("---\nknowledge: 1\nrole: guardrail\nchunking: none\n---\n# T");

        Assert.True(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Code == KnowledgeItemLoader.NoOwner && d.Severity == ConfigSeverity.Warning);
        Assert.Contains(result.Diagnostics, d => d.Code == KnowledgeItemLoader.ChunkingIgnored && d.Line == 4);
    }

    [Fact]
    public void Chunking_reads_none_and_a_heading_level_and_a_reference_keeps_it()
    {
        Assert.Equal(ChunkingSetting.None, Load("---\nknowledge: 1\nrole: reference\nchunking: none\n---\n# T").Item!.Chunking);
        Assert.Equal(new ChunkingSetting(ChunkingMode.HeadingLevel, 2), Load("---\nknowledge: 1\nrole: reference\nchunking: { split: heading-level-2 }\n---\n# T").Item!.Chunking);
        Assert.Equal(ChunkingSetting.Default, Load("---\nknowledge: 1\nrole: reference\n---\n# T").Item!.Chunking);
    }

    [Fact]
    public void A_bad_folder_file_is_refused_naming_it()
    {
        var unknown = KnowledgeItemLoader.LoadFolderFile("knowledge: 1\ndefaults:\n  title: x\n", "kb/_knowledge.yml");
        Assert.False(unknown.Ok);
        Assert.Equal("kb/_knowledge.yml", unknown.Diagnostics.Single().Path);
        Assert.Equal(3, unknown.Diagnostics.Single().Line);

        Assert.False(KnowledgeItemLoader.LoadFolderFile("knowledge: 1\n", "kb/_knowledge.yml").Ok);
        Assert.False(KnowledgeItemLoader.LoadFolderFile("defaults: {}\n", "kb/_knowledge.yml").Ok);
    }

    // the schema agrees with the reader (the reader is the authority)

    private static JsonDocument SchemaDocument() => JsonDocument.Parse(KnowledgeItemLoader.Schema());

    [Fact]
    public void The_schema_names_the_keys_and_subject_types_the_reader_reads()
    {
        using var schema = SchemaDocument();
        var root = schema.RootElement;

        Assert.Equal(KnowledgeItemLoader.ItemKeys.Order(), root.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(KnowledgeItemLoader.DefaultKeys.Order(), root.GetProperty("$defs").GetProperty("defaults").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(KnowledgeItemLoader.Roles.Order(), root.GetProperty("properties").GetProperty("role").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order());
        Assert.Equal(KnowledgeItemLoader.AudienceKeys.Order(), root.GetProperty("$defs").GetProperty("for").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());

        var subjects = root.GetProperty("$defs").GetProperty("about").GetProperty("items").GetProperty("oneOf").EnumerateArray().ToList();
        Assert.Equal(KnowledgeItemLoader.SubjectTypes.Keys.Order(), subjects.Select(s => s.GetProperty("required")[0].GetString()!).Order());
        foreach (var subject in subjects)
        {
            var type = subject.GetProperty("required")[0].GetString()!;
            var keys = subject.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != type).Order();
            Assert.Equal(KnowledgeItemLoader.SubjectTypes[type].Order(), keys);
        }
    }

    [Fact]
    public void The_schema_accepts_what_the_reader_accepts_and_refuses_what_it_refuses()
    {
        var schema = JsonSchema.FromText(KnowledgeItemLoader.Schema());
        bool Valid(string json) { using var d = JsonDocument.Parse(json); return schema.Evaluate(d.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid; }

        Assert.True(Valid("""{"knowledge":1,"title":"T","role":"instruction","about":[{"semantic-model":"Sales","measure":"revenue"},{"product":["build","sync"]},{"install":true}],"for":{"audience":["back-end"]},"owner":"x","chunking":{"split":"heading-level-2"}}"""));
        Assert.False(Valid("""{"knowledge":1,"role":"policy"}"""));
        Assert.False(Valid("""{"knowledge":1,"bogus":1}"""));
        Assert.False(Valid("""{"knowledge":1,"about":[{"semantic-model":"Sales","folder":"x"}]}"""));
        Assert.False(Valid("""{"knowledge":1,"about":[{"cube":"Sales"}]}"""));
        Assert.False(Valid("""{"knowledge":1,"chunking":"whole"}"""));
    }
}

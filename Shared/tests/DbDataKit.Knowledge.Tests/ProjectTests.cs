using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN01: a folder read as a project.</summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knowledge-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void A_folder_is_read_with_nearest_folder_defaults_and_the_project_it_is_in_is_the_implied_subject()
    {
        Write("_knowledge.yml", "knowledge: 1\ndefaults:\n  owner: data-team\n  role: instruction\n");
        Write("naming.md", "---\nknowledge: 1\ntitle: Naming\n---\nFacts are fct_.");
        Write("marts/_knowledge.yml", "knowledge: 1\ndefaults:\n  owner: marts-team\n  about:\n    - build-project: sales-warehouse\n      folder: models/marts\n");
        Write("marts/dims.md", "---\nknowledge: 1\ntitle: Dimensions\n---\nEvery dimension has an unknown member.");
        Write("marts/deep/more.md", "---\nknowledge: 1\ntitle: More\nrole: guardrail\n---\nMore.");
        Write("README.md", "not an item");
        Write("_notes.md", "not an item");
        Write(".git/x.md", "not read");

        var project = KnowledgeProject.Load(_root, new KnowledgeSubject("build-project", ["sales-warehouse"]));

        Assert.True(project.Ok, string.Join('\n', project.Diagnostics));
        Assert.Equal(["marts/deep/more.md", "marts/dims.md", "naming.md"], project.Items.Select(i => i.Id));
        var naming = project.Find("naming.md")!;
        Assert.Equal("data-team", naming.Owner);
        Assert.True(naming.AboutImplied);                                                        // no about anywhere: the project it is in
        Assert.Equal(["build-project:sales-warehouse"], naming.About.Select(s => s.Key));
        var dims = project.Find("marts/dims.md")!;
        Assert.Equal("marts-team", dims.Owner);                                                   // the nearer file wins
        Assert.Equal(KnowledgeRole.Instruction, dims.Role);                                       // only the root says it
        Assert.Equal(["build-project:sales-warehouse/folder:models/marts"], dims.About.Select(s => s.Key));
        var deep = project.Find("marts/deep/more.md")!;
        Assert.Equal(KnowledgeRole.Guardrail, deep.Role);                                         // its own key beats every default
        Assert.Equal("marts-team", deep.Owner);                                                   // the folder above, not the root
    }

    [Fact]
    public void A_bad_file_is_named_and_left_out_and_the_others_still_load()
    {
        Write("good.md", "---\nknowledge: 1\ntitle: Good\nrole: reference\n---\ntext");
        Write("bad.md", "---\nknowledge: 1\ntitle: Bad\nrole: nonsense\n---\ntext");
        Write("sub/_knowledge.yml", "knowledge: 1\ndefaults:\n  bogus: 1\n");

        var project = KnowledgeProject.Load(_root);

        Assert.False(project.Ok);
        Assert.Equal(["good.md"], project.Items.Select(i => i.Id));
        Assert.Contains(project.Diagnostics, d => d.Path == "bad.md" && d.Line == 4);
        Assert.Contains(project.Diagnostics, d => d.Path == "sub/_knowledge.yml" && d.Line == 3);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_one_diagnostic()
    {
        var project = KnowledgeProject.Load(Path.Combine(_root, "nope"));

        Assert.Single(project.Diagnostics);
        Assert.Empty(project.Items);
    }
}

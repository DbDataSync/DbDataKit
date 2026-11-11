using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN35: the install knowledge project and its policy folder.</summary>
public sealed class InstallKnowledgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knowledge-install-" + Guid.NewGuid().ToString("N"));

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
    public void The_install_template_loads_and_its_policy_items_are_guardrails_owned_by_the_administrators()
    {
        InstallKnowledge.Write(_root);

        var project = KnowledgeProject.Load(_root);

        Assert.True(project.Ok, string.Join('\n', project.Diagnostics));
        Assert.Empty(project.Diagnostics);                                                         // no warning either
        Assert.Empty(InstallKnowledge.CheckPolicyFolder(project));
        var policy = project.Items.Where(i => i.Id.StartsWith("policy/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(policy);
        Assert.All(policy, i => { Assert.Equal(KnowledgeRole.Guardrail, i.Role); Assert.Equal("administrators", i.Owner); });
        // the folders set their scope by place
        Assert.Equal(["install:true"], project.Find("shared/house-style.md")!.About.Select(s => s.Key));
        Assert.Equal(["product:build"], project.Find("build/naming.md")!.About.Select(s => s.Key));
        Assert.Equal(["product:build,sync"], project.Find("sync-and-build/environments.md")!.About.Select(s => s.Key));
    }

    [Fact]
    public void The_template_is_not_written_over_files()
    {
        Write("mine.md", "x");

        Assert.Throws<InvalidOperationException>(() => InstallKnowledge.Write(_root));
    }

    [Fact]
    public void An_item_in_the_policy_folder_that_is_not_a_guardrail_or_not_the_administrators_is_refused()
    {
        InstallKnowledge.Write(_root);
        Write("policy/note.md", "---\nknowledge: 1\ntitle: Note\nrole: reference\nowner: someone-else\n---\ntext");

        var errors = InstallKnowledge.CheckPolicyFolder(KnowledgeProject.Load(_root));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => { Assert.Equal("policy/note.md", e.Path); Assert.Equal(InstallKnowledge.PolicyRule, e.Code); });
    }

    [Theory]
    [InlineData("prompting: { build: ask }")]
    [InlineData("limits: { turns: 10 }")]
    [InlineData("data-rules: no-cloud")]
    public void A_policy_setting_in_a_knowledge_file_is_refused_with_the_message_that_says_where_it_belongs(string setting)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: T\nrole: guardrail\nowner: administrators\n{setting}\n---\ntext", "policy/p.md", "policy/p.md");

        Assert.False(result.Ok);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(6, error.Line);
        Assert.Contains("install policy", error.Message);
        Assert.Contains("agent-policy", error.Message);
    }

    [Fact]
    public void A_policy_setting_in_a_folder_file_is_refused_the_same_way()
    {
        var result = KnowledgeItemLoader.LoadFolderFile("knowledge: 1\ndefaults:\n  prompting: ask\n", "policy/_knowledge.yml");

        Assert.False(result.Ok);
        Assert.Contains("install policy", Assert.Single(result.Diagnostics).Message);
    }
}

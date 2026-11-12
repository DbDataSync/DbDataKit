using DbDataKit.Agents;
using DbDataKit.Knowledge.Cli;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>A knowledge folder in a temporary place, and the agents and catalog the checks are run against.</summary>
internal sealed class Folder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kn-cli-" + Guid.NewGuid().ToString("N")[..10]);

    public Folder() => Directory.CreateDirectory(Path);

    public Folder With(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return this;
    }

    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }

    public KnowledgeProject Load() => KnowledgeProject.Load(Path);
}

internal static class Samples
{
    public static string Item(string title, string body, string about = "  - semantic-model: Sales", string role = "instruction", string owner = "finance-data") =>
        $"---\nknowledge: 1\ntitle: {title}\nrole: {role}\nabout:\n{about}\nowner: {owner}\n---\n{body}\n";

    public static SubjectCatalog Catalog() => new SubjectCatalog().Add("semantic-model", "Sales").Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Finance");

    /// <summary>A back-end agent whose model window is small, so a long instruction pushes it over the budget (a fifth of the window).</summary>
    public static AgentBudgetInput Agent(string name = "helper", int window = 1000, int fixedTokens = 50, string scope = "semantic-models: [Sales]")
    {
        var definition = AgentDefinitionLoader.Load($"agent: 1\nname: {name}\naudience: back-end\nmodel: {{ slot: default }}\nscopes:\n  {scope}\n", "agent.yml").Definition!;
        return new AgentBudgetInput(definition, fixedTokens, window, []);
    }
}

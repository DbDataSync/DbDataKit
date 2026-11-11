using System.Text;
using DbDataKit.Apps;
using DbDataKit.Authoring;
using DbDataKit.Config;

namespace DbDataKit.Authoring.Server;

/// <summary>The <c>app</c> kind (plan 22a GP32): a code app on the default library set, started from the app template and checked with the manifest loader the Gateway uses. Built and previewed by <see cref="WorkspaceBuilds"/>, published to a Gateway.</summary>
public sealed class AppProjectKind : IProjectKind
{
    public string Name => "app";
    public string Title => "App";
    public string Description => "A code app on the default library set, with its screens as view-spec files. Built, previewed and published to a Gateway.";
    public bool Buildable => true;
    public string? GitIgnore => "node_modules/\ndist/\n";
    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } = [new("gateway", "app", "The built app, as a version of the app on a Gateway that serves apps.")];

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title) => [.. AppTemplate.Files(id, title).Select(f => new ProjectKindFile(f.Key, f.Value))];

    public Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "app.yaml");
        if (!File.Exists(path)) return Task.FromResult<IReadOnlyList<ProjectDiagnostic>>([new("error", "DBD-APP-006", "This draft has no app.yaml. An app project has its manifest, app.yaml, at the top.", "app.yaml")]);
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return Task.FromResult<IReadOnlyList<ProjectDiagnostic>>([new("error", "DBD-APP-006", $"app.yaml cannot be read: {ex.Message}", "app.yaml")]); }
        var loaded = AppManifestLoader.Load(text);
        return Task.FromResult<IReadOnlyList<ProjectDiagnostic>>([.. loaded.Diagnostics.Select(d => new ProjectDiagnostic(d.Severity.ToString().ToLowerInvariant() is "note" ? "info" : d.Severity.ToString().ToLowerInvariant(), d.Code, d.Message, "app.yaml", d.Line))]);
    }
}

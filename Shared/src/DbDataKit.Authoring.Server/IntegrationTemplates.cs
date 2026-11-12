using System.Text;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The files of the integration project templates (plan 27a IX21), kept as real files under <c>IntegrationTemplates/&lt;name&gt;/</c> and embedded: a manifest, the job in <c>src/</c>, tests with their recordings in <c>tests/</c> and a README.
/// <c>{{package}}</c> is the package name made from the project id and <c>{{title}}</c> the project's title. A test builds and runs each one with the real build and test code (<c>IntegrationTemplatesTests</c>).
/// </summary>
public static class IntegrationTemplates
{
    private const string Prefix = "IntegrationTemplates/";

    public static IReadOnlyList<ProjectKindFile> Files(string template, string id, string? title)
    {
        var package = PackageArtifacts.NameOf(id);
        var assembly = typeof(IntegrationTemplates).Assembly;
        var folder = Prefix + template + "/";
        var files = new List<ProjectKindFile>();
        // the resource name is "IntegrationTemplates/" + the folder as MSBuild writes it (backslashes on Windows): match on the normalised name, open by the name as it is
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(folder, StringComparison.Ordinal)).OrderBy(n => n.Replace('\\', '/'), StringComparer.Ordinal))
        {
            var name = resource.Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var text = reader.ReadToEnd().Replace("\r\n", "\n").Replace("{{package}}", package, StringComparison.Ordinal).Replace("{{title}}", title ?? id, StringComparison.Ordinal);
            files.Add(new ProjectKindFile(name[folder.Length..], new UTF8Encoding(false).GetBytes(text)));
        }

        if (files.Count == 0) throw new InvalidOperationException($"The template '{template}' has no files in this build.");
        return files;
    }
}

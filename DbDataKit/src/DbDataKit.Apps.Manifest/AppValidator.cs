using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>What <see cref="AppValidator.Validate"/> found: the manifest when it read, every problem and warning, and whether an author can go on (no errors).</summary>
public sealed record AppValidation(AppManifest? Manifest, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool Ok => Manifest is not null && !Diagnostics.Any(d => d.Severity == ConfigSeverity.Error);

    /// <summary>One line per finding, <c>file:line:column: severity CODE: message (fix)</c>, then a summary line.</summary>
    public string Render()
    {
        var text = new StringBuilder();
        foreach (var d in Diagnostics) text.AppendLine(d.ToString());
        var errors = Diagnostics.Count(d => d.Severity == ConfigSeverity.Error);
        var warnings = Diagnostics.Count(d => d.Severity == ConfigSeverity.Warning);
        text.Append(errors == 0 ? "The app is valid" : $"{errors} error(s)").Append(warnings == 0 ? "." : $", {warnings} warning(s).").AppendLine();
        return text.ToString();
    }
}

/// <summary>
/// Everything <c>app validate</c> checks (plan 26 section 3), as one call a command line or the apps host can make: the manifest text, the files it names, the models it uses against the Store's schemas,
/// and, when a built bundle is given, paths from the server's root and addresses of other hosts the manifest did not ask for (warnings: the browser enforces the policy, this is advice for the author).
/// </summary>
public static class AppValidator
{
    public const string OutsideUrlWarning = "DBD-APP-010";

    /// <param name="sourceDirectory">The app's folder, with <c>app.yaml</c> in it.</param>
    /// <param name="bundleDirectory">The built bundle, when there is one.</param>
    /// <param name="manifestFile">The manifest's file name in the folder.</param>
    /// <param name="modelEntities">The entities of each Store model the app can use, by model name (from the Store's schema); null skips the check, because no Store was asked.</param>
    public static AppValidation Validate(string sourceDirectory, string? bundleDirectory = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? modelEntities = null, string manifestFile = "app.yaml")
    {
        var path = Path.Combine(sourceDirectory, manifestFile);
        if (!File.Exists(path))
            return new(null, [DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.MissingFile, manifestFile, 0, 0, $"There is no `{manifestFile}` in {sourceDirectory}.", "Run the command in the app's folder, or add the manifest.")]);
        var loaded = AppManifestLoader.Load(File.ReadAllText(path), manifestFile);
        var diagnostics = new List<ConfigDiagnostic>(loaded.Diagnostics);
        if (loaded.Manifest is { } manifest)
        {
            diagnostics.AddRange(AppChecks.Files(manifest, sourceDirectory, manifestFile));
            diagnostics.AddRange(AppChecks.HomeViews(manifest, home => File.Exists(Path.Combine(sourceDirectory, home)) ? File.ReadAllText(Path.Combine(sourceDirectory, home)) : null));
            if (modelEntities is not null) diagnostics.AddRange(ModelUses(manifest, modelEntities, manifestFile));
            if (bundleDirectory is not null)
            {
                diagnostics.AddRange(AppChecks.AbsolutePaths(bundleDirectory));
                diagnostics.AddRange(AppChecks.OutsideUrls(bundleDirectory, manifest.Egress ?? AppEgress.None).Select(u =>
                    new ConfigDiagnostic(OutsideUrlWarning, ConfigSeverity.Warning, u.File, u.Line, 1, $"The bundle refers to {u.Origin}, which the manifest does not ask for; the browser will refuse the request.",
                        "Remove the dependency that uses it, or ask for the origin under `egress` and have an administrator approve it.")));
            }
        }
        return new(loaded.Manifest, diagnostics);
    }

    /// <summary>The models and entities the manifest uses that the Store does not have.</summary>
    public static IReadOnlyList<ConfigDiagnostic> ModelUses(AppManifest manifest, IReadOnlyDictionary<string, IReadOnlySet<string>> modelEntities, string manifestFile = "app.yaml")
    {
        var found = new List<ConfigDiagnostic>();
        foreach (var use in manifest.Models)
        {
            if (!modelEntities.TryGetValue(use.Model, out var entities))
            { found.Add(DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.BadUse, manifestFile, 1, 1, $"The app uses the model `{use.Model}`, which the Store does not have.", "Check the model's name, or publish the model first.")); continue; }
            foreach (var entity in use.Entities.Keys.Where(e => !entities.Contains(e)))
                found.Add(DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.BadUse, manifestFile, 1, 1, $"The app uses `{use.Model}.{entity}`, which the model does not have.", $"The model has: {string.Join(", ", entities.Order(StringComparer.Ordinal))}."));
        }
        return found;
    }
}

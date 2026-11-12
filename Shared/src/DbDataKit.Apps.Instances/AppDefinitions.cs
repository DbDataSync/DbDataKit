using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Apps;

/// <summary>
/// The kinds of the apps back end in plan 30's registry (PB01): <c>app</c> (the package: the manifest and its files), <c>app-instance</c> and <c>app-nav</c> (a set of instances a menu lists). Each is accepted by <c>serving</c> targets
/// and serves at once; the validator is what <see cref="AppInstanceStore"/> checks before it publishes, so a version that reaches the registry by another way (the shared HTTP API, plan 30 PB02) is checked the same. An instance
/// depends on its app (<c>app:name</c>) and a navigation set on the instances it lists (<c>app-instance:name</c>).
/// </summary>
public static class AppDefinitions
{
    /// <summary>Registers the three kinds.</summary>
    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(AppInstanceStore.AppKind, ["serving"], PublishMode.Serve) { Validator = ValidateApp });
        kinds.Register(new DefinitionKind(AppInstanceStore.InstanceKind, ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(AppAddresses.InstanceFormat), Validator = ValidateInstance });
        kinds.Register(new DefinitionKind(AppInstanceStore.NavigationKind, ["serving"], PublishMode.Serve) { Validator = ValidateNavigation });
        return kinds;
    }

    private static string? ValidateApp(DefinitionCandidate candidate)
    {
        try
        {
            var package = AppPackage.Parse(candidate.Manifest);
            var load = AppManifestLoader.Load(package.ManifestYaml);
            if (!load.Ok) return "The manifest is not valid: " + string.Join(" ", load.Diagnostics.Select(d => d.Message));
            return string.Equals(load.Manifest!.Name, candidate.Name, StringComparison.Ordinal) ? null : $"The manifest is named '{load.Manifest.Name}' and the app is published as '{candidate.Name}'.";
        }
        catch (AppPackageException ex)
        {
            return ex.Message;
        }
    }

    private static string? ValidateInstance(DefinitionCandidate candidate)
    {
        var node = Parse(candidate.Manifest);
        if (node is null) return "The instance is not a JSON object.";
        return node["app"] is JsonValue ? null : "The instance names no app.";
    }

    private static string? ValidateNavigation(DefinitionCandidate candidate)
    {
        var node = Parse(candidate.Manifest);
        if (node is null) return "The navigation set is not a JSON object.";
        return node["entries"] is JsonArray ? null : "The navigation set has no entries list.";
    }

    private static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

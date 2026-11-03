using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Apps;

/// <summary>
/// An instance of an app (plan 26 section 6.1): the same code published more than once. <see cref="Pin"/> is the app version it runs, or null for whatever the channel serves; <see cref="Settings"/> are the instance's own
/// (title, theme variables, feature flags); <see cref="Bindings"/> name the Store model instance each model the app uses is bound to (section 6.3).
/// </summary>
public sealed record AppInstance(string Name, string App, int? Pin = null, string? Title = null, IReadOnlyDictionary<string, string>? Settings = null, IReadOnlyDictionary<string, string>? Bindings = null)
{
    public IReadOnlyDictionary<string, string> Settings { get; init; } = Settings ?? new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Bindings { get; init; } = Bindings ?? new Dictionary<string, string>();
}

/// <summary>One member of a navigation set: an instance, with the label and icon the menu shows (the instance's title and none when absent).</summary>
public sealed record NavigationEntry(string Instance, string? Label = null, string? Icon = null);

/// <summary>A named, ordered menu of instances (plan 26 section 4): published like any definition, shown by the shell in every member instance.</summary>
public sealed record NavigationSet(string Name, string Title, IReadOnlyList<NavigationEntry> Entries);

/// <summary>An instance with the version of the app it runs and that version's manifest, as the host serves it.</summary>
public sealed record ResolvedInstance(AppInstance Instance, int InstanceVersion, DefinitionVersion AppVersion, AppManifest Manifest, AppPackage Package);

/// <summary>A publication the store refuses; the message says what to change.</summary>
public sealed class AppInstanceException(string message) : Exception(message);

/// <summary>What a token for an instance names as its audience, so one instance's validator refuses another's token (plan 26 section 6.1).</summary>
public static class AppInstanceAudience
{
    public const string Prefix = "app:";

    public static string Of(string instance) => Prefix + instance;
}

/// <summary>
/// Apps and their instances as published definitions (plan 20, O03): an app is the definition <c>app/&lt;name&gt;</c> whose content is its manifest, an instance is <c>app-instance/&lt;name&gt;</c>. Both are versioned and
/// switched by channel, so two instances can pin different versions of one app, a change to an instance is audited and rolls back like any other, and every instance of the group serves the same. Moving the app's bundle with the manifest is AP05's.
/// </summary>
public sealed class AppInstanceStore(DefinitionStore definitions)
{
    public const string AppKind = "app";
    public const string InstanceKind = "app-instance";
    public const string NavigationKind = "app-nav";

    /// <summary>The setting that names the origins allowed to frame an instance (plan 26 section 5): a comma-separated list such as <c>https://portal.example.com</c>. It becomes <c>frame-ancestors</c>; with none, nothing may frame the instance.</summary>
    public const string EmbedSetting = "embed.allowed_origins";
    public const int MaximumNavigationEntries = 50;
    private const int MaximumSettings = 50;
    private const int MaximumSettingLength = 2000;

    /// <summary>Publishes an app's manifest (with no files: a manifest alone) as its next version.</summary>
    public DefinitionVersion PublishApp(string manifestYaml, string by, string channel = DefinitionStore.Production, string? note = null) =>
        PublishApp(new AppPackage(manifestYaml), by, channel, note);

    /// <summary>Publishes an app's manifest and files as its next version. The manifest is read strictly first; what it does not pass is refused with its diagnostics.</summary>
    public DefinitionVersion PublishApp(AppPackage package, string by, string channel = DefinitionStore.Production, string? note = null)
    {
        var load = AppManifestLoader.Load(package.ManifestYaml);
        if (!load.Ok) throw new AppInstanceException("The manifest is not valid: " + string.Join(" ", load.Diagnostics.Select(d => d.Message)));
        return definitions.Publish(AppKind, load.Manifest!.Name, package.Serialize(), by, note: note, channel: channel);
    }

    /// <summary>Publishes an instance (a new version of it when it exists). The app must exist, a pin must be a version of it, and a binding must name a model the app uses.</summary>
    public DefinitionVersion Save(AppInstance instance, string by, string channel = DefinitionStore.Production, string? note = null)
    {
        if (!AppManifestLoader.NameRule.IsMatch(instance.Name)) throw new AppInstanceException($"An instance's name is lower-case letters, digits, '-' and '_', starting with a letter or digit; '{instance.Name}' is not.");
        if (AppManifestLoader.Reserved.Contains(instance.Name)) throw new AppInstanceException($"'{instance.Name}' is reserved for the host; choose another name.");
        var app = instance.Pin is { } pin ? definitions.Get(AppKind, instance.App, pin) : definitions.Current(AppKind, instance.App, channel);
        if (app is null) throw new AppInstanceException(instance.Pin is { } p ? $"The app '{instance.App}' has no version {p}." : $"The app '{instance.App}' has not been published to {channel}.");
        var manifest = AppManifestLoader.Load(AppPackage.Parse(app.Content).ManifestYaml).Manifest!;

        var models = manifest.Models.Select(m => m.Model).ToHashSet(StringComparer.Ordinal);
        foreach (var (model, modelInstance) in instance.Bindings)
        {
            if (!models.Contains(model)) throw new AppInstanceException($"'{model}' is not a model the app '{instance.App}' uses, so the instance cannot bind it. It uses: {(models.Count == 0 ? "none" : string.Join(", ", models.Order(StringComparer.Ordinal)))}.");
            if (!AppManifestLoader.NameRule.IsMatch(modelInstance)) throw new AppInstanceException($"The binding of '{model}' names '{modelInstance}', which is not a valid model instance name.");
        }
        if (instance.Settings.Count > MaximumSettings) throw new AppInstanceException($"An instance has at most {MaximumSettings} settings.");
        foreach (var (key, value) in instance.Settings)
        {
            if (!AppManifestLoader.NameRule.IsMatch(key.Replace('.', '_'))) throw new AppInstanceException($"A setting's name is lower-case letters, digits, '-', '_' and '.'; '{key}' is not.");
            if (value.Length > MaximumSettingLength) throw new AppInstanceException($"The setting '{key}' is longer than {MaximumSettingLength} characters.");
        }
        if (instance.Settings.TryGetValue(EmbedSetting, out var embed) && EmbedOrigins(embed, out var problem) is null) throw new AppInstanceException(problem!);
        return definitions.Publish(InstanceKind, instance.Name, Write(instance), by, [$"{AppKind}:{instance.App}"], note, channel);
    }

    public AppInstance? Get(string name, string channel = DefinitionStore.Production) =>
        definitions.Current(InstanceKind, name, channel) is { } version ? Read(name, version.Content) : null;

    public IReadOnlyList<AppInstance> List(string channel = DefinitionStore.Production) =>
        [.. definitions.CurrentOfKind(InstanceKind, channel).Select(v => Read(v.Name, v.Content))];

    /// <summary>The instance with the app version it runs (its pin, or the channel's current) and that version's manifest; null when there is no such instance or its app has gone.</summary>
    public ResolvedInstance? Resolve(string name, string channel = DefinitionStore.Production)
    {
        if (definitions.Current(InstanceKind, name, channel) is not { } stored) return null;
        var instance = Read(name, stored.Content);
        var app = instance.Pin is { } pin ? definitions.Get(AppKind, instance.App, pin) : definitions.Current(AppKind, instance.App, channel);
        if (app is null) return null;
        var package = AppPackage.Parse(app.Content);
        return new ResolvedInstance(instance, stored.Version, app, AppManifestLoader.Load(package.ManifestYaml).Manifest!, package);
    }

    /// <summary>The origins of an <c>embed.allowed_origins</c> value, or null with <paramref name="problem"/> set. Each is a bare http(s) origin: no path, no wildcard, nothing that could add a directive to the header.</summary>
    public static IReadOnlyList<string>? EmbedOrigins(string? value, out string? problem)
    {
        problem = null;
        var origins = new List<string>();
        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || part.EndsWith('/') || part.Any(c => c is ';' or ' ' or '*' or '\'' or '"' || char.IsControl(c)) || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            { problem = $"'{part}' is not an origin for {EmbedSetting}: use scheme, host and optional port, such as https://portal.example.com."; return null; }
            origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        return origins;
    }

    public DefinitionVersion SaveNavigation(NavigationSet set, string by, string channel = DefinitionStore.Production, string? note = null)
    {
        if (!AppManifestLoader.NameRule.IsMatch(set.Name)) throw new AppInstanceException($"A navigation set's name is lower-case letters, digits, '-' and '_', starting with a letter or digit; '{set.Name}' is not.");
        if (string.IsNullOrWhiteSpace(set.Title)) throw new AppInstanceException("A navigation set needs a title.");
        if (set.Entries.Count == 0) throw new AppInstanceException("A navigation set lists at least one instance.");
        if (set.Entries.Count > MaximumNavigationEntries) throw new AppInstanceException($"A navigation set has at most {MaximumNavigationEntries} entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in set.Entries)
        {
            if (!seen.Add(entry.Instance)) throw new AppInstanceException($"'{entry.Instance}' is listed twice in the set '{set.Name}'.");
            if (definitions.Current(InstanceKind, entry.Instance, channel) is null) throw new AppInstanceException($"'{entry.Instance}' is not an instance published to {channel}; save it before listing it in a set.");
        }
        var node = new JsonObject
        {
            ["title"] = set.Title,
            ["entries"] = new JsonArray(set.Entries.Select(e => (JsonNode)new JsonObject { ["instance"] = e.Instance, ["label"] = e.Label, ["icon"] = e.Icon }).ToArray()),
        };
        return definitions.Publish(NavigationKind, set.Name, node.ToJsonString(), by, [.. set.Entries.Select(e => $"{InstanceKind}:{e.Instance}")], note, channel);
    }

    public IReadOnlyList<NavigationSet> NavigationSets(string channel = DefinitionStore.Production) =>
        [.. definitions.CurrentOfKind(NavigationKind, channel).Select(v => ReadNavigation(v.Name, v.Content))];

    private static NavigationSet ReadNavigation(string name, string content)
    {
        var node = JsonNode.Parse(content)!;
        return new NavigationSet(name, (string)node["title"]!, [.. node["entries"]!.AsArray().Select(e => new NavigationEntry((string)e!["instance"]!, (string?)e["label"], (string?)e["icon"]))]);
    }

    private static string Write(AppInstance instance)
    {
        var node = new JsonObject { ["app"] = instance.App, ["pin"] = instance.Pin, ["title"] = instance.Title };
        node["settings"] = new JsonObject(instance.Settings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
        node["bindings"] = new JsonObject(instance.Bindings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
        return node.ToJsonString();
    }

    private static AppInstance Read(string name, string content)
    {
        var node = JsonNode.Parse(content)!;
        static Dictionary<string, string> Map(JsonNode? n) => n is JsonObject o ? o.ToDictionary(p => p.Key, p => (string)p.Value!) : [];
        return new AppInstance(name, (string)node["app"]!, node["pin"] is { } pin ? (int)pin : null, (string?)node["title"], Map(node["settings"]), Map(node["bindings"]));
    }
}

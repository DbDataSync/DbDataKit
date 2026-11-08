using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// What a host sets up to serve published dashboards and reports (plan 32 DBR10 and DBR11): the two kinds registered in the group's registry (<see cref="Register"/>, for the publisher that validates a publish), and on every
/// instance the two <see cref="PublishedViews"/> that a <see cref="DefinitionFollower"/> hands each served version to. A version an instance cannot load (a model version it does not have, a file that is gone) leaves the one
/// served and is reported: <see cref="Unserved"/> names it, and the module's health says it.
/// </summary>
public sealed class ViewsPublications
{
    private readonly DefinitionStore store;
    private readonly string channel;
    private readonly PayloadStore? payloads;

    public ViewsPublications(DefinitionStore store, DefinitionPublisher publisher, PayloadStore? payloads = null, string channel = DefinitionStore.Production)
    {
        this.store = store;
        this.channel = channel;
        this.payloads = payloads;
        Dashboards = new PublishedViews(ViewDefinitions.Dashboard, store, publisher, payloads);
        Reports = new PublishedViews(ViewDefinitions.Report, store, publisher, payloads);
    }

    public PublishedViews Dashboards { get; }

    public PublishedViews Reports { get; }

    /// <summary>
    /// A document an author is still writing, checked as a publish would check it and built as this channel would serve it (plan 32 DBR28), and **stored nowhere**: its models are pinned to the versions the channel serves now
    /// (what the Gateway's publish does), the registry's validator for the kind runs over it (the loader, the catalog check, saved queries, control attributes, images), and what passes is a <see cref="PublishedView"/> a resolver can run.
    /// A document that would be refused is not built: <paramref name="refusal"/> says why in the publish's words and <paramref name="diagnostics"/> places the loader's findings in the text.
    /// </summary>
    public PublishedView? Draft(string kind, string name, string text, out string? refusal, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var file = ViewDefinitions.FileName(kind, name, text);
        var loaded = ViewsLoader.Load(text, file, ViewDefinitions.KindOf(kind));
        diagnostics = [.. loaded.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error)];
        if (loaded.Document is null)
        {
            refusal = $"The {kind} does not load: {string.Join(" | ", diagnostics.Take(5).Select(d => d.KeyPath is null ? d.ToString() : $"{d} [{d.KeyPath}]"))}";
            return null;
        }
        var dependsOn = ViewDefinitions.DependsOn(kind, name, text, model => store.Current(ViewDefinitions.ModelKind, model.ToLowerInvariant(), channel)?.Version);
        var candidate = new DefinitionCandidate(kind, name, text, [], dependsOn, ViewDefinitions.FormatOf(text) ?? DefinitionFormat.First);
        refusal = ViewDefinitions.Validate(candidate, ViewPublishingSources.From(store, payloads));
        if (refusal is not null) return null;
        // version 0: it is nobody's version, and it has no files, so a lookup of its files finds none
        return For(kind).Build(new DefinitionVersion(kind, name, 0, string.Empty, text, dependsOn, "draft", DateTimeOffset.UtcNow, null));
    }

    /// <summary>The subscribers a follower is given (alone, or beside the host's own).</summary>
    public IReadOnlyList<IDefinitionSubscriber> Subscribers => [Dashboards, Reports];

    public PublishedViews For(string kind) => kind == ViewDefinitions.Report ? Reports : Dashboards;

    /// <summary>
    /// Registers the kinds <c>dashboard</c> and <c>report</c> with the validator over the group's models and files, in the registry of the publisher a host builds (<c>new DefinitionPublisher(state, kinds, payloads, "serving")</c>).
    /// The publisher a host publishes the Gateway's <c>semantic-model</c> through needs no change: a dashboard's <c>depends_on</c> is <c>semantic-model:name@version</c>, and a model's own (<c>data-source:name</c>) reads the same way.
    /// </summary>
    public static DefinitionKinds Register(DefinitionKinds kinds, DefinitionStore store, PayloadStore? payloads = null) =>
        ViewDefinitions.Register(kinds, ViewPublishingSources.From(store, payloads));

    /// <summary>A follower over both kinds; <paramref name="problem"/> is told when a version could not be used (the one served before keeps serving).</summary>
    public DefinitionFollower Follower(Action<string, string, int?, Exception> problem) => new(store, Subscribers, problem, channel);

    /// <summary>The definitions the channel serves that this instance does not (yet): kind, name, the version the channel serves and the one this instance serves (null for none).</summary>
    public IReadOnlyList<(string Kind, string Name, int Head, int? Served)> Unserved()
    {
        var found = new List<(string, string, int, int?)>();
        foreach (var kind in ViewDefinitions.Kinds)
            foreach (var head in store.CurrentOfKind(kind, channel))
            {
                var served = For(kind).Find(head.Name)?.Version;
                if (served != head.Version) found.Add((kind, head.Name, head.Version, served));
            }
        return found;
    }
}

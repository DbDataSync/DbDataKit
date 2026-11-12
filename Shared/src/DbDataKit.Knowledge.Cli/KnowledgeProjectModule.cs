using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>A version could not be staged: its files do not check (the diagnostics say which, with file and line). What the channel served before keeps serving.</summary>
public sealed class KnowledgeStageRefusedException(IReadOnlyList<ConfigDiagnostic> problems) : Exception(string.Join(" | ", problems.Where(p => p.Severity == ConfigSeverity.Error).Select(p => p.ToString())))
{
    public IReadOnlyList<ConfigDiagnostic> Problems { get; } = problems;
}

/// <summary>What staging a version did.</summary>
/// <param name="Project">The project's name.</param>
/// <param name="Version">The version staged.</param>
/// <param name="IndexChannel">The channel of the knowledge index the version was written to.</param>
/// <param name="Items">Items in the version.</param>
/// <param name="Unchanged">Items whose text is the same as in the version the channel serves (they are written again, which costs no model: their vectors are copied).</param>
/// <param name="ChunksCopied">Vectors taken over from the served version because the chunk's text did not change.</param>
/// <param name="ChunksEmbedded">Vectors made by the model in this step: only chunks of changed text.</param>
/// <param name="ChunksFailed">Chunks the model refused (they are listed with the reason and answer by words).</param>
/// <param name="Problem">Why the vectors are not complete (the model could not be asked), or null; words answer meanwhile.</param>
public sealed record StageReport(string Project, int Version, string IndexChannel, int Items, int Unchanged, int ChunksCopied, int ChunksEmbedded, int ChunksFailed, string? Problem);

/// <summary>What the module says of one project it serves, for the health page.</summary>
public sealed record ProjectHealth(string Project, int Version, string IndexChannel, int Items, int Chunks, string? Model, int Embedded, int ChunksTotal, bool VectorsComplete);

/// <summary>
/// The <c>knowledge</c> module of the serving layer, as a library (plan 27a KN11): it follows the kind <c>knowledge-project</c> of the group's definitions. <b>Each version has an index of its own</b> (the channel <c>release/name@version</c> of the <c>knowledge</c> and
/// <c>knowledge_vectors</c> modules), built whole before the channel moves to it, so <b>activation is the definitions' head moving and nothing else</b>: a search reads the head's version and so sees the whole of one version and never half of two, and a rollback is the head
/// moving back to an index that is still there (the served and the previous version, and a staged one, keep theirs; older ones go). <see cref="StageAsync"/> checks every item (the reader, the subjects), writes the words and gives each chunk the vector the served version already has
/// for the same text, then embeds only what is left: a version that changed one item embeds that item's chunks. It never blocks on a model: with none, or one that is down, the words answer and <see cref="StageReport.Problem"/> says why.
/// All reads go through <see cref="Reader"/> under the caller's rules; nothing reads the state database directly.
/// </summary>
public sealed class KnowledgeProjectModule : IDefinitionSubscriber
{
    private readonly DefinitionPublisher _publisher;
    private readonly PayloadStore _payloads;
    private readonly KnowledgeStore _store;
    private readonly KnowledgeVectorStore? _vectors;
    private readonly Func<EmbeddingBinding?> _binding;
    private readonly Func<ISubjectCatalog?> _catalog;
    private readonly TimeProvider _time;

    /// <param name="publisher">The group's definitions: the heads say which version a release serves.</param>
    /// <param name="payloads">Where the files of a version are kept.</param>
    /// <param name="store">The words index (module <c>knowledge</c>).</param>
    /// <param name="time">The clock the indexing time is read from.</param>
    /// <param name="release">The release channel this module serves (<c>prod</c> in production).</param>
    /// <param name="vectors">Where vectors are kept; null on an engine that cannot hold them (words only).</param>
    /// <param name="binding">The embedding model bound for this channel now, or null.</param>
    /// <param name="catalog">What exists to be about, or null (no subject is checked).</param>
    public KnowledgeProjectModule(DefinitionPublisher publisher, PayloadStore payloads, KnowledgeStore store, KnowledgeVectorStore? vectors, string release = DefinitionStore.Production, Func<EmbeddingBinding?>? binding = null, Func<ISubjectCatalog?>? catalog = null, TimeProvider? time = null)
    {
        _publisher = publisher;
        _payloads = payloads;
        _store = store;
        _vectors = vectors;
        Release = release;
        _binding = binding ?? (() => null);
        _catalog = catalog ?? (() => null);
        _time = time ?? TimeProvider.System;
    }

    public string Release { get; }

    public string Kind => KnowledgeDefinitions.Kind;

    public DefinitionFormats Formats => new(KnowledgeDefinitions.Version, KnowledgeDefinitions.Version);

    /// <summary>The index channel of one version of a project.</summary>
    public string IndexChannel(string project, int version) => $"{Release}/{project}@{version}";

    private string Prefix(string project) => $"{Release}/{project}@";

    // ---- staging ----

    /// <summary>
    /// Builds the index of a version beside the one the channel serves: checks every item, writes the words, takes over the vectors of unchanged chunks and embeds the rest. Safe to run again (what is there is not written or embedded again).
    /// </summary>
    /// <exception cref="KnowledgeStageRefusedException">A file does not check.</exception>
    public async Task<StageReport> StageAsync(string project, int version, CancellationToken cancellation = default)
    {
        var files = KnowledgeDefinitions.Files(_publisher, _payloads, project, version);
        var parsed = KnowledgeProject.LoadFiles(project, files);
        var diagnostics = new List<ConfigDiagnostic>(parsed.Diagnostics);
        if (_catalog() is { } catalog) foreach (var item in parsed.Items) diagnostics.AddRange(SubjectResolver.CheckOnSave(item, catalog));
        if (diagnostics.HasErrors()) throw new KnowledgeStageRefusedException(diagnostics);

        var channel = IndexChannel(project, version);
        var index = new StateKnowledgeIndex(_store, channel, _vectors);
        var served = ServedChannel(project);
        var unchanged = 0;
        foreach (var item in parsed.Items)
        {
            if (served is not null && _store.Item(served, item.Id)?.Hash == item.Hash) unchanged++;
            index.Put(item, Chunker.Split(item), project, $"v{version}", _time);
        }
        // an item the version no longer has is not in this channel at all (it was never written there): a channel is a whole version
        foreach (var gone in _store.Items(channel).Where(i => parsed.Items.All(p => p.Id != i.Id)).Select(i => i.Id).ToList()) _store.Remove(channel, gone);

        int copied = 0, embedded = 0, failed = 0;
        string? problem = null;
        if (_vectors is not null && _binding() is { } binding)
        {
            var embedder = new KnowledgeEmbedder(_vectors, channel);
            if (served is not null && _vectors.ReadyIndex(served) is { } ready && ready.ModelId == binding.ModelId)
            {
                if (_vectors.Indexes(channel).All(i => i.ModelId != ready.ModelId)) _vectors.Begin(channel, ready.ModelId, ready.Dimension);
                copied = _vectors.CopyUnchanged(served, channel, ready.ModelId, ready.Dimension);
            }
            var run = await embedder.RunAsync(binding, cancellation: cancellation).ConfigureAwait(false);
            embedded = run.Embedded;
            failed = run.Failed;
            problem = run.Problem;
        }
        return new StageReport(project, version, channel, parsed.Items.Count, unchanged, copied, embedded, failed, problem);
    }

    /// <summary>The channel of the version the release serves now (the head's active one) when its index is there.</summary>
    private string? ServedChannel(string project) =>
        _publisher.Head(KnowledgeDefinitions.Kind, project, Release) is { Active: { } active } && _store.Counts(IndexChannel(project, active)).Items > 0 ? IndexChannel(project, active) : null;

    // ---- following the definitions ----

    /// <summary>
    /// What the follower hands over when a version is activated, switched to or rolled back to, or the project is retired (<paramref name="current"/> is null). The index of the version is there already when it was staged; when it is not (this instance never saw the stage), the words are
    /// written now and the vectors follow in the background step (<see cref="CompleteVectorsAsync"/>), so activation never waits for a model. Indexes of versions that are neither served, previous nor staged are removed.
    /// </summary>
    public void Apply(string name, DefinitionVersion? current)
    {
        if (current is null) { Remove(name); return; }
        if (_store.Counts(IndexChannel(name, current.Version)).Items == 0)
            StageAsync(name, current.Version).GetAwaiter().GetResult();
        Prune(name);
    }

    /// <summary>The project is retired: every index of it goes, whatever the head says.</summary>
    public int Remove(string project)
    {
        var removed = 0;
        foreach (var channel in _store.Channels(Prefix(project)).ToList())
        {
            _store.RemoveChannel(channel);
            _vectors?.Drop(channel);
            removed++;
        }
        return removed;
    }

    /// <summary>Removes the indexes of versions the head no longer names (it keeps the served, the previous and the staged version: a rollback is instant).</summary>
    public int Prune(string project)
    {
        var head = _publisher.Head(KnowledgeDefinitions.Kind, project, Release);
        var keep = new[] { head?.Active, head?.Previous, head?.Staged }.Where(v => v is not null).Select(v => IndexChannel(project, v!.Value)).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var channel in _store.Channels(Prefix(project)).Where(c => !keep.Contains(c)).ToList())
        {
            _store.RemoveChannel(channel);
            _vectors?.Drop(channel);
            removed++;
        }
        return removed;
    }

    /// <summary>The background step: gives the served versions' chunks the vectors they lack (a version activated with no stage, a model bound later). Returns the chunks embedded.</summary>
    public async Task<int> CompleteVectorsAsync(CancellationToken cancellation = default)
    {
        if (_vectors is null || _binding() is not { } binding) return 0;
        var total = 0;
        foreach (var project in _publisher.Heads(KnowledgeDefinitions.Kind, Release))
        {
            if (project.Active is not { } active) continue;
            var run = await new KnowledgeEmbedder(_vectors, IndexChannel(project.Name, active)).RunAsync(binding, cancellation: cancellation).ConfigureAwait(false);
            total += run.Embedded;
        }
        return total;
    }

    // ---- reading ----

    /// <summary>The channel the generated items of every <c>build-metadata</c> definition are kept in (one per release: the items of a definition are replaced as a whole by <see cref="GeneratedSync"/>).</summary>
    public string GeneratedChannel => $"{Release}/generated";

    /// <summary>The projects the release serves, each with its version and index channel; the generated knowledge is one more (version 0) when there is any.</summary>
    public IReadOnlyList<(string Project, int Version, string IndexChannel)> Served()
    {
        var served = _publisher.Heads(KnowledgeDefinitions.Kind, Release).Where(h => h.Active is not null).Select(h => (h.Name, h.Active!.Value, IndexChannel(h.Name, h.Active!.Value))).ToList();
        if (_store.Counts(GeneratedChannel).Items > 0) served.Add(("generated", 0, GeneratedChannel));
        return served;
    }

    /// <summary>What follows the kind <c>build-metadata</c> (KN13): the definitions' documents rendered as items of the generated channel, an unchanged item not indexed again, an item the version no longer renders retired.</summary>
    public IDefinitionSubscriber BuildMetadata => new BuildMetadataSubscriber(this);

    private sealed class BuildMetadataSubscriber(KnowledgeProjectModule module) : IDefinitionSubscriber
    {
        public string Kind => BuildMetadataDefinitions.Kind;

        public DefinitionFormats Formats => new(BuildMetadataDefinitions.Version, BuildMetadataDefinitions.Version);

        public void Apply(string name, DefinitionVersion? current)
        {
            var index = new StateKnowledgeIndex(module._store, module.GeneratedChannel, module._vectors);
            var source = new GeneratedSource(BuildMetadataDefinitions.Kind, name, $"v{current?.Version}", module.Release);
            if (current is null) { GeneratedSync.Retire(index, source); return; }
            var owner = BuildMetadataDefinitions.ReadManifest(current.Content).Owner;
            var document = BuildMetadataDefinitions.Read(module._publisher, module._payloads, name, current.Version);
            GeneratedSync.Apply(index, source with { Owner = owner }, GeneratedBuildKnowledge.Render(document, source with { Owner = owner }), module._time);
        }
    }

    /// <summary>The reading commands over every project the release serves, under the caller's rules. The version a project is read at is the head's, looked up at each question.</summary>
    public IKnowledgeReader Reader(IKnowledgeCaller caller, ISubjectCatalog catalog, EmbeddingResolver? embeddings = null, KnowledgeSearchMode mode = KnowledgeSearchMode.Both) =>
        new ServedReader(this, caller, catalog, embeddings, mode);

    /// <summary>The module's health: per project the version served, the counts, the model and how much of it is embedded.</summary>
    public IReadOnlyList<ProjectHealth> Health() =>
        [.. Served().Select(s =>
        {
            var (items, chunks) = _store.Counts(s.IndexChannel);
            var progress = _vectors?.Progress(s.IndexChannel);
            return new ProjectHealth(s.Project, s.Version, s.IndexChannel, items, chunks, progress?.Ready?.ModelId, progress?.Embedded ?? 0, progress?.Chunks ?? chunks, progress?.Ready is not null && progress.Embedded >= progress.Chunks);
        })];

    private sealed class ServedReader(KnowledgeProjectModule module, IKnowledgeCaller caller, ISubjectCatalog catalog, EmbeddingResolver? embeddings, KnowledgeSearchMode mode) : IKnowledgeReader
    {
        // the readers of the projects are asked for their own index channel: the channel argument of a command is the release, and is not an index channel
        public async Task<KnowledgeSearchResult> SearchAsync(string text, string channel, string? about, string? role, CancellationToken cancellation)
        {
            var hits = new List<SearchHit>();
            string? note = null;
            var used = KnowledgeSearchMode.Words;
            foreach (var served in module.Served())
            {
                var reader = new StateKnowledgeReader(_ => new StateKnowledgeIndex(module._store, served.IndexChannel, module._vectors), caller, catalog, embeddings, mode);
                var found = await reader.SearchAsync(text, served.IndexChannel, about, role, cancellation).ConfigureAwait(false);
                hits.AddRange(found.Hits);
                note ??= found.Note;
                if (found.Used > used) used = found.Used;
            }
            return new KnowledgeSearchResult([.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.ItemId, StringComparer.Ordinal).Take(5)], used, note);
        }

        public ShownItem? Show(string id, string channel, string? section, string? version)
        {
            foreach (var served in module.Served())
                if (new StateKnowledgeReader(_ => new StateKnowledgeIndex(module._store, served.IndexChannel, module._vectors), caller, catalog, embeddings, mode).Show(id, served.IndexChannel, section, version) is { } shown) return shown;
            return null;
        }

        public IReadOnlyList<AboutItem> About(string subjectKey, string channel) =>
            [.. module.Served().SelectMany(served => new StateKnowledgeReader(_ => new StateKnowledgeIndex(module._store, served.IndexChannel, module._vectors), caller, catalog, embeddings, mode).About(subjectKey, served.IndexChannel))
                .DistinctBy(i => i.Id)];
    }
}

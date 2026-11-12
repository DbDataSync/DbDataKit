using DbDataKit.Knowledge;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Engines;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>The vector side of the index as the screen shows it.</summary>
public sealed record VectorStateView(string? ReadyModel, int? ReadyDimension, string? BuildingModel, int? BuildingDimension, int Chunks, int Embedded, int BuildingEmbedded, int Failed);

public sealed record IndexFailureView(string ItemId, int Seq, string Model, int Attempts, string Message, DateTimeOffset FailedAt);

/// <summary>What the last background run of the embedder did (null before the first).</summary>
public sealed record IndexRunView(DateTimeOffset At, int Embedded, int Failed, int Remaining, bool Switched, string? Problem);

/// <summary>
/// The console's knowledge index as the Knowledge page shows it (plan 27a KN28, KN07, KN08): whether it is built, the words it finds by, whether vectors are possible here and why not, the embedding model the <c>embeddings</c> slot names, how far the vectors are
/// (the ready index and the one being built beside it), the chunks that failed and the last run.
/// </summary>
public sealed record KnowledgeIndexView(string Channel, bool Opened, bool Running, int Items, int Chunks, string WordsMode, bool VectorsAvailable, string? VectorsProblem, ConsoleModels.EmbeddingBindingView Embedding,
    VectorStateView? Vectors, IReadOnlyList<IndexFailureView> Failures, IndexRunView? LastRun, string Message);

public sealed record IndexSyncResult(int Added, int Unchanged, int Removed);

/// <summary>The index the console builds over its own two knowledge projects, on a state database of its own: where the console's state is a server engine, that database (the modules <c>knowledge</c> and, where the engine has the vector feature, <c>knowledge_vectors</c> beside the console's modules); where it is SQLite, a file of its own, <c>knowledge-index.db</c>, which has words only. The index is derived data: it can always be made again from the files.</summary>
public sealed class ConsoleKnowledgeIndex(ConsoleSettings settings, ConsoleKnowledge knowledge, ConsoleModels models, Func<string, string?>? environment = null, ILogger? log = null) : IDisposable
{
    /// <summary>The console's one channel: the knowledge it reads from its own directory.</summary>
    public const string Channel = "main";

    private sealed record Opened(StateDatabase Database, bool Vectors, string? VectorsProblem);

    private readonly object _gate = new();
    private Opened? _opened;
    private Task? _running;
    private bool _again;
    private IndexRunView? _last;
    private string? _openProblem;

    /// <summary>The marker that says the index was asked for: until then nothing is created in the console's database by looking at the page.</summary>
    private string Marker => Path.Combine(settings.Directory, "knowledge-index.on");

    public bool Enabled => File.Exists(Marker);

    // ---- opening ----

    private Opened Open()
    {
        lock (_gate)
        {
            if (_opened is not null) return _opened;
            var state = settings.State ?? new StateSettings();
            if (state.IsSqlite)
            {
                var db = StateOpener.Open(state, Path.Combine(settings.Directory, "knowledge-index.db"), [KnowledgeModule.Module], StateScope.Install, appliedBy: "dbdatakit knowledge index");
                var report = EngineSupport.Features(StateEngineIds.Sqlite, null);
                return _opened = new Opened(db, false, EngineSupport.Unmet([KnowledgeVectorsModule.Module], report).FirstOrDefault().Message);
            }
            var secrets = new SecretResolverChain([ConsoleSecrets.Open(settings.Directory, environment), new EnvSecretProvider(environment)]);
            var words = StateOpener.Open(state, "", [KnowledgeModule.Module], StateScope.Install, secrets, "dbdatakit knowledge index");
            using var connection = words.OpenConnection();
            var features = EngineSupport.Features(connection, state.EngineId);
            var unmet = EngineSupport.Unmet([KnowledgeVectorsModule.Module], features);
            if (unmet.Count > 0) return _opened = new Opened(words, false, unmet[0].Message);
            try { return _opened = new Opened(StateOpener.Open(state, "", [KnowledgeModule.Module, KnowledgeVectorsModule.Module], StateScope.Install, secrets, "dbdatakit knowledge index"), true, null); }
            catch (InvalidOperationException ex) { return _opened = new Opened(words, false, ex.Message); }
        }
    }

    private (StateKnowledgeIndex Index, Opened Opened) Index()
    {
        var opened = Open();
        return (new StateKnowledgeIndex(new KnowledgeStore(opened.Database), Channel, opened.Vectors ? new KnowledgeVectorStore(opened.Database) : null), opened);
    }

    // ---- the files into the index ----

    private IEnumerable<(string Project, KnowledgeItem Item)> FileItems()
    {
        foreach (var name in new[] { "install", "project" })
            if (knowledge.Folder(name) is { } folder && Directory.Exists(folder.Path))
                foreach (var item in folder.Project.Items) yield return (name, item with { Id = $"{name}:{item.Id}" });
    }

    /// <summary>Brings the index in step with the files: an item whose text did not change is not written again, a changed one replaces the old, an item that is no longer in a file is taken out. Words only: no model is asked.</summary>
    public IndexSyncResult Sync()
    {
        var (index, _) = Index();
        File.WriteAllText(Marker, "");
        int added = 0, unchanged = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (project, item) in FileItems())
        {
            present.Add(item.Id);
            if (index.Put(item, Chunker.Split(item), project, item.Hash[..12])) added++; else unchanged++;
        }
        var removed = 0;
        foreach (var stored in index.Store.Items(Channel).Where(i => !present.Contains(i.Id)))
            if (index.Remove(stored.Id)) removed++;
        return new IndexSyncResult(added, unchanged, removed);
    }

    // ---- the background step ----

    /// <summary>Starts the background step (words, then vectors) unless one is running, in which case it runs once more when it ends. A no-op until the index was asked for. Saving an item never waits for it.</summary>
    public void Refresh()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) { _again = true; return; }
            _running = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        do
        {
            lock (_gate) _again = false;
            try
            {
                Sync();
                await EmbedAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogWarning(ex, "The knowledge index step failed.");
                lock (_gate) _last = new IndexRunView(DateTimeOffset.UtcNow, 0, 0, 0, false, ex.Message);
            }
        }
        while (_again);
    }

    /// <summary>Gives the chunks that need one a vector with the model the <c>embeddings</c> slot names, a few hundred at a time, until nothing is left or the model stops.</summary>
    public async Task EmbedAsync(CancellationToken cancellation = default)
    {
        var (index, opened) = Index();
        if (!opened.Vectors || index.Vectors is null || models.EmbeddingFor(Channel) is not { } bound) return;
        var embedder = new KnowledgeEmbedder(index.Vectors, Channel);
        EmbedRun total = new(0, 0, 0, false, null);
        while (true)
        {
            var run = await embedder.RunAsync(new EmbeddingBinding(bound.Generator, bound.ModelId), 32, 256, cancellation).ConfigureAwait(false);
            total = new EmbedRun(total.Embedded + run.Embedded, total.Failed + run.Failed, run.Remaining, total.Switched || run.Switched, run.Problem);
            if (run.Problem is not null || run.Remaining == 0 || run.Embedded + run.Failed == 0) break;
        }
        lock (_gate) _last = new IndexRunView(DateTimeOffset.UtcNow, total.Embedded, total.Failed, total.Remaining, total.Switched, total.Problem);
    }

    /// <summary>Forgets the failures of the bound model so its chunks are tried again, and starts the step.</summary>
    public void RetryFailures()
    {
        var (index, opened) = Index();
        if (opened.Vectors && index.Vectors is not null && models.EmbeddingBinding(Channel).ModelId is { } id) index.Vectors.RetryFailures(Channel, id);
        Refresh();
    }

    /// <summary>Throws the vectors away and makes them again (words stay).</summary>
    public void Rebuild()
    {
        var (index, opened) = Index();
        if (opened.Vectors) index.Vectors?.Drop(Channel);
        Refresh();
    }

    // ---- suggestions (KN19) ----

    private sealed class ConsoleSuggester(string person, bool administrator) : DbDataKit.Knowledge.Cli.ISuggestionCaller
    {
        public string Person { get; } = person;

        public string? Agent => null;

        /// <summary>The console knows administrators and viewers; the groups of the identity home are another module's. An administrator owns what the install's administrators own.</summary>
        public IReadOnlyCollection<string> Groups { get; } = administrator ? ["administrators"] : [];

        public bool AnswerConfirmed(string answerKey) => true;
    }

    /// <summary>The desk of suggestions over the console's index, as the person who asks (their own suggestions, and for an administrator those of the administrators' items); null until the index is built.</summary>
    public DbDataKit.Knowledge.Cli.SuggestionDesk? Desk(string person, bool administrator)
    {
        if (!Enabled) return null;
        var (index, opened) = Index();
        return new DbDataKit.Knowledge.Cli.SuggestionDesk(new SuggestionStore(opened.Database), index, new ConsoleSuggester(person, administrator), new ConsoleCaller(null, administrator), knowledge.Catalog);
    }

    // ---- verified queries that need another look (KN22) ----

    /// <summary>The verified queries of the files the index marks as needing re-verification, by the id the files give them (the project is not part of it), with why. Empty when the index is not built or cannot be read.</summary>
    public IReadOnlyDictionary<string, string> NeedsAnotherLook()
    {
        if (!Enabled) return new Dictionary<string, string>();
        try
        {
            var (index, _) = Index();
            return index.Store.VerifiedQueries(Channel).Where(v => v.Verified.Status == VerifiedStatus.NeedsReverification)
                .ToDictionary(v => v.ItemId[(v.ItemId.IndexOf(':', StringComparison.Ordinal) + 1)..], v => v.Verified.StatusNote ?? "It needs to be verified again.", StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException or IOException) { return new Dictionary<string, string>(); }
    }

    // ---- what the screen shows ----

    public KnowledgeIndexView View()
    {
        var binding = models.EmbeddingBinding(Channel);
        var running = _running is { IsCompleted: false };
        if (!Enabled) return new KnowledgeIndexView(Channel, false, false, 0, 0, "", false, null, binding, null, [], null, "The index is not built. Build it to search the knowledge by its words, and by its meaning where the engine can hold vectors and an embedding model is bound.");
        try
        {
            var (index, opened) = Index();
            var status = index.Status();
            VectorStateView? vectors = null;
            IReadOnlyList<IndexFailureView> failures = [];
            if (status.Vectors is { } p)
            {
                vectors = new VectorStateView(p.Ready?.ModelId, p.Ready?.Dimension, p.Building?.ModelId, p.Building?.Dimension, p.Chunks, p.Embedded, p.BuildingEmbedded, p.Failed);
                failures = [.. index.Vectors!.Failures(Channel, (p.Building ?? p.Ready)?.ModelId).Take(50).Select(f => new IndexFailureView(f.ItemId, f.Seq, f.ModelId, f.Attempts, f.Message, f.FailedAt))];
            }
            var message = status.Items == 0 ? "The index is built and holds no item: there is nothing in the knowledge folders yet." : $"{status.Items} items in {status.Chunks} chunks.";
            return new KnowledgeIndexView(Channel, true, running, status.Items, status.Chunks, status.Mode.ToString().ToLowerInvariant(), opened.Vectors, opened.VectorsProblem, binding, vectors, failures, _last, message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            _openProblem = ex.Message;
            return new KnowledgeIndexView(Channel, false, false, 0, 0, "", false, null, binding, null, [], _last, $"The index could not be opened: {ex.Message}");
        }
    }

    // ---- searching ----

    private sealed class ConsoleCaller(string? audience, bool administrator) : IKnowledgeCaller
    {
        public string? Audience { get; } = audience;

        /// <summary>An administrator of the console may read every subject it knows; anyone else reads none (fail closed): they find what is about the install itself, nothing about a model or a project until roles on subjects reach the console.</summary>
        public bool MayRead(KnowledgeSubject subject) => administrator;
    }

    public async Task<KnowledgeSearchResult> SearchAsync(string question, KnowledgeSearchMode mode, string? audience, bool administrator, CancellationToken cancellation = default)
    {
        if (!Enabled) return new KnowledgeSearchResult([], KnowledgeSearchMode.Words, "The index is not built.");
        var (index, _) = Index();
        return await index.FindAsync(question, new ConsoleCaller(audience, administrator), knowledge.Catalog, mode, models.EmbeddingById, 8, cancellation).ConfigureAwait(false);
    }

    /// <summary>The last problem opening the index had, for a test.</summary>
    internal string? OpenProblem => _openProblem;

    /// <summary>Waits for the running step to end (a test, or a stop).</summary>
    public async Task WaitAsync()
    {
        Task? running;
        lock (_gate) running = _running;
        if (running is not null) await running.ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}

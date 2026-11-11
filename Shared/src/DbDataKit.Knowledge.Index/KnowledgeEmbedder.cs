using DbDataKit.State;
using Microsoft.Extensions.AI;

namespace DbDataKit.Knowledge;

/// <summary>An embedding model as the indexer uses it: the generator and the id the install records it under (<c>connection/model</c>).</summary>
public sealed record EmbeddingBinding(IEmbeddingGenerator<string, Embedding<float>> Generator, string ModelId);

/// <summary>What one run of the embedder did.</summary>
/// <param name="Embedded">Chunks given a vector.</param>
/// <param name="Failed">Chunks whose embedding failed in this run.</param>
/// <param name="Remaining">Chunks still to do after the run (ones that failed for good are not counted).</param>
/// <param name="Switched">The index being built became the one searches use.</param>
/// <param name="Problem">Why the run stopped early (the provider is unreachable, the model gives another size), in words for a person; null when it ran through.</param>
public sealed record EmbedRun(int Embedded, int Failed, int Remaining, bool Switched, string? Problem);

/// <summary>
/// The background step that gives chunks their vectors (plan 27a KN07 and KN08): per channel, it finds the chunks that have no current vector for the bound model (new, or whose text changed: only <i>those</i> are embedded, the hash of the chunk text says which),
/// embeds them in batches, stores the vectors, records the chunks that could not be embedded with the reason, removes the vectors of chunks that are gone, and, when nothing is left to do for a model that was bound after another, switches the channel to it at once.
/// <b>It is separate from saving an item</b>: <see cref="StateKnowledgeIndex.Put"/> writes words and chunks and never calls a model, so a model that is slow or down never blocks saving; the host calls <see cref="RunAsync"/> on its own schedule and the
/// search by words answers meanwhile. Nothing here throws for a provider failure: it is told in <see cref="EmbedRun.Problem"/> and recorded.
/// </summary>
public sealed class KnowledgeEmbedder(KnowledgeVectorStore vectors, string channel)
{
    public string Channel { get; } = channel;

    /// <summary>The text probed with when the number of dimensions of a model is not known yet.</summary>
    public const string Probe = "dimension probe";

    /// <summary>Embeds up to <paramref name="maximum"/> chunks that need it with <paramref name="binding"/>'s model, <paramref name="batch"/> at a time.</summary>
    public async Task<EmbedRun> RunAsync(EmbeddingBinding binding, int batch = 32, int maximum = int.MaxValue, CancellationToken cancellation = default)
    {
        var index = vectors.Indexes(Channel).FirstOrDefault(i => i.ModelId == binding.ModelId);
        if (index is null)
        {
            // a model not seen before: how many dimensions it gives is learnt by asking it once
            int dimension;
            try { dimension = (await binding.Generator.GenerateAsync([Probe], cancellationToken: cancellation).ConfigureAwait(false)).FirstOrDefault()?.Vector.Length ?? 0; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new EmbedRun(0, 0, 0, false, $"The embedding model '{binding.ModelId}' could not be asked: {ex.Message}"); }
            if (dimension == 0) return new EmbedRun(0, 0, 0, false, $"The embedding model '{binding.ModelId}' gave no vector for a sample text.");
            try { index = vectors.Begin(Channel, binding.ModelId, dimension); }
            catch (Exception ex) when (ex is VectorDimensionException or ArgumentOutOfRangeException) { return new EmbedRun(0, 0, 0, false, ex.Message); }
        }

        if (index.IsReady) vectors.DropBuilding(Channel);   // the model of the ready index is bound again: a build of another one is not wanted
        var tried = new HashSet<(string Item, int Seq)>();
        int embedded = 0, failed = 0;
        string? problem = null;
        while (embedded + failed < maximum)
        {
            cancellation.ThrowIfCancellationRequested();
            // a chunk that failed in this run is not tried again in it (its attempts count once per run)
            var want = Math.Min(batch, maximum - embedded - failed);
            var pending = vectors.Pending(Channel, index, want + tried.Count).Where(c => !tried.Contains((c.ItemId, c.Seq))).Take(want).ToList();
            if (pending.Count == 0) break;
            var done = await EmbedBatchAsync(binding, index, pending, cancellation).ConfigureAwait(false);
            embedded += done.Embedded;
            failed += done.Failed;
            foreach (var chunk in done.Bad) tried.Add((chunk.ItemId, chunk.Seq));
            if (done.Problem is not null) { problem = done.Problem; break; }
        }

        vectors.Sweep(Channel);
        var switched = false;
        if (problem is null && !index.IsReady) switched = vectors.Complete(Channel);
        return new EmbedRun(embedded, failed, vectors.Remaining(Channel, index), switched, problem);
    }

    private async Task<(int Embedded, int Failed, string? Problem, IReadOnlyList<PendingChunk> Bad)> EmbedBatchAsync(EmbeddingBinding binding, VectorIndexInfo index, IReadOnlyList<PendingChunk> chunks, CancellationToken cancellation)
    {
        IReadOnlyList<float[]>? made = null;
        try { made = await Generate(binding, chunks, cancellation).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _ = ex; }

        if (made is not null)
        {
            if (made.Any(v => v.Length != index.Dimension))
                return (0, 0, KnowledgeVectorStore.DimensionMismatch(Channel, index.ModelId, index.Dimension, made.First(v => v.Length != index.Dimension).Length), []);
            vectors.Put(Channel, index, [.. chunks.Select((c, i) => new ChunkVector(c.ItemId, c.Seq, c.Hash, made[i]))]);
            return (chunks.Count, 0, null, []);
        }

        // the batch failed: one at a time, to find the chunk that is the trouble (a text the model refuses) and to tell it from a provider that is down
        var good = new List<ChunkVector>();
        var bad = new List<(PendingChunk Chunk, string Message)>();
        foreach (var chunk in chunks)
        {
            try
            {
                var one = (await Generate(binding, [chunk], cancellation).ConfigureAwait(false))[0];
                if (one.Length != index.Dimension) return (0, 0, KnowledgeVectorStore.DimensionMismatch(Channel, index.ModelId, index.Dimension, one.Length), []);
                good.Add(new ChunkVector(chunk.ItemId, chunk.Seq, chunk.Hash, one));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { bad.Add((chunk, ex.Message)); }
        }
        if (good.Count > 0) vectors.Put(Channel, index, good);
        // nothing of the batch worked: the provider is the trouble, not the chunks; say why without using up the chunks' attempts
        var providerDown = good.Count == 0 && !await ProviderAnswers(binding, cancellation).ConfigureAwait(false);
        foreach (var (chunk, message) in bad) vectors.Fail(Channel, index.ModelId, chunk.ItemId, chunk.Seq, message, counts: !providerDown);
        return (good.Count, bad.Count, providerDown ? bad[0].Message : null, [.. bad.Select(b => b.Chunk)]);
    }

    /// <summary>Whether the provider embeds a sample text: when a batch failed and every chunk of it failed alone, this tells the chunk's own trouble from a provider that is down.</summary>
    private static async Task<bool> ProviderAnswers(EmbeddingBinding binding, CancellationToken cancellation)
    {
        try { return (await binding.Generator.GenerateAsync([Probe], cancellationToken: cancellation).ConfigureAwait(false)).Count == 1; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private static async Task<IReadOnlyList<float[]>> Generate(EmbeddingBinding binding, IReadOnlyList<PendingChunk> chunks, CancellationToken cancellation)
    {
        var result = await binding.Generator.GenerateAsync(chunks.Select(c => c.ToEmbed), cancellationToken: cancellation).ConfigureAwait(false);
        if (result.Count != chunks.Count) throw new InvalidOperationException($"The embedding model gave {result.Count} vectors for {chunks.Count} texts.");
        return [.. result.Select(e => e.Vector.ToArray())];
    }
}

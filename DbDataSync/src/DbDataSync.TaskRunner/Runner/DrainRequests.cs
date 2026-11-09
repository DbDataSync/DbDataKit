using DbDataSync.Core.Config;
using DbDataKit.Runner.Kit;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>Applies the <c>requests</c> input of a drain: each request becomes a row of the drain's queue (a bulk load also the record of its batch) before the first cycle.</summary>
public static class DrainRequests
{
    internal static async Task ApplyAsync(string? text, string replication, IDrainQueue queue, IDrainCheckpoints checkpoints, IDrainLedger ledger, ILogger log, CancellationToken ct)
    {
        var requests = DrainRequestText.Parse(text);
        var applied = 0;
        foreach (var request in requests)
            if (await ApplyOneAsync(request, replication, queue, checkpoints, ledger, log, ct).ConfigureAwait(false)) applied++;
        if (applied > 0)
            log.LogInformation("{Count} request(s) of the API were queued before the first cycle.", applied);
    }

    /// <summary>A request that came as a work item of the queue: applied, and the item finished (a request that cannot be applied fails the item and says why in the log).</summary>
    internal static async Task ApplyItemAsync(RunnerWorkItem item, string replication, IDrainQueue queue, IDrainCheckpoints checkpoints, IDrainLedger ledger, ILogger log, CancellationToken ct)
    {
        await queue.StartAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ApplyOneAsync(DrainRequestText.ParseOne(item.Payload), replication, queue, checkpoints, ledger, log, ct).ConfigureAwait(false);
            await queue.FinishAsync(item.Id, WorkFinish.Done, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning("The request of the API for '{Mapping}' could not be applied: {Message}", item.Subject, ex.Message);
            await queue.FinishAsync(item.Id, WorkFinish.Failed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Applies one request, once per id: false when it was applied already (a retried run reads its input again).</summary>
    internal static async Task<bool> ApplyOneAsync(DrainRequest request, string replication, IDrainQueue queue, IDrainCheckpoints checkpoints, IDrainLedger ledger, ILogger log, CancellationToken ct)
    {
        if (request.Id is { Length: > 0 } id && ledger.WasApplied(id))
        {
            log.LogInformation("The request {Id} ({Kind} of '{Mapping}') was applied already: not applied again.", id, request.Kind, request.Mapping);
            return false;
        }

        await ApplyAsyncCore(request, replication, queue, checkpoints, ledger, log, ct).ConfigureAwait(false);
        if (request.Id is { Length: > 0 } mark) ledger.TryMarkApplied(mark);
        return true;
    }

    private static async Task ApplyAsyncCore(DrainRequest request, string replication, IDrainQueue queue, IDrainCheckpoints checkpoints, IDrainLedger ledger, ILogger log, CancellationToken ct)
    {
        {
            if (request.Kind == "Position")
            {
                if (string.IsNullOrEmpty(request.SourceKey) || string.IsNullOrEmpty(request.Position))
                    throw new InvalidOperationException($"The position request of '{request.Mapping}' has no source key or no position.");
                // a position the mapping has is the run's own progress and wins: the seed is only for the mapping that has none
                if (checkpoints.Read(request.SourceKey) is null)
                {
                    checkpoints.Write(request.SourceKey, request.Position, request.PositionTime);
                    if (Enum.TryParse<ReadIntent>(request.Intent, out var intent)) checkpoints.WriteIntent(request.SourceKey, intent);
                    if (Enum.TryParse<ReadHold>(request.Hold, out var hold)) checkpoints.WriteHold(request.SourceKey, hold);
                    log.LogInformation("'{Mapping}' starts from the position the API handed over.", request.Mapping);
                }

                return;
            }

            if (request.Kind == "Resync")
            {
                if (string.IsNullOrEmpty(request.SourceKey))
                    throw new InvalidOperationException($"The resync request of '{request.Mapping}' has no source key.");
                checkpoints.WriteIntent(request.SourceKey, ReadIntent.InitialLoad);
                checkpoints.WriteHold(request.SourceKey, ReadHold.None);
                await EnqueueAsync(queue, replication, new DrainRequest(nameof(RunKind.Primary), request.Mapping), ct).ConfigureAwait(false);
                log.LogInformation("Resync of '{Mapping}': its next pass is an initial load.", request.Mapping);
                return;
            }

            if (request.Kind == nameof(RunKind.BulkLoad) && request.BatchId is { Length: > 0 } batchId && ledger.ReadBatch(batchId) is null)
                ledger.WriteBatch(batchId, new BatchRecord(request.Mapping, "", request.BatchSegments ?? 1, 0, 0, 0, 0, request.EstimatedRows, request.EstimateCaveat, DateTimeOffset.UtcNow));
            await EnqueueAsync(queue, replication, request, ct).ConfigureAwait(false);
        }
    }

    private static Task<Guid> EnqueueAsync(IDrainQueue queue, string replication, DrainRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<RunKind>(request.Kind, out var kind))
            throw new InvalidOperationException($"The request for '{request.Mapping}' names the run kind `{request.Kind}`, which is none of {string.Join(", ", Enum.GetNames<RunKind>())} (or Resync).");
        var shape = WorkQueueStore.Describe(
            replication, kind, request.Mapping, request.Label ?? WorkQueueStore.NoSegment, request.SegmentJson, new WorkItemKinds(request.ReaderKind, request.CacheKind, request.WriterKind),
            request.DeleteGuardJson, request.OneAtATime ? SegmentOrder.OneAtATime : SegmentOrder.Together, request.BatchId, request.BatchSegments);
        return queue.EnqueueAsync(shape.Kind, shape.Subject, shape.Part, shape.Resource, shape.Lane, shape.Payload, ct);
    }
}

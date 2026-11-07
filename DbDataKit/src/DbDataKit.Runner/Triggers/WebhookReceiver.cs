using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner.Triggers;

/// <summary>One task a webhook was delivered to: the inbox item that holds it and whether the task was queued by the arrival (false when it was queued or running already, paused, or is served by another runner: it is queued at the next tick).</summary>
public sealed record WebhookDelivery(TaskRef Task, Guid ItemId, bool SignatureOk, bool Queued);

/// <summary>What became of a webhook: the tasks it was delivered to, none when no task of the package and channel has a webhook trigger for the source.</summary>
public sealed record WebhookReceipt(IReadOnlyList<WebhookDelivery> Deliveries)
{
    /// <summary>True when at least one task listens for the source.</summary>
    public bool Subscribed => Deliveries.Count > 0;
}

/// <summary>
/// The runner's end of a webhook (plan 28 section 7, plan 28a R13). The serving layer (or the runner's own endpoint, <c>POST /api/v1/webhooks/{package}/{channel}/{source}</c>) has checked the signature and hands the body over; the
/// receiver stores it as a payload owned by an inbox item of every task whose manifest has a webhook trigger for the source, and queues the task. A task takes its items with <c>Inbox&lt;T&gt;().TakeAsync</c>, each to exactly one run.
/// A body whose signature did not hold is stored too, as a record with <c>signature_ok</c> false, set aside so no run takes it, and nothing is queued.
/// </summary>
public interface IWebhookReceiver
{
    Task<WebhookReceipt> ReceiveAsync(string package, string channel, string source, bool signatureOk, ReadOnlyMemory<byte> body, CancellationToken ct = default);
}

/// <summary>The receiver over the runner's state: finds the subscribing tasks in the projection, stores body and item, and asks the engine to queue (when this process has one).</summary>
public sealed class WebhookReceiver(StateDatabase database, TriggerEngine? engine = null) : IWebhookReceiver
{
    private readonly TaskProjections _projections = new(database);
    private readonly PayloadStore _payloads = new(database);

    /// <summary>The tasks of a package and channel with a webhook trigger for <paramref name="source"/>.</summary>
    public IReadOnlyList<TaskRef> Subscribers(string package, string channel, string source)
    {
        var tasks = new List<TaskRef>();
        foreach (var row in _projections.ListByChannel(channel, package))
        {
            IReadOnlyList<TaskTrigger> triggers;
            try { triggers = ProjectionDocuments.ParseTriggers(row.Triggers); }
            catch (FormatException) { continue; }    // the scheduler reports a row it cannot read
            if (triggers.Any(t => t.Kind == TriggerKind.Webhook && t.Source == source)) tasks.Add(new TaskRef(row.Package, row.Channel, row.Task));
        }

        return tasks;
    }

    public Task<WebhookReceipt> ReceiveAsync(string package, string channel, string source, bool signatureOk, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        ReceiveAsync(package, channel, source, _ => signatureOk, body, ct);

    /// <summary>The same where the signature is judged per task (each task's trigger may name its own secret).</summary>
    public async Task<WebhookReceipt> ReceiveAsync(string package, string channel, string source, Func<TaskRef, bool> signatureOkFor, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (TaskRef.Problem(package, channel, "task") is { } problem) throw new ArgumentException(problem);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var deliveries = new List<WebhookDelivery>();
        foreach (var task in Subscribers(package, channel, source))
        {
            ct.ThrowIfCancellationRequested();
            var id = database.NewId();
            Guid payloadId;
            using (var writer = _payloads.OpenWrite(new PayloadOptions("application/octet-stream", ProgressModule.Name, id.ToString("D"), "webhook")))
            {
                payloadId = writer.Id;
                await writer.WriteAsync(body, ct).ConfigureAwait(false);
                writer.Complete();
            }

            var progress = new ProgressStore(database, new ProgressKey(task.Package, task.Channel, task.Task));
            var signatureOk = signatureOkFor(task);
            // a refused body is stored already set aside, in the one insert: stored as waiting first, the trigger engine's tick could queue a run for it
            progress.Receive(source, signatureOk, id, payloadId, signatureOk ? null : ProgressStore.RefusedBySignature);
            var queued = false;
            if (signatureOk) queued = engine?.TriggerWebhook(task) ?? false;
            deliveries.Add(new WebhookDelivery(task, id, signatureOk, queued));
        }

        return new WebhookReceipt(deliveries);
    }
}

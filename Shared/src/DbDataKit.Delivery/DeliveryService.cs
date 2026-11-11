using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Delivery;

/// <summary>
/// The delivery service (plan 22a GP07): <see cref="SendAsync"/> writes a message to the outbox (the state module <c>delivery</c>) and returns; a sender loop (<see cref="RunAsync"/>, or <see cref="RunOnceAsync"/> from a task) takes messages under a lease,
/// hands each to its channel and records the try.
/// <list type="bullet">
/// <item><b>Taken once.</b> A message is claimed by a guarded update, so two senders on one database never hold the same message; a sender that dies loses it when the lease runs out and another takes it.</item>
/// <item><b>Temporary or permanent.</b> A channel says which (<see cref="DeliveryOutcome"/>). A temporary failure is tried again after the policy's back-off until the attempt limit, then the message is parked as <c>dead</c>; a permanent failure parks it at once with the channel's words. A channel that throws is a temporary failure (outcome <c>error</c>).</item>
/// <item><b>A recorded outcome.</b> Every try is a row (<c>attempt</c>) written in the same transaction as the message's new state; a person sees what the far side said.</item>
/// <item><b>No secret is written.</b> The detail of a try and every log line pass the <see cref="SecretScrubber"/>; the message content is logged by id and size only.</item>
/// <item><b>A removed channel</b> parks the messages for it with that reason (a subscription reports the problem); <b>a limit per minute</b> holds a message back without counting a try.</item>
/// </list>
/// </summary>
public sealed class DeliveryService(DeliveryStore store, PayloadStore payloads, IDeliveryChannels channels, DeliveryPolicy policy, SecretScrubber scrubber, TimeProvider? time = null, ILogger<DeliveryService>? log = null, string? senderId = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _sentInWindow = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Who this sender is in the outbox (<c>lease_owner</c> and each try's <c>sender</c>).</summary>
    public string SenderId { get; } = senderId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";

    public DeliveryPolicy Policy => policy;

    public DeliveryStore Store => store;

    public IDeliveryChannels Channels => channels;

    /// <summary>Writes the message to the outbox, its attachments to payloads that the message row owns. <see cref="UnknownChannelException"/> when the channel is not configured: a caller that is told cannot report it as sent.</summary>
    public Task<Guid> SendAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var release = string.IsNullOrWhiteSpace(request.ReleaseChannel) ? DbDataKit.State.DefinitionStore.Production : request.ReleaseChannel.Trim();
        if (!channels.Names.Contains(request.Channel, StringComparer.OrdinalIgnoreCase) && channels.Find(request.Channel) is null) throw new UnknownChannelException(request.Channel, channels.Names);
        // a channel that is not bound in this release channel is refused here; one whose connection does not read yet is written and fails when it is sent (fixing the connection makes the next try work)
        if (channels.Resolve(request.Channel, release) is { Channel: null, Retryable: false } unbound) throw new ChannelNotBoundException(request.Channel, release, unbound.Problem ?? "not bound");
        var id = Guid.CreateVersion7(_time.GetUtcNow());
        var refs = new List<AttachmentRef>();
        foreach (var attachment in request.Attachments ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var writer = payloads.OpenWrite(new PayloadOptions(attachment.MediaType, DeliveryModule.Name, id.ToString("D"), request.OwnerPerson, Label: attachment.Name));
            writer.Write(attachment.Content);
            writer.Complete();
            refs.Add(new AttachmentRef(writer.Id, attachment.Name, attachment.MediaType, attachment.Content.LongLength));
        }

        store.Enqueue(new NewMessage(request.Channel, request.Subject, request.Recipients, request.Text, request.Html, request.Link, refs, request.Source, request.OwnerPerson, id, request.NotBefore, release == DbDataKit.State.DefinitionStore.Production ? null : release));
        _log.LogInformation("delivery: message {Message} for channel {Channel} ({Release}) written ({Attachments} attachments, {Bytes} bytes)", id, request.Channel, release, refs.Count, refs.Sum(a => a.Size));
        return Task.FromResult(id);
    }

    /// <summary>Takes what is due and tries it, once through. The count of messages it handled (tries made, parked, held back).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default, int batch = 10)
    {
        var handled = 0;
        foreach (var message in store.Claim(SenderId, policy.Lease, batch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryAsync(message, cancellationToken).ConfigureAwait(false);
            handled++;
        }

        return handled;
    }

    /// <summary>The sender loop: handle what is due, wait <see cref="DeliveryPolicy.Idle"/> when nothing is, until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int handled;
            try { handled = await RunOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                _log.LogWarning("delivery: a round failed: {Error}", scrubber.Scrub(e.Message));
                handled = 0;
            }

            if (handled == 0)
                try { await Task.Delay(policy.Idle, _time, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
        }
    }

    private async Task TryAsync(MessageRow message, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var resolution = channels.Resolve(message.Channel, message.ReleaseChannel);
        var channel = resolution.Channel;
        if (channel is null)
        {
            var problem = resolution.Problem ?? $"The delivery channel '{message.Channel}' is not configured any more; the message was not sent. Add the channel again and send it again.";
            if (resolution.Retryable) Settle(message, AttemptOutcomes.Temporary, problem, started, final: message.Attempts + 1 >= policy.MaxAttempts);
            else Settle(message, AttemptOutcomes.Permanent, problem, started, final: true);
            return;
        }

        if (HeldBack(channel, started) is { } until)
        {
            store.Release(message.Id, SenderId, until);
            _log.LogInformation("delivery: message {Message} waits for channel {Channel} (limit per minute)", message.Id, channel.Name);
            return;
        }

        List<DeliveryAttachment> attachments;
        try { attachments = LoadAttachments(message); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or InvalidDataException)
        {
            Settle(message, AttemptOutcomes.Permanent, "An attachment of the message is gone from the outbox: " + e.Message, started, final: true);
            return;
        }

        DeliveryResult result;
        string outcome;
        try
        {
            result = await channel.SendAsync(new DeliveryPayload(message.Id, channel.Name, message.Recipients, message.Subject, message.BodyText, message.BodyHtml, message.Link, attachments, message.Attempts + 1), cancellationToken).ConfigureAwait(false);
            outcome = result.Outcome switch { DeliveryOutcome.Sent => AttemptOutcomes.Sent, DeliveryOutcome.Permanent => AttemptOutcomes.Permanent, _ => AttemptOutcomes.Temporary };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;                                                    // not tried: the lease runs out and another sender takes it
        }
        catch (ChannelUnavailableException e)
        {
            (result, outcome) = (DeliveryResult.Temporary(e.Message), AttemptOutcomes.Temporary);
        }
        catch (Exception e)
        {
            (result, outcome) = (DeliveryResult.Temporary($"{e.GetType().Name}: {e.Message}"), AttemptOutcomes.Error);
        }

        if (outcome == AttemptOutcomes.Sent) NoteSent(channel, started);
        Settle(message, outcome, result.Detail, started, final: outcome == AttemptOutcomes.Permanent || (outcome != AttemptOutcomes.Sent && message.Attempts + 1 >= policy.MaxAttempts));
    }

    private void Settle(MessageRow message, string outcome, string? detail, DateTimeOffset started, bool final)
    {
        var words = scrubber.Scrub(detail);
        DateTimeOffset? retryAt = outcome == AttemptOutcomes.Sent || final ? null : _time.GetUtcNow() + policy.WaitAfter(message.Attempts + 1);
        if (final && outcome != AttemptOutcomes.Sent && outcome != AttemptOutcomes.Permanent) words = $"Gave up after {message.Attempts + 1} tries. {words}";
        var recorded = store.Complete(message.Id, SenderId, outcome, words, started, retryAt);
        _log.LogInformation("delivery: message {Message} channel {Channel} try {Try}: {Outcome}{Parked}{Lost} {Detail}", message.Id, message.Channel, message.Attempts + 1, outcome,
            final && outcome != AttemptOutcomes.Sent ? " (parked)" : "", recorded ? "" : " (the lease was lost: not recorded)", Scrub(words));
    }

    private string Scrub(string text) => scrubber.Scrub(text.Length > 200 ? text[..200] : text);

    private List<DeliveryAttachment> LoadAttachments(MessageRow message)
    {
        var list = new List<DeliveryAttachment>();
        foreach (var attachment in message.Attachments)
        {
            using var reader = payloads.OpenRead(attachment.Payload);
            using var memory = new MemoryStream();
            reader.CopyTo(memory);
            list.Add(new DeliveryAttachment(attachment.Name, attachment.MediaType, memory.ToArray()));
        }

        return list;
    }

    // ---- the limit per minute (per sender process) ----

    private DateTimeOffset? HeldBack(IDeliveryChannel channel, DateTimeOffset now)
    {
        if (channel.LimitPerMinute is not { } limit) return null;
        lock (_gate)
        {
            var key = channel.Connection ?? channel.Name;
            if (!_sentInWindow.TryGetValue(key, out var window)) _sentInWindow[key] = window = new Queue<DateTimeOffset>();
            while (window.Count > 0 && now - window.Peek() >= TimeSpan.FromMinutes(1)) window.Dequeue();
            return window.Count >= limit ? window.Peek() + TimeSpan.FromMinutes(1) : null;
        }
    }

    private void NoteSent(IDeliveryChannel channel, DateTimeOffset at)
    {
        if (channel.LimitPerMinute is null) return;
        lock (_gate)
        {
            var key = channel.Connection ?? channel.Name;
            if (!_sentInWindow.TryGetValue(key, out var window)) _sentInWindow[key] = window = new Queue<DateTimeOffset>();
            window.Enqueue(at);
        }
    }
}

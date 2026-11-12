using System.Net;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Delivery;

/// <summary>Who a subscriber is to the delivery service: how to address them and whether they still exist. The host answers from the identity home (the Gateway's <c>HomeCallers</c>, the console's users).</summary>
/// <param name="Person">The subject id.</param>
/// <param name="DisplayName">The name shown in messages.</param>
/// <param name="Email">Their address; null when the identity has none.</param>
/// <param name="Active">False for an identity that is disabled or removed.</param>
public sealed record SubscriberContact(string Person, string DisplayName, string? Email, bool Active = true);

public interface ISubscriberDirectory
{
    /// <summary>The person, or null when the identity home does not know them any more.</summary>
    Task<SubscriberContact?> FindAsync(string person, CancellationToken cancellationToken);
}

/// <summary>What a subscription run asks the renderer for: the document, its parameters, the layout and format, and <b>who</b> it is rendered as.</summary>
public sealed record SubscriptionRender(Guid SubscriptionId, string Subscriber, string Kind, string View, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> Parameters, string? Layout, string Format, string? Widget, bool InlinePreview = false);

/// <summary>What the renderer gave back.</summary>
public abstract record RenderResult
{
    /// <summary>The document was rendered. <paramref name="Notes"/> are things a person should know (a widget that failed and is shown as an error block, a font that was missing). <paramref name="Preview"/> is a PNG of the document when the request asked for one (<see cref="SubscriptionRender.InlinePreview"/>) and the renderer could make it.</summary>
    public sealed record Rendered(byte[] Content, string MediaType, string FileName, string Title, string? Summary, string? Link, IReadOnlyList<string> Notes, byte[]? Preview = null) : RenderResult;

    /// <summary>The subscriber may not see this any more (the document refuses them, or they are no longer in the group that grants it). Nothing is sent; the subscription stops and says why.</summary>
    public sealed record AccessLost(string Reason) : RenderResult;

    /// <summary>It could not be rendered (the host has no browser, the document is gone, a deadline passed). Nothing is sent; the owner is told; the subscription stays and tries at its next time.</summary>
    public sealed record Failed(string Code, string Reason) : RenderResult;
}

/// <summary>
/// Renders a document <b>as the subscriber</b> (plan 22a GP17, plan 32 DBR18): with the subscriber's own data permissions, never the runner's. The Gateway's views module is the real one (<c>DbDataKit.Views.Hosting</c>'s <c>SubscriptionRenderer</c>:
/// <c>ViewsApi.ExportAsync</c> under a principal made from the subscriber's identity); a test gives one with rows that differ by person.
/// </summary>
public interface ISubscriptionRenderer
{
    Task<RenderResult> RenderAsync(SubscriptionRender request, CancellationToken cancellationToken);
}

/// <summary>What one run of one subscription did.</summary>
public sealed record SubscriptionRunResult(Guid SubscriptionId, string Name, string Outcome, string Detail, Guid? MessageId, string? Code = null);

/// <summary>
/// Runs subscriptions (plan 22a GP17, DBR18's half that is not a runner): the due ones (<see cref="RunDueAsync"/>, called from the runner's task every minute) or one on request (<see cref="RunNowAsync"/>).
/// <list type="bullet">
/// <item><b>As the subscriber.</b> The renderer is asked for the document as the subscriber; two subscribers of one view with different rules get different attachments, and the outbox shows both.</item>
/// <item><b>A subscriber who lost access</b> (the module refuses, every widget is denied, or the identity home no longer knows them or the identity is disabled) <b>gets nothing</b> (owner decision 2026-11-11): the subscription is <c>stopped</c> with the reason, the run is recorded
/// as <c>access-lost</c> ("lost access") in the subscription's log, <b>an administrator is notified</b> through the install's notification feed (<c>SubscriptionAccessLost</c>, shown on the console), and a message tells the subscriber and the owner on the subscription's channel.</item>
/// <item><b>A render that fails</b> sends nothing and tells the owner; the subscription stays active and tries at its next time.</item>
/// <item><b>A removed channel</b> is a problem the subscription shows in its status (it stays active; the run is <c>failed</c>), so a person fixing the settings sees what was waiting.</item>
/// <item><b>The schedule</b> is read in the subscription's time zone (daylight saving follows it); a runner that was down runs a subscription once, not once for every missed time.</item>
/// </list>
/// </summary>
public sealed class SubscriptionService(SubscriptionStore store, DeliveryService delivery, ISubscriptionRenderer renderer, ISubscriberDirectory directory, TimeProvider? time = null, ILogger<SubscriptionService>? log = null, DbDataKit.Monitoring.INotificationFeed? notifications = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>The kind of notification raised for an administrator when a subscriber lost access.</summary>
    public const string AccessLostNotification = "SubscriptionAccessLost";

    public SubscriptionStore Store => store;

    /// <summary>Takes the subscriptions that are due and runs each. The results in the order they ran.</summary>
    public async Task<IReadOnlyList<SubscriptionRunResult>> RunDueAsync(CancellationToken cancellationToken, string? runId = null, int max = 50)
    {
        var now = _time.GetUtcNow();
        var due = store.ClaimDue(now, row => SubscriptionSpec.Next(row.Fields.Cron, row.Fields.TimeZone, now), max);
        var results = new List<SubscriptionRunResult>();
        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunAsync(row, runId, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Runs one subscription now (a person pressed "send now"), whatever its schedule says and whatever its status: a stopped subscription can be tried again to see whether access is back.</summary>
    public async Task<SubscriptionRunResult> RunNowAsync(Guid id, CancellationToken cancellationToken, string? runId = null)
    {
        var row = store.Find(id) ?? throw new KeyNotFoundException($"There is no subscription {id}.");
        return await RunAsync(row, runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubscriptionRunResult> RunAsync(SubscriptionRow row, string? runId, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        var f = row.Fields;
        var label = $"'{f.Name}' ({f.Kind} {f.ViewName})";

        SubscriptionRunResult Done(string outcome, string detail, Guid? message = null, string? problem = null, string? code = null)
        {
            store.RecordRun(row.Id, started, outcome, detail, message, runId, problem);
            _log.LogInformation("subscription {Subscription} {Name}: {Outcome} {Detail}", row.Id, f.Name, outcome, detail);
            return new SubscriptionRunResult(row.Id, f.Name, outcome, detail, message, code);
        }

        var channel = delivery.Channels.Find(f.Channel);
        if (channel is null)
        {
            var problem = $"The delivery channel '{f.Channel}' is not configured (the delivery: section has {(delivery.Channels.Names.Count == 0 ? "no channels" : string.Join(", ", delivery.Channels.Names))}); nothing was sent. Add the channel or change the subscription's channel.";
            return Done(SubscriptionOutcomes.Failed, problem, problem: problem);
        }

        var person = await directory.FindAsync(row.Person, ct).ConfigureAwait(false);
        if (person is not { Active: true })
            return await StopAsync(row, channel, started, person, $"{(row.PersonName ?? row.Person)} is not known to the identity home any more, or the identity is disabled.", runId, ct).ConfigureAwait(false);

        var recipient = f.Recipient ?? person.Email;
        if (recipient is null && channel.Kind == "email")
            return Done(SubscriptionOutcomes.Failed, $"{person.DisplayName} has no email address and the subscription names none; nothing was sent.", problem: "There is no address to send to: set the recipient or give the identity an email address.");

        var rendered = await renderer.RenderAsync(new SubscriptionRender(row.Id, row.Person, f.Kind, f.ViewName, f.Parameters is null ? new Dictionary<string, System.Text.Json.Nodes.JsonNode?>() : f.Parameters.ToDictionary(p => p.Key, p => p.Value?.DeepClone()), f.Layout, f.ExportFormat, f.Widget, f.InlinePreview), ct).ConfigureAwait(false);
        switch (rendered)
        {
            case RenderResult.AccessLost lost:
                return await StopAsync(row, channel, started, person, lost.Reason, runId, ct).ConfigureAwait(false);
            case RenderResult.Failed failed:
                await TellAsync(row, channel, [row.Owner], $"Subscription {label} could not be rendered", $"The {f.Kind} {f.ViewName} could not be drawn for {person.DisplayName}: {failed.Reason} ({failed.Code}). Nothing was sent. The subscription is still active and will try again at its next time ({f.Cron}, {f.TimeZone}).", ct).ConfigureAwait(false);
                return Done(SubscriptionOutcomes.Failed, $"{failed.Reason} ({failed.Code})", problem: failed.Code == "unavailable" ? failed.Reason : null, code: failed.Code);
            case RenderResult.Rendered ok:
                var notes = ok.Notes.Count == 0 ? "" : "\n\n" + string.Join("\n", ok.Notes.Select(n => "Note: " + n));
                var text = $"{ok.Summary ?? $"The {f.Kind} {f.ViewName} is attached."}{notes}\n\nYou receive this because you subscribed to '{f.Name}' ({f.Cron}, {f.TimeZone}).";
                var attachments = new List<DeliveryAttachment> { new(ok.FileName, ok.MediaType, ok.Content) };
                var pictureHtml = "";
                // the picture goes into an email's body (a content id the HTML names); Teams, Slack and the webhook cannot hold a file and show the link to the live view instead
                if (ok.Preview is { Length: > 0 } picture && channel.Kind == "email")
                {
                    const string pictureId = "preview@dbdatakit";
                    attachments.Add(new DeliveryAttachment("preview.png", "image/png", picture, pictureId));
                    pictureHtml = $"<p><img src=\"cid:{pictureId}\" alt=\"{WebUtility.HtmlEncode(ok.Title)}\" style=\"max-width:100%;border:1px solid #ddd\"></p>";
                }

                var html = $"<p>{WebUtility.HtmlEncode(ok.Summary ?? $"The {f.Kind} {f.ViewName} is attached.")}</p>{(ok.Notes.Count == 0 ? "" : "<ul>" + string.Concat(ok.Notes.Select(n => "<li>" + WebUtility.HtmlEncode(n) + "</li>")) + "</ul>")}{pictureHtml}" +
                           $"<p style=\"color:#666\">You receive this because you subscribed to '{WebUtility.HtmlEncode(f.Name)}' ({WebUtility.HtmlEncode(f.Cron)}, {WebUtility.HtmlEncode(f.TimeZone)}).</p>";
                var id = await delivery.SendAsync(new DeliveryRequest(f.Channel, ok.Title, recipient is null ? null : [recipient], text, html, ok.Link, attachments, $"subscription:{row.Id:D}", row.Person), ct).ConfigureAwait(false);
                return Done(SubscriptionOutcomes.Sent, $"{ok.FileName} ({ok.Content.Length} bytes) is in the outbox for {recipient ?? channel.Name}." + (ok.Notes.Count == 0 ? "" : " " + string.Join(" ", ok.Notes)), id);
            default:
                throw new InvalidOperationException("an unknown render result");
        }
    }

    private async Task<SubscriptionRunResult> StopAsync(SubscriptionRow row, IDeliveryChannel channel, DateTimeOffset started, SubscriberContact? person, string reason, string? runId, CancellationToken ct)
    {
        var f = row.Fields;
        var detail = $"Stopped: {reason}";
        store.SetStatus(row.Id, "stopped", detail, null);
        var tell = new List<string> { row.Person };
        if (!string.Equals(row.Owner, row.Person, StringComparison.Ordinal)) tell.Add(row.Owner);
        await TellAsync(row, channel, tell, $"Subscription '{f.Name}' was stopped", $"The subscription '{f.Name}' to the {f.Kind} {f.ViewName} was stopped and nothing was sent: {reason} Ask for access again and set the subscription going from the subscriptions page.", ct).ConfigureAwait(false);
        store.RecordRun(row.Id, started, SubscriptionOutcomes.AccessLost, detail, null, runId);
        notifications?.Raise(AccessLostNotification, $"Lost access: the subscription '{f.Name}' of {row.PersonName ?? row.Person} to the {f.Kind} {f.ViewName} was stopped and nothing was delivered. {reason}", mappingName: f.Name);
        _log.LogInformation("subscription {Subscription} {Name}: stopped: {Reason}", row.Id, f.Name, reason);
        return new SubscriptionRunResult(row.Id, f.Name, SubscriptionOutcomes.AccessLost, detail, null);
    }

    /// <summary>A notice to people (the subscriber, the owner) on the subscription's channel, to the address the identity home gives each. A person with no address, or a channel that takes no address (a webhook gets one message for all), is not left out silently: the run's record says what happened.</summary>
    private async Task TellAsync(SubscriptionRow row, IDeliveryChannel channel, IReadOnlyList<string> people, string subject, string text, CancellationToken ct)
    {
        var addresses = new List<string>();
        foreach (var person in people.Distinct(StringComparer.Ordinal))
            if (await directory.FindAsync(person, ct).ConfigureAwait(false) is { Email: { } email }) addresses.Add(email);
        if (addresses.Count == 0 && channel.Kind == "email") return;
        try
        {
            await delivery.SendAsync(new DeliveryRequest(channel.Name, subject, addresses, text, $"<p>{WebUtility.HtmlEncode(text)}</p>", Source: $"subscription:{row.Id:D}", OwnerPerson: row.Owner), ct).ConfigureAwait(false);
        }
        catch (UnknownChannelException)
        {
            // the channel went between the check and now; the run's record carries the problem
        }
    }
}

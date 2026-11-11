using System.Text.Json.Nodes;
using DbDataKit.Delivery;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>A test message: where it goes (the signed-in person's address when empty; a webhook channel ignores it) and through which release channel's connection (default <c>prod</c>).</summary>
public sealed record TestSendBody(string? Recipient = null, string? Channel = null);

/// <summary>
/// Delivery in the console (plan 22a GP18, folded into connections): the channels of the install's <c>delivery:</c> section with, per release channel, the connection each uses and whether it can be used, and a test send through a chosen release channel's connection; the outbox with its tries and what failed; and subscriptions (a person's own, or everyone's for an administrator).
/// <b>Nobody reads a message's words but who it is for or an administrator</b>; the outbox rows a person sees are their own.
/// </summary>
[ApiController]
[Route("api/delivery")]
public sealed class DeliveryController(ConsoleDelivery delivery) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    private IActionResult Refuse(int status, string code, string message) => StatusCode(status, new { error = message, code });

    private static object View(MessageRow m) => new
    {
        id = m.Id, channel = m.Channel, releaseChannel = m.Release, subject = m.Subject, recipients = m.Recipients, source = m.Source, ownerPerson = m.OwnerPerson, status = m.Status, attempts = m.Attempts, nextAttemptAt = m.NextAttemptAt,
        lastOutcome = m.LastOutcome, lastDetail = m.LastDetail, createdAt = m.CreatedAt, settledAt = m.SettledAt, attachments = m.Attachments.Select(a => new { a.Name, a.MediaType, a.Size }),
    };

    private static object View(AttemptRow a) => new { a.Number, a.Sender, a.Outcome, a.Detail, a.StartedAt, a.FinishedAt };

    private object View(SubscriptionRow s) => new
    {
        id = s.Id, name = s.Fields.Name, kind = s.Fields.Kind, view = s.Fields.ViewName, parameters = s.Fields.Parameters, layout = s.Fields.Layout, format = s.Fields.ExportFormat, widget = s.Fields.Widget, channel = s.Fields.Channel,
        recipient = s.Fields.Recipient, schedule = new { cron = s.Fields.Cron, timeZone = s.Fields.TimeZone }, person = s.Person, personName = s.PersonName ?? delivery.Users.Get(s.Person)?.DisplayName ?? s.Person, owner = s.Owner,
        status = s.Status, statusDetail = s.StatusDetail, nextRunAt = s.NextRunAt, lastRunAt = s.LastRunAt, lastOutcome = s.LastOutcome, lastDetail = s.LastDetail, version = s.Version,
        channelProblem = delivery.Channels.Find(s.Fields.Channel) is null ? $"The channel '{s.Fields.Channel}' is not configured." : null,
    };

    /// <summary>What the install has: the channels with their readiness, the outbox counts, the retry policy, and whether this console can draw a subscription's document.</summary>
    [HttpGet]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var channels = await delivery.ChannelViewsAsync(ct);
        return Ok(new
        {
            problem = delivery.Problem,
            canRender = delivery.CanRender,
            renderNote = delivery.CanRender ? null : ConsoleNoRenderer.Words,
            isAdmin = IsAdmin,
            policy = new { maxAttempts = delivery.Settings.Policy.MaxAttempts, backoffSeconds = delivery.Settings.Policy.Backoff.Select(b => (int)b.TotalSeconds), leaseSeconds = (int)delivery.Settings.Policy.Lease.TotalSeconds },
            releaseChannels = delivery.ReleaseChannels,
            connectionKinds = new[] { new { kind = "smtp", description = "A mail server: mail channels send through it." }, new { kind = "http", description = "A webhook (Slack, Teams, a signed webhook): an `http` connection with a `webhook:` section." } },
            channels,
            outbox = delivery.Outbox.Counts(),
        });
    }

    /// <summary>
    /// Sends a clearly marked test message through the connection a channel is bound to in a release channel (<c>channel</c>, default <c>prod</c>) and works the outbox once, so the answer says what the far side said. A release channel the name is not bound in is refused
    /// naming where it is bound: a test never falls back to another release channel's connection.
    /// </summary>
    [HttpPost("channels/{name}/test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Test(string name, [FromBody] TestSendBody? body, CancellationToken ct)
    {
        if (!delivery.Channels.Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return Refuse(404, "not_found", $"There is no channel '{name}' in the delivery: section ({(delivery.Channels.Names.Count == 0 ? "it has no channels" : string.Join(", ", delivery.Channels.Names))}).");
        var release = string.IsNullOrWhiteSpace(body?.Channel) ? DefinitionStore.Production : body!.Channel!.Trim();
        var resolution = delivery.Channels.Resolve(name, release);
        if (resolution.Channel is not { } channel) return Refuse(409, resolution.Retryable ? "connection_unusable" : "not_bound", resolution.Problem ?? "The channel cannot be used.");
        var recipient = string.IsNullOrWhiteSpace(body?.Recipient) ? delivery.Users.Get(PersonId)?.Email : body!.Recipient!.Trim();
        if (channel.Kind == "email" && recipient is null) return Refuse(400, "invalid", "An email test needs an address: type one, or give your own account an email address.");
        var text = $"THIS IS A TEST MESSAGE from the DbData console, sent by {User.Identity?.Name ?? PersonId} through the channel '{channel.Name}' in the release channel '{release}' (the connection '{resolution.Connection}'). It is not an alert and nothing needs doing. If you can read it, the channel works.";
        var id = await delivery.Service.SendAsync(new DeliveryRequest(channel.Name, $"[TEST, {release}] DbData test message", recipient is null ? null : [recipient], text, $"<p><b>THIS IS A TEST MESSAGE</b></p><p>{System.Net.WebUtility.HtmlEncode(text)}</p>", Source: "test", OwnerPerson: PersonId, ReleaseChannel: release), ct);
        await delivery.Service.RunOnceAsync(ct);
        return Ok(new { id, releaseChannel = release, connection = resolution.Connection, message = View(delivery.Outbox.Find(id)!), attempts = delivery.Outbox.Attempts(id).Select(View) });
    }

    /// <summary>The outbox, newest first: an administrator sees every message, a person their own. The words of a message are never in the list.</summary>
    [HttpGet("messages")]
    [Authorize(Policies.Viewer)]
    public IActionResult Messages([FromQuery] string? status, [FromQuery] string? channel, [FromQuery] string? source, [FromQuery] int limit = 100)
    {
        if (status is not null && !DeliveryModule.MessageStatuses.Contains(status)) return Refuse(400, "invalid", $"status is one of {string.Join(", ", DeliveryModule.MessageStatuses)}.");
        var rows = delivery.Outbox.List(status, channel, source, IsAdmin ? null : PersonId, Math.Clamp(limit, 1, 500));
        return Ok(new { messages = rows.Select(View) });
    }

    /// <summary>The tries of a message: what the far side said each time.</summary>
    [HttpGet("messages/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Message(Guid id)
    {
        var row = delivery.Outbox.Find(id);
        if (row is null || (!IsAdmin && row.OwnerPerson != PersonId)) return Refuse(404, "not_found", "There is no such message.");
        return Ok(new { message = View(row), attempts = delivery.Outbox.Attempts(id).Select(View) });
    }

    /// <summary>Sends a parked message again (an administrator's act).</summary>
    [HttpPost("messages/{id:guid}/requeue")]
    [Authorize(Policies.Admin)]
    public IActionResult Requeue(Guid id) =>
        delivery.Outbox.Requeue(id) ? Ok(new { message = View(delivery.Outbox.Find(id)!) }) : Refuse(409, "not_parked", "Only a parked (dead) message can be sent again.");

    // ---- subscriptions ----

    /// <summary>The subscriptions: a person's own, or with <c>all=true</c> (an administrator) everyone's.</summary>
    [HttpGet("subscriptions")]
    [Authorize(Policies.Viewer)]
    public IActionResult Subscriptions([FromQuery] bool all = false)
    {
        if (all && !IsAdmin) return Refuse(403, "forbidden", "Only an administrator sees everyone's subscriptions.");
        return Ok(new { subscriptions = delivery.Subscriptions.List(person: all ? null : PersonId).Select(View), channels = delivery.Channels.Names, canRender = delivery.CanRender });
    }

    private (SubscriptionSpec? Spec, IActionResult? Refusal) Read(JsonObject body)
    {
        try
        {
            var spec = SubscriptionSpec.Read(body, "request");
            return delivery.Channels.Find(spec.Fields.Channel) is null
                ? (null, Refuse(400, "unknown_channel", $"The channel '{spec.Fields.Channel}' is not configured ({(delivery.Channels.Names.Count == 0 ? "the delivery: section has no channels" : "channels: " + string.Join(", ", delivery.Channels.Names))})."))
                : (spec, null);
        }
        catch (SubscriptionSpecException e)
        {
            return (null, Refuse(400, "invalid", e.Message));
        }
    }

    /// <summary>Subscribes the signed-in person (or, for an administrator, another with <c>for</c>) to a dashboard or a report.</summary>
    [HttpPost("subscriptions")]
    [Authorize(Policies.Viewer)]
    public IActionResult Create([FromBody] JsonObject body, [FromQuery(Name = "for")] string? person = null)
    {
        var (spec, refusal) = Read(body);
        if (refusal is not null) return refusal;
        var subscriber = PersonId;
        if (person is not null && person != PersonId)
        {
            if (!IsAdmin) return Refuse(403, "forbidden", "Only an administrator subscribes someone else.");
            if (delivery.Users.Get(person) is null) return Refuse(404, "not_found", $"There is no person '{person}'.");
            subscriber = person;
        }

        var next = SubscriptionSpec.Next(spec!.Fields.Cron, spec.Fields.TimeZone, delivery.Time.GetUtcNow()) ?? delivery.Time.GetUtcNow();
        var id = delivery.Subscriptions.Create(spec.Fields, subscriber, delivery.Users.Get(subscriber)?.DisplayName, PersonId, next);
        return Created($"/api/delivery/subscriptions/{id}", new { subscription = View(delivery.Subscriptions.Find(id)!) });
    }

    private (SubscriptionRow? Row, IActionResult? Refusal) Mine(Guid id)
    {
        var row = delivery.Subscriptions.Find(id);
        if (row is null || (!IsAdmin && row.Person != PersonId && row.Owner != PersonId)) return (null, Refuse(404, "not_found", "There is no such subscription."));
        return (row, null);
    }

    /// <summary>Changes what a subscription asks for (it is active again; a stale <c>version</c> is refused with the current one).</summary>
    [HttpPut("subscriptions/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Update(Guid id, [FromBody] JsonObject body, [FromQuery] long? version = null)
    {
        var (row, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        var (spec, refusal) = Read(body);
        if (refusal is not null) return refusal;
        var next = SubscriptionSpec.Next(spec!.Fields.Cron, spec.Fields.TimeZone, delivery.Time.GetUtcNow()) ?? delivery.Time.GetUtcNow();
        if (!delivery.Subscriptions.Update(id, spec.Fields, next, version ?? row!.Version))
            return Refuse(409, "stale", $"The subscription was changed by someone else (its version is now {delivery.Subscriptions.Find(id)?.Version}); open it again.");
        return Ok(new { subscription = View(delivery.Subscriptions.Find(id)!) });
    }

    [HttpDelete("subscriptions/{id:guid}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Delete(Guid id)
    {
        var (_, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        delivery.Subscriptions.Delete(id);
        return NoContent();
    }

    [HttpPost("subscriptions/{id:guid}/pause")]
    [Authorize(Policies.Viewer)]
    public IActionResult Pause(Guid id)
    {
        var (_, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        delivery.Subscriptions.SetStatus(id, "paused", null, null);
        return Ok(new { subscription = View(delivery.Subscriptions.Find(id)!) });
    }

    /// <summary>Sets a paused or stopped subscription going again at its next time.</summary>
    [HttpPost("subscriptions/{id:guid}/resume")]
    [Authorize(Policies.Viewer)]
    public IActionResult Resume(Guid id)
    {
        var (row, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        var next = SubscriptionSpec.Next(row!.Fields.Cron, row.Fields.TimeZone, delivery.Time.GetUtcNow());
        delivery.Subscriptions.SetStatus(id, "active", null, next ?? delivery.Time.GetUtcNow());
        return Ok(new { subscription = View(delivery.Subscriptions.Find(id)!) });
    }

    /// <summary>Renders and sends a subscription now, whatever its schedule says. This needs a process that can draw the document; a console that cannot says so (409).</summary>
    [HttpPost("subscriptions/{id:guid}/run")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        var (_, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        if (!delivery.CanRender) return Refuse(409, "cannot_render", ConsoleNoRenderer.Words);
        var result = await delivery.Runner.RunNowAsync(id, ct);
        return Ok(new { result = new { result.Outcome, result.Detail, result.MessageId }, subscription = View(delivery.Subscriptions.Find(id)!) });
    }

    [HttpGet("subscriptions/{id:guid}/runs")]
    [Authorize(Policies.Viewer)]
    public IActionResult Runs(Guid id)
    {
        var (_, notFound) = Mine(id);
        if (notFound is not null) return notFound;
        return Ok(new { runs = delivery.Subscriptions.Runs(id).Select(r => new { r.Outcome, r.Detail, r.StartedAt, r.FinishedAt, r.MessageId, r.RunId }) });
    }
}

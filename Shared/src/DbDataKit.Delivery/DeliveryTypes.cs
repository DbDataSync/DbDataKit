namespace DbDataKit.Delivery;

/// <summary>A file that goes with a message, as a channel gets it at send time: the bytes are read from the outbox's payload just before the try.</summary>
public sealed record DeliveryAttachment(string Name, string MediaType, byte[] Content);

/// <summary>What a channel is given to send: the message as written to the outbox and the number of this try (from 1).</summary>
public sealed record DeliveryPayload(Guid MessageId, string Channel, IReadOnlyList<string> Recipients, string Subject, string? Text, string? Html, string? Link, IReadOnlyList<DeliveryAttachment> Attachments, int Attempt);

/// <summary>How a try ended, from the channel's point of view.</summary>
public enum DeliveryOutcome
{
    /// <summary>The channel took the message.</summary>
    Sent,

    /// <summary>It did not, and trying again later may work (a timeout, a 5xx, a 429, a mailbox that is full): the service tries again after a back-off, up to the attempt limit.</summary>
    Temporary,

    /// <summary>It did not, and trying again will not help (a refused recipient, a 410, a 4xx that is the message's fault): the message is parked as <c>dead</c> with the channel's words.</summary>
    Permanent,
}

/// <summary>A try's result with the channel's own words (what the server or the service said). The detail is scrubbed of secrets before it is stored or logged, but a channel should never put one in it.</summary>
public sealed record DeliveryResult(DeliveryOutcome Outcome, string Detail)
{
    public static DeliveryResult Sent(string detail = "") => new(DeliveryOutcome.Sent, detail);

    public static DeliveryResult Temporary(string detail) => new(DeliveryOutcome.Temporary, detail);

    public static DeliveryResult Permanent(string detail) => new(DeliveryOutcome.Permanent, detail);
}

/// <summary>
/// One configured channel: a name the install gave it and a way to send. <see cref="SendAsync"/> answers with a <see cref="DeliveryResult"/> for everything the far side said; it throws only for what is not the far side's doing
/// (a bug, a cancelled token): the service treats a throw as a temporary failure (outcome <c>error</c>) and a cancellation as "not tried".
/// </summary>
public interface IDeliveryChannel
{
    /// <summary>The name in the <c>delivery:</c> section (the delivery channel's name, not the connection's).</summary>
    string Name { get; }

    /// <summary>The connection this channel sends through (plan 22a GP18 fold); null for a channel that is not made from one (a test's). A sender holds back by it, so a test channel's messages never use up production's limit per minute.</summary>
    string? Connection => null;

    /// <summary>The kind: <c>email</c>, <c>teams</c>, <c>slack</c>, <c>webhook</c>.</summary>
    string Kind { get; }

    /// <summary>At most this many messages a minute (per sender process); null: no limit.</summary>
    int? LimitPerMinute { get; }

    /// <summary>Tries to send the message.</summary>
    Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken);
}

/// <summary>
/// What a delivery channel name is bound to in a release channel (plan 21: prod, test, dev): the channel, or why there is none. <see cref="Retryable"/> says whether fixing the configuration (a connection that does not read yet) makes a later try work, which is so for a connection
/// that is missing or wrong and not for a channel that is removed or not bound in that release channel.
/// </summary>
public sealed record ChannelResolution(IDeliveryChannel? Channel, string? Connection, string ReleaseChannel, string? Problem, bool Retryable = false);

/// <summary>The channels of an install, by name.</summary>
public interface IDeliveryChannels
{
    /// <summary>The names, in the order of the settings.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The channel of that name (compared ignoring case) in the production release channel, or null: it is not configured (never was, or was removed).</summary>
    IDeliveryChannel? Find(string name);

    /// <summary>
    /// The channel of that name for a release channel (null: production). A set of channels made by hand knows production only; <see cref="DeliveryChannels"/> binds a connection per release channel and <b>never answers with another release channel's</b>
    /// connection: a name that is not bound in <c>test</c> has no channel there, however production is bound.
    /// </summary>
    ChannelResolution Resolve(string name, string? releaseChannel)
    {
        var release = releaseChannel ?? DbDataKit.State.DefinitionStore.Production;
        var channel = release == DbDataKit.State.DefinitionStore.Production ? Find(name) : null;
        return new ChannelResolution(channel, channel?.Connection, release, channel is null
            ? (release == DbDataKit.State.DefinitionStore.Production
                ? $"The delivery channel '{name}' is not configured any more; the message was not sent. Add the channel again and send it again."
                : $"The delivery channel '{name}' is not bound in the release channel '{release}'; the message was not sent. Bind it there in the delivery: section, or send it from another release channel.")
            : null);
    }
}

/// <summary>A message for the outbox, in the form callers use: the attachments are bytes here and payloads in the outbox.</summary>
/// <param name="Channel">The channel's name.</param>
/// <param name="Subject">A line; a webhook channel shows it as the title.</param>
/// <param name="Recipients">Addresses (email); a webhook channel's destination is the channel's own address and ignores them.</param>
/// <param name="Source">What made the message: <c>test</c>, <c>subscription:&lt;id&gt;</c>, <c>alert:&lt;kind&gt;</c>.</param>
/// <param name="OwnerPerson">The person the message is for or who asked for it (a subject id), so a screen shows each person their own.</param>
/// <param name="NotBefore">Not tried before this time; null: at once.</param>
/// <param name="ReleaseChannel">The release channel the message is written in (plan 21: <c>prod</c>, <c>test</c>, <c>dev</c>); the channel name is bound to a different connection in each, so a message from <c>test</c> reaches a sandbox and never production. Null: <c>prod</c>.</param>
public sealed record DeliveryRequest(string Channel, string Subject, IReadOnlyList<string>? Recipients = null, string? Text = null, string? Html = null, string? Link = null,
    IReadOnlyList<DeliveryAttachment>? Attachments = null, string? Source = null, string? OwnerPerson = null, DateTimeOffset? NotBefore = null, string? ReleaseChannel = null);

/// <summary>The message names a channel that is not configured.</summary>
public sealed class UnknownChannelException(string channel, IReadOnlyList<string> known)
    : InvalidOperationException($"The delivery channel '{channel}' is not configured (the delivery: section has {(known.Count == 0 ? "no channels" : string.Join(", ", known))}).")
{
    public string Channel { get; } = channel;
}

/// <summary>The channel is configured but not bound to a connection in the release channel the message is written in (or its connection is not a usable one): the message is refused to the caller, never sent through another release channel's connection.</summary>
public sealed class ChannelNotBoundException(string channel, string releaseChannel, string reason) : InvalidOperationException(reason)
{
    public string Channel { get; } = channel;

    public string ReleaseChannel { get; } = releaseChannel;
}

/// <summary>A channel cannot be used right now for a reason in its configuration (a secret that is not in the store yet): a temporary failure, because fixing the configuration makes the next try work.</summary>
public sealed class ChannelUnavailableException(string message) : Exception(message);

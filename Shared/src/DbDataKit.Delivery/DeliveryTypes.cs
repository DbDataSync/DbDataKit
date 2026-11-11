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
    /// <summary>The name in the <c>delivery:</c> section.</summary>
    string Name { get; }

    /// <summary>The kind: <c>email</c>, <c>teams</c>, <c>slack</c>, <c>webhook</c>.</summary>
    string Kind { get; }

    /// <summary>At most this many messages a minute (per sender process); null: no limit.</summary>
    int? LimitPerMinute { get; }

    /// <summary>Tries to send the message.</summary>
    Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken);
}

/// <summary>The channels of an install, by name.</summary>
public interface IDeliveryChannels
{
    /// <summary>The names, in the order of the settings.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The channel of that name (compared ignoring case), or null: it is not configured (never was, or was removed).</summary>
    IDeliveryChannel? Find(string name);
}

/// <summary>A message for the outbox, in the form callers use: the attachments are bytes here and payloads in the outbox.</summary>
/// <param name="Channel">The channel's name.</param>
/// <param name="Subject">A line; a webhook channel shows it as the title.</param>
/// <param name="Recipients">Addresses (email); a webhook channel's destination is the channel's own address and ignores them.</param>
/// <param name="Source">What made the message: <c>test</c>, <c>subscription:&lt;id&gt;</c>, <c>alert:&lt;kind&gt;</c>.</param>
/// <param name="OwnerPerson">The person the message is for or who asked for it (a subject id), so a screen shows each person their own.</param>
/// <param name="NotBefore">Not tried before this time; null: at once.</param>
public sealed record DeliveryRequest(string Channel, string Subject, IReadOnlyList<string>? Recipients = null, string? Text = null, string? Html = null, string? Link = null,
    IReadOnlyList<DeliveryAttachment>? Attachments = null, string? Source = null, string? OwnerPerson = null, DateTimeOffset? NotBefore = null);

/// <summary>The message names a channel that is not configured.</summary>
public sealed class UnknownChannelException(string channel, IReadOnlyList<string> known)
    : InvalidOperationException($"The delivery channel '{channel}' is not configured (the delivery: section has {(known.Count == 0 ? "no channels" : string.Join(", ", known))}).")
{
    public string Channel { get; } = channel;
}

/// <summary>A channel cannot be used right now for a reason in its configuration (a secret that is not in the store yet): a temporary failure, because fixing the configuration makes the next try work.</summary>
public sealed class ChannelUnavailableException(string message) : Exception(message);

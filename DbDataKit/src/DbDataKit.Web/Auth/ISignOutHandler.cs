namespace DbDataKit.Web.Auth;

/// <summary>
/// What a host does besides ending its own session when a person signs out (plan 13a U17): a console that signs people in through an identity home asks the home to end theirs. Registered by the host; none is the usual case.
/// </summary>
public interface ISignOutHandler
{
    /// <summary>Called with the id of the session that was just ended. Returns an address to send the browser to (the home's end-session endpoint), or null.</summary>
    Task<string?> SignedOutAsync(string sessionId, CancellationToken ct);
}

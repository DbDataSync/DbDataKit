namespace DbDataKit.Web.Trust;

/// <summary>What is on the other end of a pairing (plan 13, W12): a console, another server calling this one for a named purpose, or a command line on someone's machine.</summary>
public enum ClientKind
{
    Console,
    Server,
    Cli,
}

/// <summary>A client as a server knows it after pairing: who it is, its public signing key, and the most any of its users may do here.</summary>
/// <param name="Id">The client's id (the token's issuer).</param>
/// <param name="Name">A label for lists and the audit.</param>
/// <param name="PublicKey">The Ed25519 public key tokens are checked against.</param>
/// <param name="Ceiling">The roles this client may grant on this server; a token that claims more is refused.</param>
/// <param name="Kind">What the client is.</param>
/// <param name="PairedAt">When the pairing was made, if known.</param>
public sealed record PairedClient(string Id, string Name, byte[] PublicKey, ServerRole Ceiling, ClientKind Kind = ClientKind.Console, DateTimeOffset? PairedAt = null);

/// <summary>The server's list of paired clients (kept in <c>server.yaml</c> by <see cref="PairedClientsFile"/>).</summary>
public interface IPairedClients
{
    PairedClient? Find(string clientId);
}

/// <summary>What a console asks its token to say.</summary>
/// <param name="Audience">The server's instance id: a token is for one server.</param>
/// <param name="SubjectId">The user's id, or the console's own for background polling.</param>
/// <param name="SubjectName">The user's name, for the audit.</param>
/// <param name="Roles">The user's roles on this server.</param>
/// <param name="Projects">For Build: the user's roles in each project.</param>
/// <param name="IsConsole">True when the subject is the console itself (polling), not a user.</param>
public sealed record ConsoleTokenRequest(string Audience, string SubjectId, string? SubjectName, ServerRole Roles,
    IReadOnlyDictionary<string, ServerRole>? Projects = null, bool IsConsole = false);

/// <summary>A token that passed every check: who is calling, from which client, with what roles (already within the console's ceiling).</summary>
public sealed record ValidatedToken(PairedClient Client, string SubjectId, string? SubjectName, bool IsConsole, ServerRole Roles,
    IReadOnlyDictionary<string, ServerRole> Projects, string TokenId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public enum TokenFailure
{
    None,
    Malformed,
    UnsupportedAlgorithm,
    UnknownIssuer,
    BadSignature,
    MissingClaim,
    WrongAudience,
    Expired,
    NotYetValid,
    LifetimeTooLong,
    AboveCeiling,
    Replayed,
}

/// <summary>The outcome of checking a token. <see cref="Detail"/> is for the server's log, never for the caller: it says why, and a caller must only be told it was refused.</summary>
public sealed record TokenResult(ValidatedToken? Token, TokenFailure Failure, string? Detail)
{
    public bool Ok => Token is not null;
    public static TokenResult Success(ValidatedToken token) => new(token, TokenFailure.None, null);
    public static TokenResult Refused(TokenFailure failure, string detail) => new(null, failure, detail);
}

using DbDataKit.Cli;

namespace DbDataKit.Semantic.Cli;

/// <summary>A column of an answer: its name and the type the server gave it.</summary>
public sealed record SemanticColumn(string Name, string Type);

/// <summary>What a served model answered: its columns, the rows read (at most the cap asked for), whether there were more, and the server's notes (<c>qis.status</c> and the like).</summary>
public sealed record SemanticTable(IReadOnlyList<SemanticColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, IReadOnlyDictionary<string, string> Notes);

/// <summary>
/// A connection to a server that serves semantic models, as the person or agent running the command (plan 19, J15). The statement is what the model source takes: semantic SQL, or a request in JSON (<c>kind</c> query, explain, members,
/// describe). The server runs it as the caller, so the caller's own access applies; the command line adds nothing to it.
/// </summary>
public interface ISemanticSession : IAsyncDisposable
{
    Task<SemanticTable> RunAsync(string model, string statement, int maxRows, CancellationToken cancellationToken);
}

/// <summary>How a product connects: it reads its own connection options (an address, a user, a password from the environment) from the arguments of the verb.</summary>
public delegate Task<ISemanticSession> SemanticConnect(CliArgs arguments, CliContext context);

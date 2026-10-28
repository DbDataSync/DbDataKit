namespace DbDataKit.Serving.Conformance;

/// <summary>A caller of the host under test, by the credentials that identify them, and a request that caller must be refused.</summary>
public sealed record ServingRefusal(ServingCredentials Who, ServingRequest.Sql Request);

/// <summary>A caller who is held to a row limit, and a request that reaches it.</summary>
public sealed record ServingLimited(ServingCredentials Who, ServingRequest.Sql Request, long MaxRows);

/// <summary>
/// What the conformance suite needs to know about a host: its services, who can sign in, and a few requests whose answers every host gives the same way. The host's own test project builds one (a fake host, Proxy over DuckDB) and the suite checks the promises
/// a protocol module relies on; what is host specific (the data) is in this record, not in the suite.
/// </summary>
public sealed class ServingTarget : IAsyncDisposable
{
    public required IServingServices Services { get; init; }

    /// <summary>Credentials of a caller who may run <see cref="Open"/> and see <see cref="VisibleTable"/>.</summary>
    public required ServingCredentials Full { get; init; }

    /// <summary>Credentials that identify nobody.</summary>
    public required ServingCredentials Wrong { get; init; }

    /// <summary>The source <see cref="Full"/> uses, and a request to it returning <see cref="OpenRows"/> rows.</summary>
    public required string Source { get; init; }

    public required ServingRequest.Sql Open { get; init; }

    public required long OpenRows { get; init; }

    /// <summary>The name of a table or view <see cref="Full"/> sees in <see cref="Source"/>'s catalog.</summary>
    public required string VisibleTable { get; init; }

    /// <summary>A request for something that is not there (an unknown table); the answer is not-found or a classified backend failure.</summary>
    public required ServingRequest.Sql Missing { get; init; }

    /// <summary>A request to a source that does not exist.</summary>
    public required ServingRequest.Sql UnknownSource { get; init; }

    /// <summary>A caller who is refused something: a table they may not read, a source they may not use.</summary>
    public required ServingRefusal Refused { get; init; }

    /// <summary>A caller held to a row limit, when the host has limits.</summary>
    public ServingLimited? Limited { get; init; }

    /// <summary>A caller who does not see <see cref="HiddenTable"/> in the catalog, when the host hides tables.</summary>
    public ServingCredentials? Restricted { get; init; }

    public string? HiddenTable { get; init; }

    /// <summary>A statement <see cref="Full"/> may run when the host takes writes, and a caller who must be refused one (a read-only source, a caller without the right); both null for a host with no write path.</summary>
    public ServingRequest.Sql? Write { get; init; }

    public ServingRefusal? RefusedWrite { get; init; }

    /// <summary>Whether preparing a statement is refused when running it would be. A host that knows no dialect cannot tell without running it and says false.</summary>
    public bool PrepareRefuses { get; init; } = true;

    /// <summary>Whether asking for the facts of a source that does not exist is answered with not-found. The adapter over the small host interface cannot say, because that interface lists no sources, and says false.</summary>
    public bool FactsRefuseUnknownSource { get; init; } = true;

    /// <summary>Whether <see cref="Source"/> takes writes, as <see cref="IServingCatalog.FactsAsync"/> must say.</summary>
    public bool SourceReadOnly { get; init; } = true;

    /// <summary>Released after the tests: the host, its engine, its files.</summary>
    public Func<ValueTask>? Dispose { get; init; }

    public ValueTask DisposeAsync() => Dispose?.Invoke() ?? ValueTask.CompletedTask;
}

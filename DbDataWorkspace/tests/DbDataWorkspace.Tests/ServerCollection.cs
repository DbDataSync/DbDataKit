namespace DbDataWorkspace.Tests;

/// <summary>
/// Every class that builds a server is in one collection, so they run one at a time: the announcement test sets the console's announce address in the process's environment while its server is built, and a server another class built in that
/// moment would announce to it too (and be the one the test heard).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ServerCollection
{
    public const string Name = "workspace servers";
}

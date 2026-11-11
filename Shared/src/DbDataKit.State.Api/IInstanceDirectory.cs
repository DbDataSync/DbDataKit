namespace DbDataKit.State.Api;

/// <summary>One live instance that shares this server's state.</summary>
/// <param name="Key">The instance's own key (the product's instance table).</param>
/// <param name="IsSelf">True for the instance that answered.</param>
/// <param name="Host">The machine it runs on, when known.</param>
/// <param name="ProcessId">Its process id on that machine, when known.</param>
/// <param name="Started">When it started, when known.</param>
/// <param name="Heartbeat">When it last refreshed its row, when known.</param>
/// <param name="Version">The software version the instance runs, when the product records it; null when it does not.</param>
/// <param name="Modules">The module versions the instance's binary has, when the product records them; null when it does not.</param>
/// <param name="DefinitionFormats">The formats of each kind of published definition the instance reads (24a UC01), when its row records them; null when it does not.</param>
/// <param name="Generation">The compatibility generation of the instance's binary (24a UC05), when its row records it; null when it does not.</param>
public sealed record StateInstance(string Key, bool IsSelf, string? Host = null, int? ProcessId = null, DateTimeOffset? Started = null, DateTimeOffset? Heartbeat = null, string? Version = null, IReadOnlyDictionary<string, int>? Modules = null,
    IReadOnlyDictionary<string, DefinitionFormats>? DefinitionFormats = null, int? Generation = null);

/// <summary>
/// The live instances that share a server's state, for the products that keep an instance table (the Gateway's <c>serving_instance</c>). The default, <see cref="Empty"/>, is for the products that have none: one process owns its state and the answer
/// has no <c>instances</c> beyond the one that answered.
/// </summary>
public interface IInstanceDirectory
{
    /// <summary>The live instances, this one included. Read-only.</summary>
    IReadOnlyList<StateInstance> Live();

    public static IInstanceDirectory Empty { get; } = new EmptyDirectory();

    private sealed class EmptyDirectory : IInstanceDirectory
    {
        public IReadOnlyList<StateInstance> Live() => [];
    }
}

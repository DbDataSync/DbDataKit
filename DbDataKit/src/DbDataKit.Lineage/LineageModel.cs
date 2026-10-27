using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Lineage;

public enum NodeKind { Dataset, Column, Job, SemanticMember, Serving, Consumer }

/// <summary>How data moves along an edge; the edge runs from the upstream object to the downstream one.</summary>
public enum EdgeKind { Replicates, Derives, Copies, Binds, Serves, Extracts, Reads, Promotes }

public enum Granularity { Column, Table }

/// <summary>How an edge is known: written in a definition, computed from one, or seen in a run.</summary>
public enum Confidence { Declared, Derived, Observed }

public enum RunOutcome { Succeeded, Failed }

/// <summary>
/// An object of the graph. <paramref name="Id"/> is its identity (see <see cref="LineageIdentity"/> for datasets and columns; a suite object is its server's instance id and its own name). <paramref name="UnknownUpstream"/> is true where the owning
/// product cannot say what feeds this object.
/// </summary>
public sealed record LineageNode(string Id, NodeKind Kind, string Name, bool UnknownUpstream = false, IReadOnlyDictionary<string, string>? Properties = null)
{
    public IReadOnlyDictionary<string, string> PropertyMap => Properties ?? new Dictionary<string, string>();
}

/// <summary>Data flowing from <paramref name="From"/> to <paramref name="To"/>. <paramref name="Detail"/> is the kind's own word (the transform of a <c>derives</c>: identity, expression, aggregate).</summary>
public sealed record LineageEdge(string From, string To, EdgeKind Kind, Granularity Granularity, Confidence Confidence, string? Detail = null)
{
    /// <summary>What names the edge: its ends and kind. Two edges with one key are one edge.</summary>
    public string Key => $"{From}\n{To}\n{Kind}";

    /// <summary>The key as a fixed-length text, for a store that indexes it.</summary>
    public string KeyHash => Hash(Key);

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>What happened along an edge when it ran: when, how it ended and how many rows moved (null when the product does not count).</summary>
public sealed record EdgeRun(string From, string To, EdgeKind Kind, DateTimeOffset At, RunOutcome Outcome, long? Rows = null)
{
    public string EdgeKey => $"{From}\n{To}\n{Kind}";
}

/// <summary>A graph that has a cycle in it, or an edge to a node it does not have.</summary>
public sealed class LineageGraphException(string message) : Exception(message);

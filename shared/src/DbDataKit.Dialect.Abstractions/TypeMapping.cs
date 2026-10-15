namespace DbDataKit.Dialect;

/// <summary>How faithfully a value survives a type mapping.</summary>
public enum Fidelity
{
    /// <summary>Same domain and semantics.</summary>
    Exact,
    /// <summary>Target domain is a superset (unsigned int64 into decimal(20,0)); values survive, the type changes.</summary>
    Widened,
    /// <summary>Some values or semantics are lost (precision cut, money semantics, range clamped).</summary>
    Lossy,
    /// <summary>No mapping exists; <see cref="TypeMapping{T}.Result"/> is default.</summary>
    Unmappable,
}

/// <summary>The outcome of a type mapping. A silent coercion is a bug: any non-Exact outcome carries a Note.</summary>
public sealed record TypeMapping<T>(T? Result, Fidelity Fidelity, string? Note = null)
{
    public bool IsMapped => Fidelity != Fidelity.Unmappable;
    public static TypeMapping<T> Exact(T result) => new(result, Fidelity.Exact);
    public static TypeMapping<T> Unmappable(string note) => new(default, Fidelity.Unmappable, note);
    public T Require() => IsMapped ? Result! : throw new InvalidOperationException(Note ?? "type is unmappable");
}

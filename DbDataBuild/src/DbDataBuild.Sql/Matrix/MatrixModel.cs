namespace DbDataBuild.Sql.Matrix;

/// <summary>Status tiers of DESIGN.md 7.1.</summary>
public enum SupportStatus { Native, Translated, Approximated, Emulated, Unsupported, Unverified }

public sealed record MatrixEntry(SupportStatus Status, int? MinVersion, string? Note, string? Test);

/// <summary>How a row finds its construct in the AST: a node tag, a function name, or a named coded detector.</summary>
public enum DetectKind { Node, Function, Detector, DataType }

public sealed record DetectRule(DetectKind Kind, string Name)
{
    public override string ToString() => Kind switch { DetectKind.Node => "node:", DetectKind.Function => "fn:", _ => "detector:" } + Name;
}

public sealed record ConstructRow(string Id, IReadOnlyList<DetectRule> Detect, IReadOnlyDictionary<string, MatrixEntry> Targets, string File, int Line);

/// <summary>A node or function verified as plain SQL, with the spike/conformance cases that show it.</summary>
public sealed record CoverageEntry(DetectKind Kind, string Name, IReadOnlyList<string> Evidence, string File, int Line);

public sealed record SupportMatrix(IReadOnlyList<ConstructRow> Rows, IReadOnlyList<CoverageEntry> Covered, IReadOnlyList<ConstructRow>? StrategyRows = null)
{
    /// <summary>Load strategy rows (matrix/strategies.yml). Ids are `strategy.<name>`.</summary>
    public IReadOnlyList<ConstructRow> Strategies => StrategyRows ?? [];

    public static readonly IReadOnlyList<string> Targets = ["sqlserver", "fabric", "postgres"];

    public bool IsCoveredNode(string tag) => Covered.Any(c => c.Kind == DetectKind.Node && c.Name == tag);
    public bool IsCoveredFunction(string name) =>
        Covered.Any(c => c.Kind == DetectKind.Function && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

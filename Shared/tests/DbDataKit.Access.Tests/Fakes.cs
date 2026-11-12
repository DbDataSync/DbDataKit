namespace DbDataKit.Access.Tests;

/// <summary>A definition with a fact table `sales`, a dimension-like `customers`, the rules tables `region_access` and `closed_accounts`, and a table `read_by_model` that a measure reads.</summary>
internal sealed class Fakes : IAccessTarget
{
    public bool Shared { get; init; }

    public bool CacheSharedAcrossCallers => Shared;

    private static readonly Dictionary<string, AccessDataset> Tables = new()
    {
        ["sales"] = new("sales", "warehouse", new Dictionary<string, AccessColumnKind> { ["region"] = AccessColumnKind.Text, ["channel"] = AccessColumnKind.Text, ["account"] = AccessColumnKind.Number, ["amount"] = AccessColumnKind.Number }, "sqlserver"),
        ["customers"] = new("customers", "warehouse", new Dictionary<string, AccessColumnKind> { ["email"] = AccessColumnKind.Text, ["salary"] = AccessColumnKind.Number, ["phone"] = AccessColumnKind.Text }, "sqlserver"),
        ["region_access"] = new("region_access", "warehouse", new Dictionary<string, AccessColumnKind> { ["who"] = AccessColumnKind.Text, ["region_code"] = AccessColumnKind.Text, ["channel_code"] = AccessColumnKind.Text, ["amount_limit"] = AccessColumnKind.Number }, "sqlserver"),
        ["closed_accounts"] = new("closed_accounts", "warehouse", new Dictionary<string, AccessColumnKind> { ["who"] = AccessColumnKind.Text, ["account_id"] = AccessColumnKind.Number }, "sqlserver"),
        ["other_source"] = new("other_source", "elsewhere", null, "postgres"),
        ["read_by_model"] = new("read_by_model", "warehouse", null, "sqlserver"),
        ["cube"] = new("cube", "olap", null, "ssas"),
    };

    public static Fakes Target(bool shared = false) => new() { Shared = shared };

    public AccessDataset? Dataset(string name) => Tables.GetValueOrDefault(name);

    public IReadOnlyList<string> DatasetNames => [.. Tables.Keys];

    public AccessDataset? RulesTable(string reference, AccessDataset filtered) => Tables.GetValueOrDefault(reference);

    public string? WhyNotRulesTable(AccessDataset table) => table.Name == "read_by_model" ? "a measure reads it" : null;

    public string? WhyCannotFilter(AccessDataset dataset) => dataset.SourceKind == "ssas" ? "its source is Analysis Services, which no filter reaches" : null;
}

namespace DbData.Hosting;

public sealed class DbDataHostOptions
{
    /// <summary>Product name reported by /version (and the default service name).</summary>
    public string Product { get; set; } = "";

    public string Version { get; set; } = "0.0.0";

    public string? Commit { get; set; }

    /// <summary>Schema/contract versions, e.g. {"config": "3", "state-db": "12"}.</summary>
    public IDictionary<string, string> SchemaVersions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Windows service name; defaults to <see cref="Product"/>.</summary>
    public string? ServiceName { get; set; }

    /// <summary>How long to wait for in-flight work when stopping.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public DrainRejection DrainRejection { get; set; } = DrainRejection.MutationsOnly;

    /// <summary>Path prefixes that keep working while draining.</summary>
    public IList<string> DrainExemptPrefixes { get; } = ["/health", "/version"];

    public RequestLimitOptions Limits { get; } = new();

    public ForwardedHeadersPolicy ForwardedHeaders { get; } = new();

    /// <summary>Force a mode (tests); null = detect.</summary>
    public HostMode? ModeOverride { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Product)) throw new InvalidOperationException("DbDataHostOptions.Product is required.");
        if (DrainTimeout < TimeSpan.Zero) throw new InvalidOperationException("DrainTimeout must not be negative.");
        Limits.Validate();
    }

    internal VersionInfo ToVersionInfo(bool ready) =>
        new(Product, Version, Commit, new Dictionary<string, string>(SchemaVersions, StringComparer.Ordinal), ready);
}

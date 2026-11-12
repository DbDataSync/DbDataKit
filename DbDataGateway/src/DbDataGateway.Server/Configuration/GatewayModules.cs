namespace DbDataGateway.Server.Configuration;

/// <summary>The modules of the serving layer a Gateway instance can serve, and which of them one instance does (plan 20, O01).</summary>
public static class GatewayModules
{
    public const string FlightSql = "flightsql";
    public const string Identity = "identity";
    public const string Apps = "apps";
    public const string Store = "store";
    public const string Views = "views";
    public const string Mcp = "mcp";
    public const string Feeds = "feeds";

    public static readonly IReadOnlyList<string> Known = [FlightSql, Identity, Apps, Store, Views, Mcp, Feeds];

    /// <summary>True when the instance serves the module: it is listed in <c>Modules</c>, or (for the identity home) <c>Identity.Enabled</c> is set.</summary>
    public static bool Has(GatewayOptions options, string module) =>
        options.Modules.Contains(module, StringComparer.OrdinalIgnoreCase) || (module == Identity && options.Identity.Enabled);
}

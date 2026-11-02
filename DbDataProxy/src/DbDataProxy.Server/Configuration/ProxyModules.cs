namespace DbDataProxy.Server.Configuration;

/// <summary>The modules of the serving layer a Proxy instance can serve, and which of them one instance does (plan 20, O01).</summary>
public static class ProxyModules
{
    public const string FlightSql = "flightsql";
    public const string Identity = "identity";

    public static readonly IReadOnlyList<string> Known = [FlightSql, Identity];

    /// <summary>True when the instance serves the module: it is listed in <c>Modules</c>, or (for the identity home) <c>Identity.Enabled</c> is set.</summary>
    public static bool Has(ProxyOptions options, string module) =>
        options.Modules.Contains(module, StringComparer.OrdinalIgnoreCase) || (module == Identity && options.Identity.Enabled);
}

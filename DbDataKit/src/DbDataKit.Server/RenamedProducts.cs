namespace DbDataKit.Server;

/// <summary>
/// Products that were renamed (plan 20, O14: DbDataProxy is now DbDataGateway). A clean break: an announcement under the old name is refused with a message that names both, not taken for the new product.
/// A server already paired follows its own answer on <c>/version</c>, so it shows as the new product once it is upgraded.
/// </summary>
public static class RenamedProducts
{
    /// <summary>Why an announcement as this product is refused, or null when the name was never renamed.</summary>
    public static string? Message(string? product) => product?.ToLowerInvariant() switch
    {
        "dbdataproxy" => "DbDataProxy is now DbDataGateway; the old name is no longer accepted. Upgrade that server to dbdatagateway and announce it again.",
        _ => null,
    };
}

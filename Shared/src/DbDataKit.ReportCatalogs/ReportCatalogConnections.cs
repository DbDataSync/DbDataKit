using DbDataKit.Connections;
using DbDataKit.Connections.Http;
using DbDataKit.Secrets;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs;

/// <summary>A provider made from a <c>report-catalog</c> connection; dispose it when the read is done (it owns the signed-in HTTP client).</summary>
public sealed class ReportCatalogConnection(IReportCatalogProvider provider, IDisposable owned) : IDisposable
{
    public IReportCatalogProvider Provider { get; } = provider;

    public void Dispose() => owned.Dispose();
}

/// <summary>Makes the provider of a connection of kind <c>report-catalog</c> (plan 32 RC01): the HTTP client signs in as the connection says (the service account, secrets as references), the provider reads the portal's REST API.</summary>
public static class ReportCatalogConnections
{
    public static ReportCatalogConnection Create(ConnectionDefinition definition, ISecretResolver secrets, HttpConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.KindName != ConnectionKinds.ReportCatalog || definition.ReportCatalog is not { } spec || definition.Endpoint is null)
            throw new ArgumentException("The connection is not of kind report-catalog with an endpoint and a report_catalog section.", nameof(definition));
        if (!Uri.TryCreate(definition.Endpoint, UriKind.Absolute, out var portal)) throw new ArgumentException($"The endpoint of '{definition.Name}' is not an address.", nameof(definition));
        Uri? server = spec.ReportServerAddress is { } a ? new Uri(a) : null;
        var client = HttpConnections.Create(definition, secrets, options);
        try
        {
            var kind = spec.Provider == "pbirs" ? ReportServerKind.Pbirs : ReportServerKind.Ssrs;
            return new ReportCatalogConnection(new ReportServerCatalogProvider(client.Client, new ReportServerOptions(portal, kind, server, spec.Name)), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

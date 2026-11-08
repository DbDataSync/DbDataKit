using DbDataKit.Hosting.Certificates;

namespace DbDataGateway.Server.Tls;

/// <summary>
/// The shared protected password file with the format identifiers Gateway has always written, so existing password
/// files keep loading. See <see cref="DbDataKit.Hosting.Certificates.ProtectedPasswordFile"/>.
/// </summary>
public sealed class ProtectedPasswordFile(string path, IReadOnlyList<string> identities)
    : DbDataKit.Hosting.Certificates.ProtectedPasswordFile(path, identities, "dbdatagateway-secret-v1", "DbDataGateway.Tls.PfxPassword.v1");

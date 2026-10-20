using DbDataKit.Hosting.Certificates;

namespace DbDataProxy.Server.Tls;

/// <summary>
/// The shared protected password file with the format identifiers Proxy has always written, so existing password
/// files keep loading. See <see cref="DbDataKit.Hosting.Certificates.ProtectedPasswordFile"/>.
/// </summary>
public sealed class ProtectedPasswordFile(string path, IReadOnlyList<string> identities)
    : DbDataKit.Hosting.Certificates.ProtectedPasswordFile(path, identities, "dbdataproxy-secret-v1", "DbDataProxy.Tls.PfxPassword.v1");

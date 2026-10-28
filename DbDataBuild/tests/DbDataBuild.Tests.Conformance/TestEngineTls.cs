using System.Runtime.CompilerServices;
using DbDataBuild.Execution;
using DbDataKit.Connections;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The shared test engines have self-signed certificates (or none), and a connection without a `tls:` block encrypts and verifies. A conformance run says so once, here, for every connection of its projects; a test of the product's own
/// default (<c>LoginTlsTests</c>) does not run against an engine.
/// </summary>
internal static class TestEngineTls
{
    [ModuleInitializer]
    internal static void Initialize() => LoginSettings.DefaultTls = new TlsSpec { Encrypt = false, TrustServerCertificate = true };
}

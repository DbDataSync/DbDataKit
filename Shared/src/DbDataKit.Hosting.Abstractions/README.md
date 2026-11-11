# DbDataKit.Hosting.Abstractions

Dependency-free host conventions (Plan 03 section 3.6). No ASP.NET reference.

Contract: `HostModeDetector` (console/service/systemd; `--console` always wins), `VersionInfo` (body of `/version`: product, version, commit, schema versions, ready), `HealthReport`/`IReadinessProbe` (`/health`), `IDrainCoordinator`/`DrainCoordinator` (stop accepting work, wait N seconds; generalises Sync's `UpdateDrainMiddleware`), `RequestLimitOptions` (safe defaults: 100 concurrent, 100 s timeout, 4 MB bodies), `ForwardedHeadersPolicy` (trust only listed proxies; `/0` refused), `HealthProbeClient` (`--healthcheck [url]` for Docker HEALTHCHECK).

Decisions: draining answers mutations with 409 (Sync behaviour) or everything with 503; probe exceptions never leak text into `/health`; readiness is false while draining.

`HealthProbeOptions` (optional second argument of `HealthProbeClient.TryHandleAsync`/`RunAsync`): `DefaultUrl` for a bare `--healthcheck`, `HttpVersion` (2.0 for HTTP/2-only gRPC/Flight hosts), and `TrustLoopbackCertificate` (accept a self-signed certificate for a loopback URL only).

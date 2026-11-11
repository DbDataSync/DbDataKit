# syntax=docker/dockerfile:1

# ── The .NET side ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# The shared libraries sit next to the product, as in the repository: DbDataSync's Directory.Build.props finds them at ../DbDataKit.
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY duckdb.pin ./
COPY Shared/Directory.Build.props Shared/global.json Shared/
COPY Shared/src/ Shared/src/
WORKDIR /src/DbDataSync
COPY DbDataSync/DbDataSync.slnf DbDataSync/Directory.Build.props DbDataSync/Directory.Build.targets DbDataSync/Directory.Packages.props DbDataSync/dbdatasync.runsettings ./
COPY DbDataSync/src/ ./src/
COPY DbDataSync/tests/ ./tests/
COPY DbDataSync/tools/ ./tools/

# The image carries no web app (plan 13, U11): the screens are the console's, which has its own image (DbDataKit/Dockerfile).
#
# VERSION is the release this image is built for (phase 163). Left empty — a local `docker build -f DbDataSync/Dockerfile .` from the repository root — the csproj's own
# development default applies, which is what an image built by hand should say. Set, it is the version `dbdatasync version`
# prints and the one `dbdatasync update` compares against, so a published image must always be given it: without it an image
# reports a build timestamp with `-alpha` on it, whatever release it really is.
# LauncherRuntimeIdentifiers=none (phase 196L): the per-platform launchers are for converting a tool-style install to
# slots; an image is never one, and building them would fetch eight apphost packs for nothing.
ARG VERSION=""
RUN dotnet publish src/DbDataSync.Cli/DbDataSync.Cli.csproj \
    -c Release -o /app -p:LauncherRuntimeIdentifiers=none ${VERSION:+-p:Version=$VERSION}

# Phase 121: every KnownLibraries entry, restored once at its pinned version, while this stage still
# has both the SDK and the just-published CLI (the "internal" command exists only for this — see
# InternalCommand.cs). Shipped into *both* final images below: a few megabytes per entry (Oracle ~6MB) except
# DuckDB, which carries its engine (about 100MB: DuckDbEngine downloads it from duckdb.pin, hash-checked, here at build time), so the
# runtime-only image needs no network for it and the default (SDK) image gets the same no-network fast path.
# CI's cache of the DuckDB archives (.duckdb-archives/, filled by .github/actions/duckdb-cache) is inside the build context; a local build has none and
# DuckDbEngine downloads from the pin's address (the pattern matches nothing then, and duckdb.pin alone is copied). DUCKDB_RELEASE_BASE is the variable it reads.
COPY duckdb.pin .duckdb-archive[s] /duckdb-archives/
RUN DUCKDB_RELEASE_BASE=file:///duckdb-archives dotnet /app/DbDataSync.Cli.dll internal build-catalog-cache /app/library-cache

# ── The runtime-only image (phase 121) ─────────────────────────────────────────
# mcr.microsoft.com/dotnet/aspnet:10.0 — no SDK. Installing a library needs none (the server resolves it from a NuGet feed
# itself, LibraryInstaller): a catalog id at its pinned version is copied from /app/library-cache above, so it needs no
# network; any other library is resolved from the feed and needs the network. See docs/install.md.
#
# This stage is deliberately NOT the last one in this file (see the final stage below) — `docker
# build .` with no --target must keep resolving to the SDK-based image, exactly as phase 120 decided.
# Build this one explicitly: `docker build -f DbDataSync/Dockerfile --target runtime -t dbdatasync:<v>-runtime .` (from the repository root)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The image serves https, as the Build, Proxy, Store and console images do (plan 13a, L08): on its first start the server makes a self-signed certificate in the
# volume (tls/dbdatasync.pfx), which a console pins when it pairs. Plain http is no longer the image's default; a deployment behind a proxy that ends TLS overrides the
# entrypoint (docs/install.md, "Running in a container").
ENV DBDATAKIT_DATA_SYNC=/var/lib/dbdatasync
VOLUME ["/var/lib/dbdatasync"]

WORKDIR /app
COPY --from=build /app/ ./

EXPOSE 8443

HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/DbDataSync.Cli.dll", "health", "check", "--url", "https://127.0.0.1:8443"]

ENTRYPOINT ["dotnet", "/app/DbDataSync.Cli.dll", "server", "run", "--url", "https://0.0.0.0:8443"]

# ── The default image ───────────────────────────────────────────────────────────
# Debian, not Alpine. LibGit2Sharp, Microsoft.Data.Sqlite and DuckDB.NET all ship glibc natives, and
# a musl base finds that out as a DllNotFoundException at run time — the worst place to learn it.
#
# The SDK image, kept as the default (phase 120) although nothing in the server shells out to `dotnet publish` any more
# (installing a library resolves it from a NuGet feed, no SDK). This image stays
# the same for people who run their own tools in it. Bigger (~250MB → ~750MB
# uncompressed base); the `runtime` stage above is the smaller image.
#
# This is the true last stage in the file on purpose: `docker build -f DbDataSync/Dockerfile .` / `docker compose -f
# docker-compose.app.yml build` with no --target picks whichever stage is positionally last, and
# phase 120 already decided the SDK image stays the default — moving it earlier and leaving `runtime`
# last would silently flip that default the next time someone edits this file without noticing the
# ordering was load-bearing.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# One mount is a complete deployment: the config repository and the state database live together, so
# a backup of this directory is a backup of everything that is not the image.
#
# DBDATAKIT_DATA_SYNC is not a container-only name — it is the suite's variable for Sync's folder (plan 33 section 6c),
# the same one an interactive install or a Windows/Linux service points at its own machine-wide data directory with.
# The volume stays /var/lib/dbdatasync (only the variable's name changed), so an existing volume is still the one used.
# The image serves https, as the Build, Proxy, Store and console images do (plan 13a, L08): on its first start the server makes a self-signed certificate in the
# volume (tls/dbdatasync.pfx), which a console pins when it pairs. Plain http is no longer the image's default; a deployment behind a proxy that ends TLS overrides the
# entrypoint (docs/install.md, "Running in a container").
ENV DBDATAKIT_DATA_SYNC=/var/lib/dbdatasync
VOLUME ["/var/lib/dbdatasync"]

WORKDIR /app
COPY --from=build /app/ ./

EXPOSE 8443

# The tool checks itself. The runtime image has no curl and no bash, so a shell-based check has to
# pick its way around both — the first attempt used /dev/tcp under /bin/sh, which is a bash feature
# under dash, and reported a healthy container as unhealthy every time.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/DbDataSync.Cli.dll", "health", "check", "--url", "https://127.0.0.1:8443"]

ENTRYPOINT ["dotnet", "/app/DbDataSync.Cli.dll", "server", "run", "--url", "https://0.0.0.0:8443"]

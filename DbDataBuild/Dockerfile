# The Build server for Linux containers.
#   docker build -f DbDataBuild/Dockerfile -t dbdatabuild .        (run from the repository root: the build needs Shared/ too)
#   docker run -p 8470:8470 -v ./build-data:/data dbdatabuild        (/data holds the server's identity, paired clients, projects, secrets and certificate)
# Pair a console, add a project, set a login (the commands run inside the container, as the server's own user):
#   docker exec <container> dbdatabuild server pair
#   docker exec <container> dbdatabuild server project add sales --git https://host/sales.git
#   docker exec -i <container> dbdatabuild secret set dbdatakit:build:connection:warehouse:read --stdin
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY Shared Shared
COPY DbDataBuild DbDataBuild
COPY duckdb.pin ./
COPY scripts/fetch-duckdb.sh scripts/fetch-duckdb.sh
# CI's cache of the archives (.duckdb-archives/, filled by .github/actions/duckdb-cache) is inside the build context; a local build has none and
# downloads from the pin's address (the pattern matches nothing then, and duckdb.pin alone is copied). An archive missing there is downloaded.
COPY duckdb.pin .duckdb-archive[s] /duckdb-archives/
RUN DUCKDB_RELEASE_BASE=file:///duckdb-archives scripts/fetch-duckdb.sh linux-x64
# The same for the polyglot library (.native-archives/, filled by .github/actions/native-cache).
COPY DbDataBuild/scripts/polyglot.pin .native-archive[s] /native-archives/
WORKDIR /src/DbDataBuild
RUN POLYGLOT_RELEASE_BASE=file:///native-archives scripts/fetch-native.sh linux-x64
RUN dotnet publish src/DbDataBuild.Cli -c Release -r linux-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# a shell script in the image is the only way to make the command short; the real program is dbdatabuild.dll
RUN printf '#!/bin/sh\nexec dotnet /app/dbdatabuild.dll "$@"\n' > /usr/local/bin/dbdatabuild \
    && chmod +x /usr/local/bin/dbdatabuild \
    && mkdir /data && chown 1654:1654 /data
USER 1654
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net \
    DBDATAKIT_DATA_BUILD=/data
VOLUME /data
EXPOSE 8470
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dbdatabuild", "health", "check"]
ENTRYPOINT ["dbdatabuild", "server", "run", "--listen", "https://0.0.0.0:8470"]

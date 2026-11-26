# Arrow Flight SQL gateway for Linux containers.
#   docker build -f DbDataGateway/Dockerfile -t dbdatagateway .      (run from the repository root: the build needs Shared/ too)
#   docker run -p 7821:7821 -v ./dbgateway-data:/data dbdatagateway     (/data holds config/ and the generated certificate)
# ODBC sources need drivers installed in the image (see the commented example below); the 32-bit worker is
# Windows-only, so "Bitness": "x86" data sources are not available in containers.
# The runtime that draws an app of views only (plan 26, AP25): the web build of Shared/web/view-app, copied to /app/apps-views below.
FROM node:22 AS views
WORKDIR /web
COPY Shared/web .
RUN npm ci && npm run build -w @dbdatakit/view-app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY Shared Shared
COPY DbDataGateway DbDataGateway
COPY duckdb.pin ./
# the export library embeds the repository's browser and font pins (DbDataKit.Views.Export: a verification line, never a gate), from the root of the repository
COPY chromium.pin fonts.pin ./
COPY scripts/fetch-duckdb.sh scripts/fetch-duckdb.sh
# CI's cache of the archives (.duckdb-archives/, filled by .github/actions/duckdb-cache) is inside the build context; a local build has none and
# downloads from the pin's address (the pattern matches nothing then, and duckdb.pin alone is copied). An archive missing there is downloaded.
COPY duckdb.pin .duckdb-archive[s] /duckdb-archives/
RUN DUCKDB_RELEASE_BASE=file:///duckdb-archives scripts/fetch-duckdb.sh
WORKDIR /src/DbDataGateway
RUN dotnet publish src/DbDataGateway.Server -c Release -o /app --no-self-contained
COPY --from=views /web/view-app/dist /app/apps-views

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends unixodbc \
    && rm -rf /var/lib/apt/lists/*
# Example: Microsoft ODBC Driver 18 for SQL Server
#   RUN apt-get update && apt-get install -y curl gnupg \
#    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg \
#    && echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list \
#    && apt-get update && ACCEPT_EULA=Y apt-get install -y msodbcsql18
WORKDIR /app
COPY --from=build /app .
# The Flight port listens on every address of the container (it is what clients connect to) and is the only port the image exposes. A container has
# ports of its own, so the image sets the two listeners it publishes or keeps private instead of taking free ports of the Gateway's block (a setting of
# the container, set again by any file or variable that names the listener).
# The management API (https, port 7822, plan 33 D15) is on by default but bound to the container's loopback address: nothing outside the container
# reaches it, `-p 7822:7822` publishes nothing, and no console can pair with it until its address is widened. To let a console in another container
# pair with it, set DBDATAKIT_Gateway__Management__Listen=https://0.0.0.0:7822 on a private network and **never publish 7822** (dist/compose/gateway.yml does
# exactly that); to pair from the host, publish it on loopback only (-p 127.0.0.1:7822:7822, with the address 0.0.0.0 inside). Every call needs a
# token that `dbdatagateway server pair` issued, but the port itself is a listener of the admin API: keep it off public interfaces.
# `DBDATAKIT_Gateway__Management__Enabled=false` turns it off. dist/tests checks this block and that no compose file publishes 7822 beyond loopback.
ENV DBDATAKIT_DATA_GATEWAY=/data \
    DBDATAKIT_Gateway__Listen=https://0.0.0.0:7821 \
    DBDATAKIT_Gateway__Management__Listen=https://127.0.0.1:7822
VOLUME /data
EXPOSE 7821
# a bare health check asks the Flight listener the settings above name, on the container's loopback address
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "/app/dbdatagateway.dll", "health", "check"]
ENTRYPOINT ["dotnet", "dbdatagateway.dll", "server", "run"]

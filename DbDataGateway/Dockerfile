# Arrow Flight SQL gateway for Linux containers.
#   docker build -f DbDataGateway/Dockerfile -t dbdatagateway .      (run from the repository root: the build needs Shared/ too)
#   docker run -p 8815:8815 -v ./dbgateway-data:/data dbdatagateway     (/data holds config/ and the generated certificate)
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
COPY DbDataKit DbDataKit
COPY DbDataGateway DbDataGateway
COPY duckdb.pin ./
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
ENV DBDATAKIT_DATA_GATEWAY=/data \
    DBDATAKIT_Gateway__Listen__Address=0.0.0.0
VOLUME /data
EXPOSE 8815
ENTRYPOINT ["dotnet", "dbdatagateway.dll", "server", "run"]

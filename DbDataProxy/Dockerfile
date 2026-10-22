# Arrow Flight SQL proxy for Linux containers.
#   docker build -f DbDataProxy/Dockerfile -t dbdataproxy .      (run from the repository root: the build needs DbDataKit/ too)
#   docker run -p 8815:8815 -v ./dbproxy-data:/data dbdataproxy     (/data holds appsettings.json and the generated certificate)
# ODBC sources need drivers installed in the image (see the commented example below); the 32-bit worker is
# Windows-only, so "Bitness": "x86" data sources are not available in containers.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY DbDataKit DbDataKit
COPY DbDataProxy DbDataProxy
COPY duckdb.pin ./
COPY scripts/fetch-duckdb.sh scripts/fetch-duckdb.sh
RUN scripts/fetch-duckdb.sh
WORKDIR /src/DbDataProxy
RUN dotnet publish src/DbDataProxy.Server -c Release -o /app --no-self-contained

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
ENV DBPROXY_Proxy__DataDirectory=/data \
    DBPROXY_Proxy__Listen__Address=0.0.0.0
VOLUME /data
EXPOSE 8815
ENTRYPOINT ["dotnet", "DbDataProxy.dll"]

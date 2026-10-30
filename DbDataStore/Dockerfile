# The DbDataStore server for Linux containers.
#   docker build -f DbDataStore/Dockerfile -t dbdatastore .        (run from the repository root: the build needs DbDataKit/ too)
#   docker run -p 8490:8490 -v ./store-data:/data dbdatastore        (/data holds the server's identity, paired clients and certificate)
# Pair a console (the commands run inside the container, as the server's own user):
#   docker exec <container> dbdatastore server pair --max-role admin
# or let the server announce itself to a console on a network they share (dist/compose/store.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY DbDataKit DbDataKit
COPY DbDataStore DbDataStore
WORKDIR /src/DbDataStore
RUN dotnet publish src/DbDataStore.Server -c Release -r linux-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# a shell script in the image is the only way to make the command short; the real program is dbdatastore.dll
RUN printf '#!/bin/sh\nexec dotnet /app/dbdatastore.dll "$@"\n' > /usr/local/bin/dbdatastore \
    && chmod +x /usr/local/bin/dbdatastore \
    && mkdir /data && chown 1654:1654 /data
USER 1654
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net \
    DBDATASTORE_SERVER_DIR=/data \
    ASPNETCORE_HTTP_PORTS=
VOLUME /data
EXPOSE 8490
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dbdatastore", "health", "check"]
ENTRYPOINT ["dbdatastore", "server", "run", "--listen", "https://0.0.0.0:8490"]

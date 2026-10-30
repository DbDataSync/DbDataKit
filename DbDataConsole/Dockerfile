# The dbdatakit console for Linux containers.
#   docker build -f DbDataConsole/Dockerfile -t dbdatakit .        (run from the repository root: the build needs DbDataKit/ too)
#   docker run -p 8480:8480 -v ./console-data:/data dbdatakit        (/data holds the users, sessions, invitations, the servers it manages, its signing key and its certificate)
# The first administrator redeems the invitation the console prints in its log and writes to /data/FIRST-RUN.txt; one more can be made at any time:
#   docker exec <container> dbdatakit user invite --url https://<the address people use>
# Pair a server with it (the code is made on the server with 'server pair'):
#   docker exec <container> dbdatakit instance add https://build.example:8470 --code <code>
# A passkey is bound to the address people open the console at, so tell it that address: add  --public-url https://<name>:8480  to the run command (the image says https://localhost:8480).
FROM node:24 AS web
WORKDIR /src/DbDataKit/web
COPY DbDataKit/web/package.json DbDataKit/web/package-lock.json ./
COPY DbDataKit/web/shell/package.json shell/package.json
COPY DbDataKit/web/admin/package.json admin/package.json
COPY DbDataKit/web/sync/package.json sync/package.json
COPY DbDataKit/web/console/package.json console/package.json
RUN npm ci
COPY DbDataKit/web .
# The console serves every module's docs, generated from the module's own folder at build time (U11): the Sync docs and the screenshots they show.
COPY DbDataSync/docs /src/DbDataSync/docs
COPY DbDataSync/screenshots /src/DbDataSync/screenshots
RUN npm run build -w @dbdatakit/console

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY DbDataKit DbDataKit
COPY DbDataConsole DbDataConsole
RUN dotnet publish DbDataConsole/src/DbDataConsole.Server -c Release -r linux-x64 --self-contained false -p:SkipWebBuild=true -p:DebugType=none -p:DebugSymbols=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=web /src/DbDataKit/web/console/dist ./wwwroot
RUN mkdir /data && chown 1654:1654 /data
USER 1654
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net \
    DBDATAKIT_CONSOLE_DIR=/data \
    ASPNETCORE_HTTP_PORTS=
VOLUME /data
EXPOSE 8480
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "/app/dbdatakit.dll", "health", "check"]
ENTRYPOINT ["dotnet", "/app/dbdatakit.dll", "server", "run", "--listen", "https://0.0.0.0:8480", "--public-url", "https://localhost:8480"]

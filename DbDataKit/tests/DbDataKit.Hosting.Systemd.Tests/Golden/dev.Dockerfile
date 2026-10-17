# DEV IMAGE: contains the .NET SDK. Do not use for production; build with the Runtime flavor.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
WORKDIR /app
COPY --chown=1654:1654 publish/ /app/
ENV ASPNETCORE_URLS=http://+:5080 \
    DOTNET_gcServer=0 \
    DBDATA_DATA_DIR=/data
VOLUME ["/data"]
EXPOSE 5080
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "DbDataSync.Api.dll", "--healthcheck", "http://127.0.0.1:5080/health/live"]
ENTRYPOINT ["dotnet", "DbDataSync.Api.dll"]

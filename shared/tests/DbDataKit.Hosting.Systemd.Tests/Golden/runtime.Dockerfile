FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chown=1654:1654 publish/ /app/
RUN mkdir -p /data && chown 1654:1654 /data
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0 \
    DBDATA_DATA_DIR=/data
VOLUME ["/data"]
EXPOSE 8080
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "DbDataProxy.Server.dll", "--healthcheck", "http://127.0.0.1:8080/health/live"]
ENTRYPOINT ["dotnet", "DbDataProxy.Server.dll"]

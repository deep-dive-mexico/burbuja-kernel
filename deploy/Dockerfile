ARG SDK=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${SDK} AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Burbuja.Host/Burbuja.Host.csproj -c Release -m:1 -o /out/host \
    && rm -f /out/host/appsettings.Development.json \
    && dotnet build plugins/Burbuja.Plugins.Store.Sqlite/Burbuja.Plugins.Store.Sqlite.csproj -c Release -m:1 \
    && mkdir -p /out/plugins/Store.Sqlite \
    && cp -R plugins/Burbuja.Plugins.Store.Sqlite/bin/Release/net10.0/. /out/plugins/Store.Sqlite/ \
    && cp plugins/Burbuja.Plugins.Store.Sqlite/plugin.json /out/plugins/Store.Sqlite/ \
    && rm -rf /out/plugins/Store.Sqlite/publish \
    && for banned in Burbuja.Core.dll Burbuja.Core.Sdk.dll Burbuja.Core.Abstractions.dll; do \
         test ! -f "/out/plugins/Store.Sqlite/$banned" || exit 1; \
       done
FROM ${RUNTIME}
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.source="https://github.com/deep-dive-mexico/burbuja-kernel" \
      org.opencontainers.image.revision="${SOURCE_COMMIT}"
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates git tini \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 10001 burbuja && useradd -u 10001 -g 10001 -m -s /usr/sbin/nologin burbuja \
    && mkdir -p /app /var/burbuja/state \
    && chown -R burbuja:burbuja /app /var/burbuja
WORKDIR /app
COPY --from=build --chown=10001:10001 /out/host/ /app/
COPY --from=build --chown=10001:10001 /out/plugins/ /app/plugins/
USER 10001:10001
ENV DOTNET_ENVIRONMENT=Production \
    HOME=/var/burbuja/state \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    Kernel__Role=all \
    Kernel__Seed=false \
    Kernel__PluginsDirectory=/app/plugins \
    Store__Sqlite__Path=/var/burbuja/state/burbuja.db \
    Sqlite__Path=/var/burbuja/state/burbuja.db \
    Sqlite__ArtifactRoot=/var/burbuja/state/artifacts \
    Compute__LocalDirectory__Root=/var/burbuja/state/worktrees \
    Compute__Git__Root=/var/burbuja/state/repos
EXPOSE 8080
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "/app/Burbuja.Host.dll", "--role", "all"]

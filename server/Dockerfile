# Pickyfin Server: personal recommendation rows for Jellyfin.
# Everything is built on the build machine's own CPU (no emulation): the app is
# plain .NET, the same files run on amd64 and arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Time zone data, so TZ (e.g. Europe/Berlin) works for the container's own log
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src/Pickyfin.Server.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="Pickyfin Server" \
      org.opencontainers.image.description="Pickyfin Server: personal recommendation rows for Jellyfin" \
      org.opencontainers.image.source="https://github.com/Sternpaul/pickyfin" \
      org.opencontainers.image.licenses="GPL-2.0"
COPY --from=build /usr/share/zoneinfo /usr/share/zoneinfo
ARG PICKYFIN_VERSION=dev
WORKDIR /app
COPY --from=build /app .
ENV PICKYFIN_VERSION=$PICKYFIN_VERSION \
    PICKYFIN_DATA=/data \
    ASPNETCORE_URLS=http://+:8097
VOLUME /data
EXPOSE 8097
ENTRYPOINT ["dotnet", "Pickyfin.Server.dll"]

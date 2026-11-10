# Pickyfin Server: personal recommendation rows for Jellyfin.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Pickyfin.Server.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PICKYFIN_VERSION=dev
WORKDIR /app
COPY --from=build /app .
ENV PICKYFIN_VERSION=$PICKYFIN_VERSION \
    PICKYFIN_DATA=/data \
    ASPNETCORE_URLS=http://+:8097
VOLUME /data
EXPOSE 8097
ENTRYPOINT ["dotnet", "Pickyfin.Server.dll"]

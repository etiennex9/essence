# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/Essence.Data/Essence.Data.csproj src/Essence.Data/
COPY src/Essence.Ingest/Essence.Ingest.csproj src/Essence.Ingest/
RUN dotnet restore src/Essence.Ingest/Essence.Ingest.csproj
COPY src/Essence.Data/ src/Essence.Data/
COPY src/Essence.Ingest/ src/Essence.Ingest/
RUN dotnet publish src/Essence.Ingest/Essence.Ingest.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
# /data is the app's data folder and the archive a subfolder. Created here so a new named volume mounted on /data inherits
# the app user's ownership.
RUN mkdir -p /data/archive && chown -R "$APP_UID" /data
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Essence.Ingest.dll"]

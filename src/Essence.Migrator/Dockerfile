# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/Essence.Data/Essence.Data.csproj src/Essence.Data/
COPY src/Essence.Migrator/Essence.Migrator.csproj src/Essence.Migrator/
RUN dotnet restore src/Essence.Migrator/Essence.Migrator.csproj
COPY src/Essence.Data/ src/Essence.Data/
COPY src/Essence.Migrator/ src/Essence.Migrator/
RUN dotnet publish src/Essence.Migrator/Essence.Migrator.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Essence.Migrator.dll"]

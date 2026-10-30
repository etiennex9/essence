# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/Essence.Contracts/Essence.Contracts.csproj src/Essence.Contracts/
COPY src/Essence.Data/Essence.Data.csproj src/Essence.Data/
COPY src/Essence.Ranking/Essence.Ranking.csproj src/Essence.Ranking/
COPY src/Essence.Api/Essence.Api.csproj src/Essence.Api/
RUN dotnet restore src/Essence.Api/Essence.Api.csproj
COPY src/Essence.Contracts/ src/Essence.Contracts/
COPY src/Essence.Data/ src/Essence.Data/
COPY src/Essence.Ranking/ src/Essence.Ranking/
COPY src/Essence.Api/ src/Essence.Api/
RUN dotnet publish src/Essence.Api/Essence.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
# The image listens on ASPNETCORE_HTTP_PORTS=8080.
EXPOSE 8080
ENTRYPOINT ["dotnet", "Essence.Api.dll"]

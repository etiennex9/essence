# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/Essence.Contracts/Essence.Contracts.csproj src/Essence.Contracts/
COPY src/Essence.UI/Essence.UI.csproj src/Essence.UI/
COPY src/Essence.Web/Essence.Web.csproj src/Essence.Web/
RUN dotnet restore src/Essence.Web/Essence.Web.csproj
COPY src/Essence.Contracts/ src/Essence.Contracts/
COPY src/Essence.UI/ src/Essence.UI/
COPY src/Essence.Web/ src/Essence.Web/
RUN dotnet publish src/Essence.Web/Essence.Web.csproj --configuration Release --no-restore --output /app \
    # appsettings.json is written at container start (see 40-essence-config.sh); drop the build-time copies.
    && rm /app/wwwroot/appsettings.json*

# A static site: nginx serves the published wwwroot, running as the image's non-root user.
FROM nginxinc/nginx-unprivileged:1.30-alpine AS final
COPY --from=build /app/wwwroot /usr/share/nginx/html
COPY src/Essence.Web/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --chmod=755 src/Essence.Web/nginx/40-essence-config.sh /docker-entrypoint.d/
# The image listens on 8080.
EXPOSE 8080

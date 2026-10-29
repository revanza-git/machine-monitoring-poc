# Builds the server into a runtime-only image. The build context is the
# repository root (see docker-compose.yml) so src/Shared is visible.
# Nothing machine-specific is baked in: appsettings.Local.json and
# machines.json are excluded by .dockerignore and mounted at run time.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Shared/Shared.csproj src/Shared/
COPY src/Server/Server.csproj src/Server/
RUN dotnet restore src/Server/Server.csproj
COPY src/Shared/ src/Shared/
COPY src/Server/ src/Server/
RUN dotnet publish src/Server/Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Server.dll"]

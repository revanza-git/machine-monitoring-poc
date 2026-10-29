# Builds the simulator into a runtime-only image. The build context is the
# repository root (see docker-compose.yml) so src/Shared and the linked
# src/Agent/Backoff.cs are visible. The server's appsettings.json rides along
# so `prepare` can copy its Machines entry to index 0 of machines.json.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Shared/Shared.csproj src/Shared/
COPY tools/FleetSim/FleetSim.csproj tools/FleetSim/
RUN dotnet restore tools/FleetSim/FleetSim.csproj
COPY src/Shared/ src/Shared/
COPY src/Agent/Backoff.cs src/Agent/
COPY tools/FleetSim/ tools/FleetSim/
RUN dotnet publish tools/FleetSim/FleetSim.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
COPY src/Server/appsettings.json /app/server-appsettings.json
# Where things are inside the compose network; every flag can still override.
ENV FLEETSIM_SERVER=https://server:7181 \
    FLEETSIM_SERVER_SETTINGS=/app/server-appsettings.json \
    FLEETSIM_MACHINES_FILE=/config/machines.json \
    FLEETSIM_RESULTS=/results
ENTRYPOINT ["dotnet", "FleetSim.dll"]

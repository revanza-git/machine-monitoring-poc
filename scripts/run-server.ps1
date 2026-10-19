# Starts the central API + portal on the dev HTTPS certificate.
# Prerequisite: `docker compose up -d` so the database is reachable.
$root = Split-Path $PSScriptRoot -Parent
dotnet run --project "$root\src\Server\Server.csproj" --launch-profile https

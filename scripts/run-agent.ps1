# Starts the agent as a console app on this Windows machine.
# It samples real performance counters, so it never runs inside Docker.
$root = Split-Path $PSScriptRoot -Parent
dotnet run --project "$root\src\Agent\Agent.csproj"

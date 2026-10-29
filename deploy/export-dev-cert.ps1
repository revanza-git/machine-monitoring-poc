# Exports the ASP.NET Core development certificate so the server container can
# serve HTTPS with the same certificate the host already uses. Run once; the
# file lands in the git-ignored deploy/certs folder that docker-compose.yml
# mounts. The password must match ASPNETCORE_Kestrel__Certificates__Default__Password
# in docker-compose.yml. Development only.
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "deploy\certs\server.pfx"
dotnet dev-certs https -ep $out -p dev
Write-Host "Exported to $out. Now: docker compose up -d"

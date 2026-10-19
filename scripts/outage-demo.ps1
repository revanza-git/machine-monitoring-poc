<#
.SYNOPSIS
  Shows what the agent does when the server disappears and comes back.

.DESCRIPTION
  1. Starts the server and the agent (Development profile: 10 s intervals).
  2. Lets them run until the first batches are acknowledged.
  3. Kills the server. Watch the agent window: uploads fail, retries back
     off with jitter, sampling continues and the outbox depth climbs.
  4. Restarts the server. Watch the backlog drain in one batch, and the
     chart fill in with the original sampled_at timestamps: a delay, not a hole.
  5. Prints what the API reports before and after.

  Prerequisites: docker compose up -d, and the solution built once.
  The server and agent open in their own windows so their logs stay visible.
#>
param(
    [int]$OutageSeconds = 60,
    [string]$BaseUrl = "https://localhost:7181"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ENVIRONMENT = "Development"

# PowerShell 5.1 cannot skip certificate checks on Invoke-RestMethod; curl.exe can.
function Api($path) { curl.exe -sk "$BaseUrl$path" | ConvertFrom-Json }

function Start-Server {
    Start-Process dotnet -ArgumentList "run --project `"$root\src\Server\Server.csproj`" --launch-profile https --no-build" -WindowStyle Normal
    Write-Host "  waiting for the server..." -NoNewline
    for ($i = 0; $i -lt 60; $i++) {
        try { if ((curl.exe -sk -o NUL -w "%{http_code}" "$BaseUrl/health") -eq "200") { break } } catch {}
        Start-Sleep -Seconds 1
        Write-Host "." -NoNewline
    }
    Write-Host " up"
}

function Stop-Server { Get-Process -Name Server -ErrorAction SilentlyContinue | Stop-Process -Force }

function Show-Fleet($label) {
    $m = (Api "/api/machines")[0]
    Write-Host ("  [{0}] {1}: status={2} last_seen={3:HH:mm:ss}Z outbox_depth={4}" -f $label, $m.name, $m.status, ([datetime]$m.last_seen).ToUniversalTime(), $m.outbox_depth)
}

Write-Host "== 1. Start server and agent =="
Start-Server
Start-Process dotnet -ArgumentList "run --project `"$root\src\Agent\Agent.csproj`" --no-build" -WindowStyle Normal
Write-Host "  letting the agent settle for 30 s"
Start-Sleep -Seconds 30
Show-Fleet "before outage"

Write-Host "== 2. Kill the server for $OutageSeconds s =="
Stop-Server
Write-Host "  watch the agent window: 'Upload ... failed', 'Retrying in Ns', outbox depth climbing"
$outageStart = Get-Date
Start-Sleep -Seconds $OutageSeconds

Write-Host "== 3. Server returns =="
Start-Server
Write-Host "  watch the agent window: 'Sent N rows' in one batch"
Write-Host "  (the agent's Development backoff tops out at 30 s, so the reconnect can take up to that long)"
Start-Sleep -Seconds 45
Show-Fleet "after recovery"

$from = $outageStart.ToUniversalTime().AddSeconds(-5).ToString("o")
$to = (Get-Date).ToUniversalTime().ToString("o")
$metrics = Api "/api/machines/$((Api '/api/machines')[0].machine_id)/metrics?from=$from&to=$to"
Write-Host ("  chart points covering the outage window: {0} (sampled while the server was down, stored afterwards)" -f $metrics.points.Count)
Write-Host "  open $BaseUrl and look at the last hour: no hole, just a delay."

Write-Host "== done; server and agent windows stay open. Ctrl+C the agent to see its stopping heartbeat and the 'off' status. =="

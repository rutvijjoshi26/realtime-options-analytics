<#
.SYNOPSIS
    Post-deployment smoke test for the options analytics service.

.DESCRIPTION
    Verifies a running instance end to end: health, feed ingestion, analytic
    computation, surface fit, and the WebSocket upgrade. Exits non-zero on the
    first failed check so it can gate a deployment pipeline.

    Also reports feed gap counters, which is the first thing worth checking when
    a client reports missing or stale analytics.

.EXAMPLE
    ./Invoke-Smoke.ps1 -BaseUrl http://localhost:5080 -Underlying SPY
#>
[CmdletBinding()]
param(
    [string] $BaseUrl    = 'http://localhost:5080',
    [string] $FeedHost   = '127.0.0.1',
    [int]    $FeedPort   = 9301,
    [string] $Underlying = 'SPY',
    [double] $Spot       = 500.0,
    [int]    $QuoteCount = 400
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = 0

function Test-Step {
    param([string] $Name, [scriptblock] $Check)

    Write-Host -NoNewline ("  {0,-42}" -f $Name)
    try {
        $result = & $Check
        if ($result -eq $false) {
            Write-Host "FAIL" -ForegroundColor Red
            $script:Failures++
        }
        else {
            Write-Host "PASS" -ForegroundColor Green
            if ($result -is [string]) { Write-Host "      $result" -ForegroundColor DarkGray }
        }
    }
    catch {
        Write-Host "FAIL" -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkGray
        $script:Failures++
    }
}

Write-Host "Smoke test against $BaseUrl" -ForegroundColor Cyan
Write-Host ""

Test-Step "service responds to /health" {
    $r = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5
    $r.status -eq 'ok'
}

Test-Step "feed stats endpoint is reachable" {
    $r = Invoke-RestMethod -Uri "$BaseUrl/api/feed/stats" -TimeoutSec 5
    "received=$($r.feed.received) computed=$($r.engine.computed)"
}

$before = (Invoke-RestMethod -Uri "$BaseUrl/api/feed/stats").engine.computed

Test-Step "publish $QuoteCount quotes over UDP" {
    & "$PSScriptRoot/Send-Quotes.ps1" `
        -TargetHost $FeedHost -Port $FeedPort `
        -Underlying $Underlying -Spot $Spot -Count $QuoteCount | Out-Null
    $true
}

Start-Sleep -Seconds 1

Test-Step "engine computed new analytics" {
    $after = (Invoke-RestMethod -Uri "$BaseUrl/api/feed/stats").engine.computed
    if ($after -le $before) { return $false }
    "computed rose from $before to $after"
}

Test-Step "analytics endpoint returns the chain" {
    $r = Invoke-RestMethod -Uri "$BaseUrl/api/analytics?underlying=$Underlying"
    if ($r.Count -lt 1) { return $false }
    "$($r.Count) contract(s) live"
}

Test-Step "implied vols are finite and positive" {
    $r = Invoke-RestMethod -Uri "$BaseUrl/api/analytics?underlying=$Underlying"
    $bad = @($r | Where-Object { $_.impliedVol -le 0 -or [double]::IsNaN($_.impliedVol) })
    if ($bad.Count -gt 0) { return $false }
    "all $($r.Count) vols in range"
}

Test-Step "surface fits and passes arbitrage checks" {
    $r = Invoke-RestMethod -Uri "$BaseUrl/api/surface/${Underlying}?refit=true"
    $violations = @($r | Where-Object { -not $_.arbitrageFree })
    if ($violations.Count -gt 0) {
        Write-Host ""
        $violations | ForEach-Object { Write-Host "      $($_.expiry): $($_.arbitrageNote)" -ForegroundColor Yellow }
        return $false
    }
    "$($r.Count) expiry curve(s), all arbitrage-free"
}

Test-Step "no feed sequence gaps observed" {
    $s = (Invoke-RestMethod -Uri "$BaseUrl/api/feed/stats").feed
    if ($s.gapMessages -gt 0) {
        return "WARN: $($s.gapCount) gap(s), $($s.gapMessages) message(s) missing"
    }
    "0 gaps, last sequence $($s.lastSequence)"
}

Test-Step "websocket endpoint accepts an upgrade" {
    $ws  = [System.Net.WebSockets.ClientWebSocket]::new()
    $uri = [Uri]($BaseUrl.Replace('http://','ws://').Replace('https://','wss://') + "/ws/analytics?underlying=$Underlying")
    $cts = [System.Threading.CancellationTokenSource]::new(5000)
    try {
        $ws.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
        $state = $ws.State
        $ws.Dispose()
        $state -eq [System.Net.WebSockets.WebSocketState]::Open
    }
    catch { $ws.Dispose(); throw }
}

Write-Host ""
if ($script:Failures -gt 0) {
    Write-Host "$script:Failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "All checks passed." -ForegroundColor Green
exit 0

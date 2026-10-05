<#
.SYNOPSIS
    Publishes synthetic NBBO option quotes to the analytics service feed port.

.DESCRIPTION
    Encodes the 56-byte binary quote layout the feed handler expects and sends
    it over UDP. Useful for driving the service in a dev environment, for smoke
    testing a deployment, and for reproducing a client-reported analytic against
    a known input.

    Wire layout (little-endian):
        [0..7]   underlying, ASCII, null padded
        [8..11]  expiry as packed yyyyMMdd int
        [12..19] strike, double
        [20]     right (0 = call, 1 = put)
        [21..23] padding
        [24..31] bid, double
        [32..39] ask, double
        [40..47] underlying price, double
        [48..55] sequence number, long

.PARAMETER Underlying
    Root symbol, 8 ASCII characters or fewer.

.PARAMETER Count
    Number of quotes to publish across the generated chain.

.EXAMPLE
    ./Send-Quotes.ps1 -Underlying SPY -Spot 505 -Count 500

.EXAMPLE
    ./Send-Quotes.ps1 -Underlying QQQ -Spot 440 -Count 2000 -DelayMs 5 -Verbose
#>
[CmdletBinding()]
param(
    [string]   $TargetHost = '127.0.0.1',
    [int]      $Port       = 9301,
    [string]   $Underlying = 'SPY',
    [double]   $Spot       = 500.0,
    [int]      $Count      = 250,
    [int]      $DelayMs    = 0,
    [datetime] $Expiry     = (Get-Date).AddDays(45)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Underlying.Length -gt 8) {
    throw "Underlying '$Underlying' exceeds the 8-byte symbol field."
}

function New-QuoteDatagram {
    param(
        [string]   $Symbol,
        [datetime] $ExpiryDate,
        [double]   $Strike,
        [int]      $Right,
        [double]   $Bid,
        [double]   $Ask,
        [double]   $UnderlyingPrice,
        [long]     $Sequence
    )

    $buffer = New-Object byte[] 56

    $symbolBytes = [System.Text.Encoding]::ASCII.GetBytes($Symbol)
    [Array]::Copy($symbolBytes, 0, $buffer, 0, $symbolBytes.Length)

    $packedExpiry = [int]($ExpiryDate.Year * 10000 + $ExpiryDate.Month * 100 + $ExpiryDate.Day)
    [Array]::Copy([BitConverter]::GetBytes($packedExpiry),        0, $buffer,  8, 4)
    [Array]::Copy([BitConverter]::GetBytes([double]$Strike),      0, $buffer, 12, 8)
    $buffer[20] = [byte]$Right
    [Array]::Copy([BitConverter]::GetBytes([double]$Bid),         0, $buffer, 24, 8)
    [Array]::Copy([BitConverter]::GetBytes([double]$Ask),         0, $buffer, 32, 8)
    [Array]::Copy([BitConverter]::GetBytes([double]$UnderlyingPrice), 0, $buffer, 40, 8)
    [Array]::Copy([BitConverter]::GetBytes([long]$Sequence),      0, $buffer, 48, 8)

    return $buffer
}

# Approximate an option premium well enough to produce a plausible two-sided
# market. This is intentionally not a full pricer: the service computes the real
# analytics, and the generator only needs to emit quotes inside the arbitrage
# bounds so they are not rejected at the edge.
function Get-ApproximatePremium {
    param([double] $S, [double] $K, [double] $T, [double] $Vol, [int] $Right)

    $intrinsic = if ($Right -eq 0) { [Math]::Max($S - $K, 0) } else { [Math]::Max($K - $S, 0) }
    $timeValue = 0.4 * $S * $Vol * [Math]::Sqrt($T) * [Math]::Exp(-2.0 * [Math]::Pow([Math]::Log($K / $S), 2))
    return $intrinsic + [Math]::Max($timeValue, 0.05)
}

$client = New-Object System.Net.Sockets.UdpClient
try {
    $client.Connect($TargetHost, $Port)

    $years    = [Math]::Max(($Expiry - (Get-Date)).TotalDays / 365.0, 0.01)
    $sequence = 0L
    $sent     = 0

    Write-Host "Publishing $Count quote(s) for $Underlying to ${TargetHost}:${Port}" -ForegroundColor Cyan
    Write-Host "  spot=$Spot  expiry=$($Expiry.ToString('yyyy-MM-dd'))  T=$([Math]::Round($years,4))y"

    :outer while ($sent -lt $Count) {
        for ($pct = 0.75; $pct -le 1.25; $pct += 0.025) {

            $strike = [Math]::Round($Spot * $pct, 2)
            $x      = [Math]::Log($strike / $Spot)
            $vol    = [Math]::Max(0.12, 0.22 - 0.12 * $x + 0.45 * $x * $x)

            foreach ($right in 0, 1) {
                $fair       = Get-ApproximatePremium -S $Spot -K $strike -T $years -Vol $vol -Right $right
                $halfSpread = [Math]::Max(0.01, $fair * 0.01)
                $sequence++

                $datagram = New-QuoteDatagram `
                    -Symbol $Underlying -ExpiryDate $Expiry -Strike $strike -Right $right `
                    -Bid ($fair - $halfSpread) -Ask ($fair + $halfSpread) `
                    -UnderlyingPrice $Spot -Sequence $sequence

                [void]$client.Send($datagram, $datagram.Length)
                $sent++

                Write-Verbose ("seq={0,-5} {1} {2} {3} bid={4:N2} ask={5:N2}" -f `
                    $sequence, $Underlying, $strike, $(if ($right -eq 0) { 'C' } else { 'P' }), `
                    ($fair - $halfSpread), ($fair + $halfSpread))

                if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
                if ($sent -ge $Count) { break outer }
            }
        }
    }

    Write-Host "Sent $sent quote(s), final sequence $sequence." -ForegroundColor Green
}
finally {
    $client.Close()
    $client.Dispose()
}

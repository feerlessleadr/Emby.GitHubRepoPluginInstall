<#
.SYNOPSIS
  Live check of the alerts feature against a running Emby server, connecting the way the Embidextrous app does.

.DESCRIPTION
  The token goes in the X-Emby-Token header (to connect) and in the start message body (to subscribe), the deviceId is in the address, and there is NO api_key anywhere.
  (A test that connects with api_key proves nothing about the app: Emby treats that socket differently.)

  Steps: Info (ApiLevel and Features), Notifications/Catalog, Events, then a websocket subscription that prints every
  message for a while. With -TriggerCheck it starts a check-only run (POST /run, Mode Check) so a RunCompleted event
  appears while the socket listens. The plugin's phone API switch must be on in its settings.

  Expected: the first message right after Start has the events after -AfterSeq (always sent; 0 gives the whole log; if you could omit it you would get an empty list with LatestSeq when
  -AfterSeq is left out), a message within about a second of RunCompleted, and a heartbeat with "Events":[] every
  30 seconds. With a non-administrator token the only message is {"Error":"Administrator login required."}.

.EXAMPLE
  $env:EMBY_TOKEN = '<administrator token>'
  .\Test-EventsSubscription.ps1 -Server http://emby-host:8096 -Seconds 75 -TriggerCheck
#>
param(
    [Parameter(Mandatory = $true)][string] $Server,
    [string] $Token = $env:EMBY_TOKEN,
    [long]   $AfterSeq = -1,
    [int]    $Seconds = 70,
    [switch] $TriggerCheck
)

$ErrorActionPreference = 'Stop'
if (-not $Token) { throw 'Give -Token or set EMBY_TOKEN.' }

$base = $Server.TrimEnd('/')
$api  = "$base/emby/Plugins/GitHubRepoPluginInstall"
$headers = @{ 'X-Emby-Token' = $Token }

function Show($title, $obj) {
    Write-Host "== $title"
    ($obj | ConvertTo-Json -Depth 8 -Compress) | Write-Host
}

Show 'Info' (Invoke-RestMethod -Uri "$api/info" -Headers $headers)
Show 'Catalog' (Invoke-RestMethod -Uri "$api/Notifications/Catalog" -Headers $headers)
Show 'Events (After=0)' (Invoke-RestMethod -Uri "$api/Events?After=0&Limit=5" -Headers $headers)

# The websocket address: ws or wss, the deviceId, nothing else. No api_key.
$wsBase = $base -replace '^http', 'ws'
$uri = [Uri]("$wsBase/embywebsocket?deviceId=plugin-events-live-test")
if ($uri.Query -match 'api_key') { throw 'The address must not carry api_key.' }

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.Options.SetRequestHeader('X-Emby-Token', $Token)
$cts = New-Object System.Threading.CancellationTokenSource
$ws.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()
Write-Host "== Connected (token in header only)"

function Send-Text($text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()
}

# Third value: the last Seq seen (-AfterSeq), or empty for a baseline only. Then the administrator token, never in the address
$afterText = if ($AfterSeq -ge 0) { "$AfterSeq" } else { '' }   # empty = baseline only
Send-Text ('{"MessageType":"GitHubRepoPluginInstallEventsStart","Data":"0,30000,' + $afterText + ',' + $Token + '"}')
Write-Host "== Sent GitHubRepoPluginInstallEventsStart (token in the body, not printed)"

if ($TriggerCheck) {
    Start-Sleep -Seconds 3
    Write-Host '== Starting a check-only run'
    Invoke-RestMethod -Uri "$api/run" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"Mode":"Check"}' | Out-Null
}

$buffer = New-Object byte[] 65536
$deadline = (Get-Date).AddSeconds($Seconds)
$received = 0
$sb = New-Object System.Text.StringBuilder
$pending = $null
# A receive is never cancelled (cancelling would abort the socket); it is waited on with a timeout instead
while ((Get-Date) -lt $deadline) {
    if ($null -eq $pending) {
        $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $buffer)
        $pending = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
    }
    $remaining = [int]($deadline - (Get-Date)).TotalMilliseconds
    if ($remaining -le 0) { break }
    if (-not $pending.Wait([Math]::Min($remaining, 1000))) { continue }
    $r = $pending.Result
    $pending = $null
    if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { Write-Host '== The server closed the socket'; break }
    [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
    if (-not $r.EndOfMessage) { continue }

    $text = $sb.ToString()
    [void]$sb.Clear()
    $stamp = (Get-Date).ToString('HH:mm:ss')
    if ($text -match 'GitHubRepoPluginInstallEvents') {
        $received++
        Write-Host "[$stamp] $text"
    } else {
        Write-Host "[$stamp] (other Emby message, ignored) $($text.Substring(0, [Math]::Min(80, $text.Length)))"
    }
}

try { Send-Text '{"MessageType":"GitHubRepoPluginInstallEventsStop"}' } catch { }
try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch { }
Write-Host "== Done: $received GitHubRepoPluginInstallEvents message(s) in $Seconds seconds"

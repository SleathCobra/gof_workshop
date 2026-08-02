[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [Parameter(Mandatory = $true)]
    [string]$Url,

    [Parameter(Mandatory = $true)]
    [string]$Screenshot,

    [ValidateRange(1024, 65535)]
    [int]$DebugPort = 9231,

    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 60,

    [switch]$SkipWebGlValidation
)

$ErrorActionPreference = 'Stop'
$browser = (Resolve-Path -LiteralPath $Executable).Path
$screenshotPath = [IO.Path]::GetFullPath($Screenshot)
$screenshotDirectory = [IO.Path]::GetDirectoryName($screenshotPath)
[IO.Directory]::CreateDirectory($screenshotDirectory) | Out-Null
$profile = [IO.Path]::Combine(
    [IO.Path]::GetDirectoryName($screenshotDirectory),
    'browser-smoke-profile-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($profile) | Out-Null

$arguments = @(
    '--headless=new',
    '--no-first-run',
    '--disable-default-apps',
    '--disable-extensions',
    '--ignore-gpu-blocklist',
    '--window-size=1600,1000',
    "--remote-debugging-port=$DebugPort",
    "--user-data-dir=$profile",
    'about:blank'
)

$process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru
$socket = [Net.WebSockets.ClientWebSocket]::new()
$nextId = 0
$events = [Collections.Generic.List[string]]::new()

function Receive-CdpMessage {
    $buffer = [byte[]]::new(65536)
    $stream = [IO.MemoryStream]::new()
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        do {
            $segment = [ArraySegment[byte]]::new($buffer)
            $receive = $socket.ReceiveAsync($segment, $cancellation.Token)
            if (!$receive.Wait([TimeSpan]::FromSeconds(15))) {
                $cancellation.Cancel()
                $socket.Abort()
                throw [TimeoutException]::new('Timed out waiting for a DevTools response.')
            }
            $result = $receive.GetAwaiter().GetResult()
            if ($result.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) {
                throw 'The browser closed the DevTools socket.'
            }
            $stream.Write($buffer, 0, $result.Count)
            if ($stream.Length -gt 16MB) {
                throw 'A DevTools response exceeded the 16 MiB smoke-test limit.'
            }
        } while (!$result.EndOfMessage)
        return [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 100
    }
    finally {
        $cancellation.Dispose()
        $stream.Dispose()
    }
}

function Invoke-Cdp {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [hashtable]$Parameters = @{}
    )

    $script:nextId++
    $request = @{ id = $script:nextId; method = $Method; params = $Parameters } | ConvertTo-Json -Compress -Depth 100
    $bytes = [Text.Encoding]::UTF8.GetBytes($request)
    Write-Verbose "CDP -> $Method ($($script:nextId))"
    $send = $socket.SendAsync(
        [ArraySegment[byte]]::new($bytes),
        [Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None)
    if (!$send.Wait([TimeSpan]::FromSeconds(15))) {
        $socket.Abort()
        throw [TimeoutException]::new("Timed out sending DevTools command $Method.")
    }
    $send.GetAwaiter().GetResult() | Out-Null

    while ($true) {
        $message = Receive-CdpMessage
        if ($message.method -eq 'Runtime.exceptionThrown' -and $script:events.Count -lt 20) {
            $details = $message.params.exceptionDetails
            $script:events.Add("exception: $($details.text) $($details.exception.description)")
        }
        elseif ($message.method -eq 'Runtime.consoleAPICalled' -and
                $message.params.type -in @('error', 'warning') -and
                $script:events.Count -lt 20) {
            $consoleText = ($message.params.args | ForEach-Object { $_.value ?? $_.description }) -join ' '
            $script:events.Add("$($message.params.type): $consoleText")
        }
        if ($message.id -eq $script:nextId) {
            if ($message.error) {
                throw "CDP $Method failed: $($message.error.message)"
            }
            Write-Verbose "CDP <- $Method ($($script:nextId))"
            return $message.result
        }
    }
}

function Evaluate-InBrowser {
    param([Parameter(Mandatory = $true)][string]$Expression)
    $result = Invoke-Cdp -Method 'Runtime.evaluate' -Parameters @{
        expression = $Expression
        returnByValue = $true
        awaitPromise = $true
    }
    if ($result.exceptionDetails) {
        throw "Browser evaluation failed: $($result.exceptionDetails.text) $($result.exceptionDetails.exception.description)"
    }
    return $result.result.value
}

try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $targets = $null
    do {
        try {
            $targets = Invoke-RestMethod "http://127.0.0.1:$DebugPort/json/list" -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    } while (!$targets -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (!$targets) {
        throw "The browser did not expose DevTools on port $DebugPort."
    }

    $target = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
    if (!$target) {
        throw 'No browser page target was available.'
    }

    Write-Verbose "Connecting to $($target.webSocketDebuggerUrl)"
    $connect = $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None)
    if (!$connect.Wait([TimeSpan]::FromSeconds(15))) {
        $socket.Abort()
        throw [TimeoutException]::new('Timed out connecting to the browser DevTools socket.')
    }
    $connect.GetAwaiter().GetResult() | Out-Null
    Invoke-Cdp -Method 'Runtime.enable' | Out-Null
    Invoke-Cdp -Method 'Page.enable' | Out-Null
    Invoke-Cdp -Method 'Page.navigate' -Parameters @{ url = $Url } | Out-Null

    $state = $null
    do {
        $state = Evaluate-InBrowser @'
(() => ({
  ready: document.body?.dataset.workshopSmoke || '',
  webgl: document.body?.dataset.workshopWebglStatus || '',
  frames: Number(document.body?.dataset.workshopWebglFrames || 0),
  frameMs: Number(document.body?.dataset.workshopWebglFrameMs || 0),
  selected: Number(document.body?.dataset.workshopWebglSelected || -1),
  canvas: Boolean(document.getElementById('workshop-webgl-viewport')),
  storageProfile: (() => { try { return localStorage.getItem('gof2workshop.profile') || ''; } catch { return ''; } })()
}))()
'@
        if ($state.ready -eq 'fail') {
            throw "The in-app browser smoke scenario reported failure (WebGL state: $($state.webgl))."
        }
        if ($state.ready -ne 'pass') {
            Write-Verbose "Smoke state: ready=$($state.ready), webgl=$($state.webgl), frames=$($state.frames)"
            Start-Sleep -Seconds 1
        }
    } while ($state.ready -ne 'pass' -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($state.ready -ne 'pass') {
        $pageState = Evaluate-InBrowser @'
(() => ({
  text: document.body?.innerText.slice(0, 1000) || '',
  htmlCanvases: document.querySelectorAll('canvas').length,
  resources: performance.getEntriesByType('resource').slice(-20).map(entry => ({ name: entry.name, duration: entry.duration, bytes: entry.transferSize })),
  readyState: document.readyState,
  url: location.href
}))()
'@
        throw "The browser smoke scenario timed out (WebGL state: $($state.webgl), frames: $($state.frames), events: $($events -join ' | '), page: $($pageState | ConvertTo-Json -Compress -Depth 10))."
    }

    $framesBefore = $state.frames
    $diagnostics = $null
    if (!$SkipWebGlValidation) {
        Evaluate-InBrowser 'globalThis.workshopWebGlSmoke.orbit()' | Out-Null
        Start-Sleep -Milliseconds 350
        $diagnostics = Evaluate-InBrowser 'JSON.parse(globalThis.workshopWebGlSmoke.diagnostics())'
        if ($diagnostics.frames -le $framesBefore) {
            throw 'The WebGL camera interaction did not schedule and render another frame.'
        }
    }

    $capture = Invoke-Cdp -Method 'Page.captureScreenshot' -Parameters @{
        format = 'png'
        captureBeyondViewport = $false
        fromSurface = $true
    }
    [IO.File]::WriteAllBytes($screenshotPath, [Convert]::FromBase64String($capture.data))

    $contextLossRequested = $false
    $afterLoss = $null
    if (!$SkipWebGlValidation) {
        $contextLossRequested = Evaluate-InBrowser 'globalThis.workshopWebGlSmoke.contextLoss()'
        if ($contextLossRequested) {
            Start-Sleep -Milliseconds 1000
        }
        $afterLoss = Evaluate-InBrowser @'
(() => ({
  status: document.body.dataset.workshopWebglStatus || '',
  smoke: document.body.dataset.workshopSmoke || '',
  diagnostics: JSON.parse(globalThis.workshopWebGlSmoke.diagnostics())
}))()
'@
    }

    [pscustomobject]@{
        Browser = (Get-Item $browser).VersionInfo.ProductVersion
        Url = $Url
        Smoke = $state.ready
        WebGL = $state.webgl
        FramesBeforeInteraction = $framesBefore
        FramesAfterInteraction = if ($diagnostics) { $diagnostics.frames } else { $state.frames }
        FrameMilliseconds = if ($diagnostics) { [math]::Round([double]$diagnostics.lastFrameMs, 3) } else { 0 }
        Renderer = if ($diagnostics) { $diagnostics.renderer } else { '' }
        Vendor = if ($diagnostics) { $diagnostics.vendor } else { '' }
        Version = if ($diagnostics) { $diagnostics.version } else { '' }
        MaxTextureSize = if ($diagnostics) { $diagnostics.maxTextureSize } else { 0 }
        Meshes = if ($diagnostics) { $diagnostics.meshes } else { 0 }
        DrawCalls = if ($diagnostics) { $diagnostics.drawCalls } else { 0 }
        StorageProfile = $state.storageProfile
        ContextLossRequested = [bool]$contextLossRequested
        ContextStatus = if ($afterLoss) { $afterLoss.status } else { '' }
        ContextLosses = if ($afterLoss) { $afterLoss.diagnostics.contextLosses } else { 0 }
        Screenshot = $screenshotPath
        ScreenshotBytes = (Get-Item $screenshotPath).Length
    } | ConvertTo-Json -Depth 20
}
finally {
    $socket.Abort()
    $socket.Dispose()
    $ownedProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($ownedProcess) {
        Stop-Process -Id $process.Id -Force
    }
}

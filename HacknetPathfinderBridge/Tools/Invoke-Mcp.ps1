param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\Output\Control')
)
$ErrorActionPreference = 'Stop'
$payload = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$hostPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Output\HacknetPathfinderBridge\Host\HacknetPathfinderBridge.Host.exe'))
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($hostPath)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$process = [Diagnostics.Process]::Start($start)
function Send-Request($id, $method, $parameters) {
    $request = @{jsonrpc='2.0';id=$id;method=$method;params=$parameters} | ConvertTo-Json -Depth 20 -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $read = $process.StandardOutput.ReadLineAsync()
    if (!$read.Wait(16000)) { throw 'MCP response timed out; do not retry a mutation blindly.' }
    if ($null -eq $read.Result) { throw 'MCP host closed unexpectedly.' }
    $response = $read.Result | ConvertFrom-Json
    if ($null -ne $response.error) { throw ($response.error | ConvertTo-Json -Compress) }
    return $response
}
try {
    $null = Send-Request 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='HnpbOperator';version='1.0'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $response = Send-Request 2 'tools/call' @{name=$payload.tool;arguments=$payload.arguments}
    $response | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'last-response.json') -Encoding utf8
    $image = @($response.result.content | Where-Object type -eq 'image')
    if ($image.Count -gt 0) {
        $imagePath = Join-Path $OutputDirectory 'latest.png'
        [IO.File]::WriteAllBytes($imagePath, [Convert]::FromBase64String($image[0].data))
    }
    $result = $response.result.structuredContent
    $data = $result.data
    if ($null -ne $data.State) {
        [pscustomobject]@{ok=$result.ok;error=$result.error;State=$data.State | Select-Object InstanceId,SessionEpoch,Paused,OsReady,GameTimeSeconds,Terminal,ConnectedNode,Trace,ErrorsObserved,LastError;Ui=$data.Ui;Activated=$data.Activated;UpdateBodies=$data.UpdateBodies;DrawBodies=$data.DrawBodies;ImagePath=$imagePath} | ConvertTo-Json -Depth 12 -Compress
    } else { $result | ConvertTo-Json -Depth 15 -Compress }
} finally {
    $process.StandardInput.Close()
    if (!$process.WaitForExit(3000)) { $process.Kill() }
    $process.Dispose()
}

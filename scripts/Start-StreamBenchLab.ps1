<#
.SYNOPSIS
    Launches the StreamBench multi-stream lab with working defaults.

.DESCRIPTION
    Thin launcher: resolves the application, forwards ONLY the options you pass explicitly (so values in a
    configuration file are never replaced by script defaults) and propagates the application's exit code.
    StreamBench itself owns the RTSP relay, publishers, benchmark commands, validation and cleanup;
    this script never starts FFmpeg or MediaMTX processes itself.

    Exit codes: 0 PASS, 1 WARN, 2 FAIL, 3 could not run (missing dependency, busy port, invalid configuration).

.EXAMPLE
    .\Start-StreamBenchLab.ps1
.EXAMPLE
    .\Start-StreamBenchLab.ps1 -Streams 8 -Duration "00:30:00"
.EXAMPLE
    .\Start-StreamBenchLab.ps1 -Config .\configs\nas-lab.json
.EXAMPLE
    .\Start-StreamBenchLab.ps1 -Profile original,dual-copy -Execution sequential
.EXAMPLE
    .\Start-StreamBenchLab.ps1 -Headless --relay-exe D:\tools\mediamtx.exe --http-port 18088
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 256)]
    [int]$Streams = 4,

    [ValidateRange(1, 64)]
    [int]$ConsumersPerStream = 1,

    [string]$Duration = "00:10:00",

    [string]$Profile = "dual-copy",

    [string]$Config,

    [string]$Preset,

    [ValidateSet("parallel", "sequential", "staged-load")]
    [string]$Execution,

    [switch]$Headless,

    # Any further StreamBench options are passed through unchanged, e.g.
    #   .\Start-StreamBenchLab.ps1 -Streams 2 --relay external --relay-url rtsp://127.0.0.1:8554
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = "Stop"

# app\StreamBench.exe (Windows) or app/StreamBench (other platforms), next to this script
$exe = @(
    (Join-Path (Join-Path $PSScriptRoot "app") "StreamBench.exe"),
    (Join-Path (Join-Path $PSScriptRoot "app") "StreamBench")
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if (-not $exe) {
    throw "StreamBench executable not found: $(Join-Path (Join-Path $PSScriptRoot 'app') 'StreamBench.exe')"
}

$cliArgs = @("lab")

if ($Config) {
    $cliArgs += @("--config", $Config)
}

$overrides = [ordered]@{
    Streams            = "--streams"
    ConsumersPerStream = "--consumers-per-stream"
    Duration           = "--duration"
    Profile            = "--profiles"
    Preset             = "--preset"
    Execution          = "--execution"
}

foreach ($name in $overrides.Keys) {
    if ($PSBoundParameters.ContainsKey($name)) {
        $cliArgs += @($overrides[$name], [string]$PSBoundParameters[$name])
    }
}

if ($Headless) {
    $cliArgs += "--no-tui"
}

if ($ExtraArgs) {
    $cliArgs += $ExtraArgs
}

& $exe @cliArgs
exit $LASTEXITCODE

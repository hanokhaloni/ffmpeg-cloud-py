<#
.SYNOPSIS
    Builds a self-contained StreamBench distribution:
      dist\Start-StreamBenchLab.ps1
      dist\app\StreamBench.exe        (+ runtime, no .NET install needed on the server)
      dist\config\*.json, dist\profiles\*.json
      dist\tools\mediamtx\mediamtx.exe (only if -MediaMtx is given; nothing is downloaded)
.EXAMPLE
    .\scripts\Publish-StreamBench.ps1 -MediaMtx C:\Downloads\mediamtx_v1.21.3_windows_amd64\mediamtx.exe
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist"),
    [string]$MediaMtx
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $root "src/FFmpegReliabilityBench.App") -c Release -r $Runtime --self-contained -o (Join-Path $Output "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot "Start-StreamBenchLab.ps1") $Output -Force
Copy-Item (Join-Path $root "config") $Output -Recurse -Force
Copy-Item (Join-Path $root "profiles") $Output -Recurse -Force
if ($MediaMtx) {
    $tools = Join-Path $Output "tools/mediamtx"
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    Copy-Item $MediaMtx $tools -Force
}
Write-Host "Distribution ready in $Output"

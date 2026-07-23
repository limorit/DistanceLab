param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "packaging\windows\build.ps1") -Configuration $Configuration

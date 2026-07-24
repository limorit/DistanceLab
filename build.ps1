param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$OpenCvDir = "",
    [string]$DependencyRoot = "",
    [switch]$SkipArchive
)
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "packaging\windows\build.ps1") @PSBoundParameters

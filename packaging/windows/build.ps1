param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$OpenCvDir = ""
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
if (-not $OpenCvDir -and $env:OpenCV_DIR) {
    $OpenCvDir = $env:OpenCV_DIR
}
if (-not $OpenCvDir) {
    $OpenCvDir = Join-Path $root ".deps\opencv\build"
}
if (-not (Test-Path -LiteralPath (Join-Path $OpenCvDir "OpenCVConfig.cmake"))) {
    throw "OpenCVConfig.cmake was not found. Pass -OpenCvDir or install OpenCV 4.x under .deps\opencv\build."
}
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw "CMake 3.24 or newer is required." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET 10 SDK or newer is required." }

$nativeBuild = Join-Path $root "build\native\win-x64"
$nativeOutput = Join-Path $root "artifacts\native\win-x64"
$publishOutput = Join-Path $root "artifacts\publish\win-x64"
cmake -S $root -B $nativeBuild -G "Visual Studio 17 2022" -A x64 `
    "-DOpenCV_DIR=$OpenCvDir" "-DCMAKE_INSTALL_PREFIX=$nativeOutput"
if ($LASTEXITCODE -ne 0) { throw "Native configure failed." }
cmake --build $nativeBuild --config $Configuration --target distance_tracker_native distance_tracker_native_test
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }
& (Join-Path $nativeBuild "$Configuration\distance_tracker_native_test.exe")
if ($LASTEXITCODE -ne 0) { throw "Native self-test failed." }
cmake --install $nativeBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "Native install failed." }

if (Test-Path -LiteralPath $publishOutput) { Remove-Item -LiteralPath $publishOutput -Recurse -Force }
dotnet publish (Join-Path $root "src\OpenCVDistanceLab.App\OpenCVDistanceLab.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    -p:UseSharedCompilation=false -m:1 -nodeReuse:false -o $publishOutput
if ($LASTEXITCODE -ne 0) { throw "Avalonia publish failed." }

$opencvRuntime = Get-ChildItem -LiteralPath $OpenCvDir -Recurse -Filter "opencv_*.dll" |
    Where-Object {
        $_.FullName -match '\\bin\\' -and
        ($Configuration -eq "Debug" -or $_.Name -notmatch 'd\.dll$')
    }
if (-not $opencvRuntime) { throw "OpenCV runtime DLLs were not found below $OpenCvDir." }
$opencvRuntime | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $publishOutput -Force
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path -LiteralPath $vswhere) {
    $visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($visualStudio) {
        $crt = Get-ChildItem -LiteralPath (Join-Path $visualStudio "VC\Redist\MSVC") -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "x64\Microsoft.VC143.CRT\vcruntime140.dll") } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if ($crt) {
            Copy-Item -Path (Join-Path $crt.FullName "x64\Microsoft.VC143.CRT\*") -Destination $publishOutput -Force
        }
    }
}

Write-Host "Windows product: $publishOutput"
Write-Host "Run: $(Join-Path $publishOutput 'OpenCVDistanceLab.exe')"

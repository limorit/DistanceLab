param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$OpenCvDir = "",
    [string]$DependencyRoot = "",
    [switch]$SkipArchive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
if (-not $DependencyRoot) {
    if (-not $env:LOCALAPPDATA) { throw "LOCALAPPDATA is unavailable." }
    $DependencyRoot = Join-Path $env:LOCALAPPDATA "DistanceLab"
}
$DependencyRoot = [System.IO.Path]::GetFullPath($DependencyRoot)
$downloads = Join-Path $DependencyRoot "downloads"
New-Item -ItemType Directory -Path $DependencyRoot, $downloads -Force | Out-Null

$dotnetChannel = "10.0"
$openCvVersion = "4.13.0"
$openCvSha256 = "f0e98c302464d6860777a7015065e11b9b271b5394e6ba92663f0cf1fc303f2c"
$openCvUrl = "https://github.com/opencv/opencv/releases/download/$openCvVersion/opencv-$openCvVersion-windows.exe"

function Test-DotNet10([string]$Executable) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { return $false }
    try {
        $versionText = (& $Executable --version | Select-Object -Last 1).Trim()
        $version = [version]$versionText
        return $LASTEXITCODE -eq 0 -and $version.Major -ge 10
    }
    catch {
        return $false
    }
}

function Get-DotNet10 {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed -and (Test-DotNet10 $installed.Source)) {
        Write-Host "Using .NET SDK: $($installed.Source)"
        return $installed.Source
    }

    $cached = Join-Path $DependencyRoot "dotnet10\dotnet.exe"
    if (Test-DotNet10 $cached) {
        Write-Host "Using cached .NET 10 SDK: $cached"
        return $cached
    }

    $installer = Join-Path $downloads "dotnet-install.ps1"
    Write-Host "Downloading the official .NET install script..."
    Invoke-WebRequest -UseBasicParsing -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
    $installDirectory = Split-Path -Parent $cached
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
        -Channel $dotnetChannel -Quality GA -Architecture x64 `
        -InstallDir $installDirectory -NoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-DotNet10 $cached)) {
        throw ".NET 10 SDK bootstrap failed."
    }
    return $cached
}

function Get-VisualStudioPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { return $null }
    $installation = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $installation) { return $null }
    return ([string]$installation).Trim()
}

function Get-CMake([string]$VisualStudioPath) {
    $installed = Get-Command cmake -ErrorAction SilentlyContinue
    if ($installed) {
        Write-Host "Using CMake: $($installed.Source)"
        return $installed.Source
    }
    if ($VisualStudioPath) {
        $bundled = Join-Path $VisualStudioPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
        if (Test-Path -LiteralPath $bundled -PathType Leaf) {
            Write-Host "Using Visual Studio CMake: $bundled"
            return $bundled
        }
    }
    throw "CMake was not found. Install the Visual Studio 2022 C++ CMake tools component."
}

function Test-OpenCvDirectory([string]$Directory) {
    return $Directory -and
        (Test-Path -LiteralPath (Join-Path $Directory "OpenCVConfig.cmake") -PathType Leaf)
}

function Get-FileSha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-VerifiedOpenCvArchive([string]$Archive) {
    if ((Get-FileSha256 $Archive) -eq $openCvSha256) {
        Write-Host "Using cached verified OpenCV archive: $Archive"
        return
    }

    Write-Host "Downloading OpenCV $openCvVersion for Windows (about 185 MB)..."
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        $arguments = @(
            "-fL", "--retry", "8", "--retry-all-errors", "--retry-delay", "3",
            "--connect-timeout", "30", "--max-time", "1800"
        )
        if ((Test-Path -LiteralPath $Archive) -and (Get-Item -LiteralPath $Archive).Length -gt 0) {
            $arguments += @("-C", "-")
        }
        $arguments += @($openCvUrl, "-o", $Archive)
        & $curl.Source @arguments
        $curlExit = $LASTEXITCODE
        # Some GitHub edge nodes reset a completed transfer before curl exits.
        # The official hash is authoritative, so accept the file when it matches.
        if ($curlExit -ne 0 -and (Get-FileSha256 $Archive) -ne $openCvSha256) {
            throw "OpenCV download failed with exit code $curlExit."
        }
    }
    else {
        Import-Module BitsTransfer
        Start-BitsTransfer -Source $openCvUrl -Destination $Archive `
            -DisplayName "DistanceLab OpenCV $openCvVersion"
    }

    $actualHash = Get-FileSha256 $Archive
    if ($actualHash -ne $openCvSha256) {
        throw "OpenCV package checksum mismatch. Expected $openCvSha256, got $actualHash."
    }
}

function Get-OpenCvDirectory {
    if ($OpenCvDir) {
        $explicit = [System.IO.Path]::GetFullPath($OpenCvDir)
        if (-not (Test-OpenCvDirectory $explicit)) {
            throw "OpenCVConfig.cmake was not found below the explicit OpenCvDir: $explicit"
        }
        return $explicit
    }

    $candidates = @()
    if ($env:OpenCV_DIR) { $candidates += $env:OpenCV_DIR }
    $candidates += (Join-Path $root ".deps\opencv\build")
    $candidates += (Join-Path $DependencyRoot "opencv413\opencv\build")
    foreach ($candidate in $candidates) {
        if (Test-OpenCvDirectory $candidate) {
            $resolved = [System.IO.Path]::GetFullPath($candidate)
            Write-Host "Using OpenCV: $resolved"
            return $resolved
        }
    }

    $archive = Join-Path $downloads "opencv-$openCvVersion-windows.exe"
    Get-VerifiedOpenCvArchive $archive
    $extractRoot = Join-Path $DependencyRoot "opencv413"
    $expectedDirectory = Join-Path $extractRoot "opencv\build"
    if (-not (Test-OpenCvDirectory $expectedDirectory)) {
        if (Test-Path -LiteralPath $extractRoot) {
            $dependencyRootFull = [System.IO.Path]::GetFullPath($DependencyRoot).TrimEnd('\')
            $extractFull = [System.IO.Path]::GetFullPath($extractRoot).TrimEnd('\')
            if (-not $extractFull.StartsWith(
                $dependencyRootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsafe OpenCV extraction path: $extractFull"
            }
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        Write-Host "Extracting OpenCV to $extractRoot..."
        $process = Start-Process -FilePath $archive `
            -ArgumentList "-o`"$extractRoot`"", "-y" `
            -WindowStyle Hidden -Wait -PassThru
        # The upstream self-extractor has returned 1 after successful extraction
        # on some systems, so validate its output instead of trusting that code.
        if (-not (Test-OpenCvDirectory $expectedDirectory)) {
            throw "OpenCV extraction failed with exit code $($process.ExitCode)."
        }
    }
    Write-Host "OpenCV $openCvVersion is ready: $expectedDirectory"
    return $expectedDirectory
}

$visualStudio = Get-VisualStudioPath
if (-not $visualStudio) {
    throw "Visual Studio 2022 with the Desktop development with C++ workload is required."
}
$dotnet = Get-DotNet10
$cmake = Get-CMake $visualStudio
$OpenCvDir = Get-OpenCvDirectory

$originalEnvironment = @{
    PATH = $env:PATH
    DOTNET_ROOT = $env:DOTNET_ROOT
    DOTNET_CLI_HOME = $env:DOTNET_CLI_HOME
    NUGET_PACKAGES = $env:NUGET_PACKAGES
    DOTNET_CLI_TELEMETRY_OPTOUT = $env:DOTNET_CLI_TELEMETRY_OPTOUT
}

try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_CLI_HOME = Join-Path $DependencyRoot "dotnet-home"
    $env:NUGET_PACKAGES = Join-Path $DependencyRoot "nuget"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:PATH = ((@(
        (Split-Path -Parent $dotnet),
        (Split-Path -Parent $cmake)
    ) + $env:PATH) -join [System.IO.Path]::PathSeparator)

    $nativeBuild = Join-Path $root "build\native\win-x64"
    $nativeOutput = Join-Path $root "artifacts\native\win-x64"
    $publishOutput = Join-Path $root "artifacts\publish\win-x64"

    & $cmake -S $root -B $nativeBuild -G "Visual Studio 17 2022" -A x64 `
        "-DOpenCV_DIR=$OpenCvDir" "-DCMAKE_INSTALL_PREFIX=$nativeOutput"
    if ($LASTEXITCODE -ne 0) { throw "Native configure failed." }
    & $cmake --build $nativeBuild --config $Configuration `
        --target distance_tracker_native distance_tracker_native_test
    if ($LASTEXITCODE -ne 0) { throw "Native build failed." }

    $nativeTest = Get-ChildItem -LiteralPath $nativeBuild -Recurse `
        -Filter "distance_tracker_native_test.exe" -File |
        Where-Object { $_.FullName -match "\\$([regex]::Escape($Configuration))\\" } |
        Select-Object -First 1
    if (-not $nativeTest) {
        throw "Native self-test executable was not found below $nativeBuild."
    }

    $opencvRuntime = Get-ChildItem -LiteralPath $OpenCvDir -Recurse -Filter "opencv_*.dll" |
        Where-Object {
            $_.FullName -match '\\bin\\' -and
            ($Configuration -eq "Debug" -or $_.Name -notmatch 'd\.dll$')
        }
    if (-not $opencvRuntime) {
        throw "OpenCV runtime DLLs were not found below $OpenCvDir."
    }

    $buildPath = $env:PATH
    try {
        $opencvBinDirectories = $opencvRuntime.DirectoryName | Sort-Object -Unique
        $env:PATH = ((@($opencvBinDirectories) + $buildPath) `
            -join [System.IO.Path]::PathSeparator)
        & $nativeTest.FullName
        $nativeTestExit = $LASTEXITCODE
    }
    finally {
        $env:PATH = $buildPath
    }
    if ($nativeTestExit -ne 0) { throw "Native self-test failed." }

    & $cmake --install $nativeBuild --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Native install failed." }

    if (Test-Path -LiteralPath $publishOutput) {
        $publishFull = [System.IO.Path]::GetFullPath($publishOutput).TrimEnd('\')
        $artifactsFull = [System.IO.Path]::GetFullPath(
            (Join-Path $root "artifacts")).TrimEnd('\')
        if (-not $publishFull.StartsWith(
            $artifactsFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe publish cleanup path: $publishFull"
        }
        Remove-Item -LiteralPath $publishOutput -Recurse -Force
    }

    & $dotnet publish (Join-Path $root `
        "src\OpenCVDistanceLab.App\OpenCVDistanceLab.App.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:UseSharedCompilation=false `
        -m:1 -nodeReuse:false -o $publishOutput
    if ($LASTEXITCODE -ne 0) { throw "Avalonia publish failed." }

    $opencvRuntime | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $publishOutput -Force
    }

    $crt = Get-ChildItem -LiteralPath (Join-Path $visualStudio "VC\Redist\MSVC") -Directory |
        Where-Object {
            Test-Path -LiteralPath (
                Join-Path $_.FullName "x64\Microsoft.VC143.CRT\vcruntime140.dll")
        } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $crt) { throw "MSVC x64 application-local runtime was not found." }
    Copy-Item -Path (Join-Path $crt.FullName "x64\Microsoft.VC143.CRT\*") `
        -Destination $publishOutput -Force

    & $dotnet run --project (Join-Path $root `
        "tests\OpenCVDistanceLab.Core.Tests\OpenCVDistanceLab.Core.Tests.csproj") `
        -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Core self-test failed." }

    $publishedExe = Join-Path $publishOutput "DistanceLab.exe"
    $publishedTest = Start-Process -FilePath $publishedExe `
        -ArgumentList "--native-self-test" -WindowStyle Hidden -Wait -PassThru
    if ($publishedTest.ExitCode -ne 0) {
        throw "Published native self-test failed with exit code $($publishedTest.ExitCode)."
    }

    $archiveOutput = $null
    if (-not $SkipArchive) {
        $projectText = Get-Content -LiteralPath (Join-Path $root `
            "src\OpenCVDistanceLab.App\OpenCVDistanceLab.App.csproj") -Raw
        if ($projectText -notmatch '<Version>([^<]+)</Version>') {
            throw "Application version was not found."
        }
        $version = $matches[1]
        $archiveOutput = Join-Path $root "artifacts\DistanceLab-$version-win-x64.zip"
        if (Test-Path -LiteralPath $archiveOutput) {
            Remove-Item -LiteralPath $archiveOutput -Force
        }
        Compress-Archive -Path (Join-Path $publishOutput "*") `
            -DestinationPath $archiveOutput -CompressionLevel Optimal
    }

    Write-Host ""
    Write-Host "Windows build completed successfully." -ForegroundColor Green
    Write-Host "Product: $publishOutput"
    Write-Host "Run: $publishedExe"
    if ($archiveOutput) { Write-Host "ZIP: $archiveOutput" }
}
finally {
    $env:PATH = $originalEnvironment.PATH
    $env:DOTNET_ROOT = $originalEnvironment.DOTNET_ROOT
    $env:DOTNET_CLI_HOME = $originalEnvironment.DOTNET_CLI_HOME
    $env:NUGET_PACKAGES = $originalEnvironment.NUGET_PACKAGES
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $originalEnvironment.DOTNET_CLI_TELEMETRY_OPTOUT
}

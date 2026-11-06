param(
    [switch]$Publish,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$localDotnet = Join-Path $launcherRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetPath = $localDotnet
} elseif ($dotnetCommand) {
    $dotnetPath = $dotnetCommand.Source
} else {
    throw 'Install the .NET 10 SDK or extract it into launcher/.tools/dotnet.'
}

$env:DOTNET_CLI_HOME = Join-Path $launcherRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $launcherRoot '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_PROCESSOR_COUNT = '2'
$projectPath = Join-Path $launcherRoot 'src\ArysLauncher\ArysLauncher.csproj'
if (-not ($Publish -or $Package)) {
    & $dotnetPath build $projectPath -c Release -p:UseSharedCompilation=false -p:BuildInParallel=false -maxcpucount:1 --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher build failed with exit code $LASTEXITCODE."
    }
    return
}

$outputPath = Join-Path $launcherRoot 'app'
if (Get-Process ArysLauncher -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $outputPath 'ArysLauncher.exe')) {
    throw 'Close the launcher before publishing into app.'
}
$staging = Join-Path $launcherRoot '.tools\publish'
if (Test-Path -LiteralPath $staging) {
    $resolved = (Resolve-Path -LiteralPath $staging).Path
    if ($resolved -ne (Join-Path $launcherRoot '.tools\publish')) {
        throw 'Unexpected publish directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
& (Join-Path $launcherRoot 'PrepareWebView2.ps1')
& $dotnetPath publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -p:BuildInParallel=false -maxcpucount:1 -o $staging --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "Launcher publish failed with exit code $LASTEXITCODE."
}
Get-ChildItem -LiteralPath $staging -File -Filter 'Microsoft.Web.WebView2.*.xml' | Remove-Item -Force
$licensesPath = Join-Path $staging 'Licenses'
New-Item -ItemType Directory -Path $licensesPath -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $launcherRoot 'docs') -File | Where-Object {
    $_.Name -like '*LICENSE*' -or $_.Name -like '*NOTICE*' -or $_.Name -eq 'ThirdPartyNotices.md'
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licensesPath $_.Name)
}
Copy-Item -LiteralPath (Join-Path $launcherRoot 'SourceREADME.md') -Destination (Join-Path $staging 'README.md')
Copy-Item -LiteralPath (Join-Path $launcherRoot '.tools\Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab') -Destination (Join-Path $staging 'BrowserRuntime.cab')
if (Test-Path -LiteralPath $outputPath) {
    $resolved = (Resolve-Path -LiteralPath $outputPath).Path
    if ($resolved -ne (Join-Path $launcherRoot 'app')) {
        throw 'Unexpected application output directory.'
    }
    Get-ChildItem -LiteralPath $resolved -Force | Where-Object Name -notin @('data', '.minecraft', 'startup-logs') | ForEach-Object {
        if ($_.FullName.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
} else {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}
Get-ChildItem -LiteralPath $staging -Force | Copy-Item -Destination $outputPath -Recurse
Write-Output (Join-Path $outputPath 'ArysLauncher.exe')
if ($Package) {
    & (Join-Path $launcherRoot 'Package.ps1')
}

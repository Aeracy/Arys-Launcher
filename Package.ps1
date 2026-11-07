param(
    [string]$NsisDirectory = $env:NSIS_HOME,
    [string]$BundledPack
)

$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$application = Join-Path $launcherRoot '.tools\publish'
[xml]$project = Get-Content -LiteralPath (Join-Path $launcherRoot 'src\ArysLauncher\ArysLauncher.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$artifacts = Join-Path $launcherRoot 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$allowedRoots = @('ArysLauncher.exe', 'BrowserRuntime.cab', 'release-channel.json', 'README.md', 'Licenses')
$unexpected = Get-ChildItem -LiteralPath $application -Force | Where-Object Name -notin $allowedRoots
if ($unexpected) {
    throw 'The clean publish directory contains unexpected files.'
}
if (-not $NsisDirectory) {
    $NsisDirectory = Join-Path $launcherRoot '.tools\nsis-3.13'
}
$compiler = Join-Path $NsisDirectory 'makensis.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'NSIS 3.13 is required. Set NSIS_HOME or extract it into .tools/nsis-3.13.'
}
$uninstallList = Join-Path $artifacts 'uninstall-licenses.nsh'
$deleteLines = Get-ChildItem -LiteralPath (Join-Path $launcherRoot 'docs') -File | Where-Object {
    $_.Name -like '*LICENSE*.txt' -or $_.Name -like '*NOTICE*.txt' -or $_.Name -eq 'ThirdPartyNotices.md'
} | ForEach-Object {
    'Delete "$INSTDIR\app\Licenses\' + $_.Name + '"'
}
[IO.File]::WriteAllLines($uninstallList, $deleteLines, [Text.UTF8Encoding]::new($false))
$installer = Join-Path $artifacts "ArysLauncher-$version-Setup.exe"
$appIcon = Join-Path $launcherRoot 'src\ArysLauncher\Assets\app.ico'
$packArguments = @()
if ($BundledPack) {
    $BundledPack = (Get-Item -LiteralPath $BundledPack).FullName
    if ([IO.Path]::GetExtension($BundledPack) -ne '.mrpack') {
        throw 'Use a prepared .mrpack for the bundled modpack.'
    }
    $packArguments = @("/DBundledPack=$BundledPack")
}
& $compiler '/V2' "/DAppDirectory=$application" "/DAppVersion=$version" "/DAppIcon=$appIcon" "/DOutputFile=$installer" "/DUninstallLicenses=$uninstallList" @packArguments (Join-Path $launcherRoot 'Setup.nsi')
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}
$portable = Join-Path $artifacts "ArysLauncher-$version-Portable.zip"
if (Test-Path -LiteralPath $portable) {
    Remove-Item -LiteralPath $portable
}
$portableArchive = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $application -Recurse -File | Where-Object Name -ne 'BrowserRuntime.cab' | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($application, $_.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($portableArchive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    if ($BundledPack) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($portableArchive, $BundledPack, 'Arys-Modpack.mrpack',
            [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
} finally {
    $portableArchive.Dispose()
}
$source = Join-Path $artifacts 'source'
if (Test-Path -LiteralPath $source) {
    $resolved = (Resolve-Path -LiteralPath $source).Path
    if ($resolved -ne (Join-Path $launcherRoot 'artifacts\source') -or (Test-Path -LiteralPath (Join-Path $resolved '.git'))) {
        throw 'The source export directory is not an empty build output.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $source | Out-Null
$files = @('Build.ps1', 'Package.ps1', 'Setup.nsi', 'PrepareWebView2.ps1', 'Prepare-Pack.py', 'pack_worlds.py', 'Publish-Pack.ps1', 'Get-ChannelRelease.ps1', 'New-UpdateRelease.ps1', 'Publish-GitHubRelease.ps1', 'release-channel.json', 'Start.cmd', '.gitignore', 'SourceREADME.md')
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $launcherRoot $file) -Destination (Join-Path $source $file)
}
Copy-Item -LiteralPath (Join-Path $launcherRoot 'SourceREADME.md') -Destination (Join-Path $source 'README.md')
$sourceCode = Join-Path $source 'src\ArysLauncher'
New-Item -ItemType Directory -Path $sourceCode -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $launcherRoot 'src\ArysLauncher') -File | Where-Object {
    $_.Extension -in @('.cs', '.xaml', '.csproj', '.manifest') -or
    $_.Name -in @('packages.lock.json', 'pack-content-policy.json')
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sourceCode $_.Name)
}
$assetDirectory = Join-Path $sourceCode 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
foreach ($asset in @('hero.png', 'app.png', 'app.ico')) {
    Copy-Item -LiteralPath (Join-Path $launcherRoot ('src\ArysLauncher\Assets\' + $asset)) -Destination $assetDirectory
}
$resources = Join-Path $sourceCode 'Resources'
New-Item -ItemType Directory -Path $resources | Out-Null
Get-ChildItem -LiteralPath (Join-Path $launcherRoot 'src\ArysLauncher\Resources') -File -Filter '*.resx' |
    Copy-Item -Destination $resources
$sourceDocs = Join-Path $source 'docs'
New-Item -ItemType Directory -Path $sourceDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $launcherRoot 'docs') -File | Where-Object {
    $_.Name -like '*LICENSE*' -or $_.Name -like '*NOTICE*' -or $_.Name -eq 'ThirdPartyNotices.md'
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $sourceDocs
}
$sourceZip = Join-Path $artifacts "ArysLauncher-$version-Source.zip"
if (Test-Path -LiteralPath $sourceZip) {
    Remove-Item -LiteralPath $sourceZip
}
[IO.Compression.ZipFile]::CreateFromDirectory($source, $sourceZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-Item -LiteralPath $installer, $portable, $sourceZip | Select-Object Name, Length

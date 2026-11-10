param(
    [Parameter(Mandatory)][ValidateSet('Launcher', 'Pack')][string]$Kind,
    [Parameter(Mandatory)][string]$Version,
    [string]$ContentRoot,
    [string[]]$Include = @('mods/*'),
    [string]$PreviousManifest,
    [string]$DownloadIndex,
    [string]$PackId = 'arys-eternal-dungeon',
    [string]$PackName = "The Maiden's Eternal Dungeon",
    [string]$MinecraftVersion = '1.20.1',
    [string]$ForgeVersion = '47.4.16',
    [string]$Repository = 'Aeracy/Arys-Launcher',
    [string]$Tag,
    [string]$SigningKey,
    [string]$MinimumLauncherVersion,
    [string]$ReleaseNotes = ''
)

$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
if (-not $MinimumLauncherVersion) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $launcherRoot 'src\ArysLauncher\ArysLauncher.csproj')
    $MinimumLauncherVersion = if ($Kind -eq 'Pack') { [string]$project.Project.PropertyGroup.Version } else { '1.0.0' }
}
$packRules = if ($Kind -eq 'Pack') {
    Get-Content -LiteralPath (Join-Path $launcherRoot 'src\ArysLauncher\pack-content-policy.json') -Raw | ConvertFrom-Json
}

function Test-PackReleaseFile([string]$Relative) {
    if ($Relative -ceq 'options.txt') {
        return $true
    }
    $segments = $Relative.Split('/')
    $folder = $segments[0]
    if ($folder -in $packRules.ExcludedRoots -or $Relative -in $packRules.ExcludedPaths -or
        $Relative -in $packRules.LegacyConfigPaths) {
        return $false
    }
    if ($folder -eq 'mods' -and ($segments.Length -ne 2 -or [IO.Path]::GetExtension($Relative) -ne '.jar')) {
        return $false
    }
    foreach ($prefix in @($packRules.ExcludedPrefixes) + @($packRules.LegacyConfigPrefixes)) {
        if ($Relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    foreach ($prefix in $packRules.ExcludedRootPrefixes) {
        if ($folder.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

$parsedVersion = $null
if (-not [version]::TryParse($Version, [ref]$parsedVersion)) {
    throw 'Version must contain numeric major, minor and patch components.'
}
$parsedMinimum = $null
if (-not [version]::TryParse($MinimumLauncherVersion, [ref]$parsedMinimum)) {
    throw 'MinimumLauncherVersion must contain a numeric version.'
}
if (-not $Tag) {
    $Tag = if ($Kind -eq 'Launcher') { 'v' + $Version } else { 'pack-v' + $Version }
}
if ($Tag -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'Use a tag containing only letters, numbers, periods, hyphens and underscores.'
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Repository -eq 'Aeracy/arysmod') {
    throw 'Use the separate launcher release repository.'
}
if (-not $SigningKey) {
    $SigningKey = Join-Path $launcherRoot '.tools\signing\updates-private.pem'
}
if (-not $ContentRoot) {
    $ContentRoot = Join-Path $launcherRoot 'app'
}
$ContentRoot = (Resolve-Path -LiteralPath $ContentRoot).Path
$output = Join-Path $launcherRoot ('artifacts\release-' + $Tag)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$previous = if ($PreviousManifest) { Get-Content -LiteralPath $PreviousManifest -Raw | ConvertFrom-Json } else { $null }
$previousFiles = @{}
foreach ($file in @($previous.Files)) {
    if ($file) {
        $previousFiles[$file.Path] = $file
    }
}
$downloadSources = if ($DownloadIndex) {
    Get-Content -LiteralPath $DownloadIndex -Raw | ConvertFrom-Json -AsHashtable
} else {
    @{}
}
$manifest = [ordered]@{
    SchemaVersion = 1
    Kind = $Kind
    Version = $Version
    MinimumLauncherVersion = $MinimumLauncherVersion
    ReleaseNotes = $ReleaseNotes
    Files = @()
    RetiredFiles = @()
}
if ($Kind -eq 'Pack') {
    $manifest.Pack = [ordered]@{ Id = $PackId; Name = $PackName; MinecraftVersion = $MinecraftVersion; ForgeVersion = $ForgeVersion }
}
$records = [Collections.Generic.List[object]]::new()
$modDigests = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$sourceFiles = Get-ChildItem -LiteralPath $ContentRoot -Recurse -File | Sort-Object @{Expression={$_.Name.StartsWith('[')}}, FullName
foreach ($file in $sourceFiles) {
    $relative = [IO.Path]::GetRelativePath($ContentRoot, $file.FullName).Replace('\', '/')
    $selected = if ($Kind -eq 'Launcher') {
        $relative -in @('ArysLauncher.exe', 'release-channel.json', 'README.md') -or $relative.StartsWith('Licenses/')
    } else {
        @($Include | Where-Object { $relative -like $_ }).Count -gt 0
    }
    if (-not $selected) {
        continue
    }
    if ($Kind -eq 'Pack' -and -not (Test-PackReleaseFile $relative)) {
        continue
    }
    $folder = $relative.Split('/')[0]
    if ($Kind -eq 'Pack' -and $relative -cne 'options.txt' -and $folder -notin @('mods', 'config', 'defaultconfigs', 'resourcepacks', 'shaderpacks', 'datapacks', 'resources', 'resource', 'kubejs', 'scripts', 'patchouli_books', 'openloader', 'global_packs', 'paxi', 'fancymenu', 'moonlight-global-datapacks')) {
        throw "The update path is outside the managed pack folders: $relative"
    }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "The release contains a linked file: $relative"
    }
    $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Kind -eq 'Pack' -and $folder -eq 'mods' -and -not $modDigests.Add($digest)) {
        Write-Output "Skipped identical JAR: $relative"
        continue
    }
    $asset = $digest + $file.Extension.ToLowerInvariant()
    $releaseUrl = "https://github.com/$Repository/releases/download/$Tag/$asset"
    $urls = @($releaseUrl)
    $preserve = $Kind -eq 'Pack' -and $folder -in @('config', 'defaultconfigs', 'kubejs', 'scripts')
    $prior = $previousFiles[$relative]
    if ($Kind -eq 'Pack' -and $prior -and $prior.Size -eq $file.Length -and $prior.Sha256 -eq $digest -and $prior.Downloads) {
        $urls = @($prior.Downloads)
    }
    if ($downloadSources.ContainsKey($relative)) {
        $record = $downloadSources[$relative]
        if ($record.Size -ne $file.Length -or $record.Sha256 -ne $digest -or -not $record.Downloads) {
            throw "A download entry does not match the prepared pack file: $relative"
        }
        if ($record.AssetName) {
            if ($Kind -ne 'Pack' -or $relative -notlike 'mods/arysdungeonmod-*' -or
                $record.AssetName -cne [IO.Path]::GetFileName($relative) -or $record.AssetName -notmatch '^[A-Za-z0-9_.+-]+\.jar$') {
                throw 'Use the original English ArysMod filename for the release attachment.'
            }
            $asset = $record.AssetName
            $releaseUrl = "https://github.com/$Repository/releases/download/$Tag/$asset"
        }
        $urls = @($record.Downloads)
        $prior = $previousFiles[$relative]
        if ($relative -like 'mods/arysdungeonmod-*' -and $prior) {
            $preserve = $record.CleanedFromSha256 -eq $prior.Sha256 -or
                ($prior.PreserveExisting -and $prior.Sha256 -eq $digest)
        }
    }
    if ($releaseUrl -in $urls) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output $asset) -Force
    }
    $records.Add([ordered]@{
        Path = $relative
        Size = $file.Length
        Sha256 = $digest
        Downloads = $urls
        PreserveExisting = $preserve
    })
}
if ($records.Count -eq 0) {
    throw 'No matching release files were found.'
}
$manifest.Files = @($records | Sort-Object Path)
$retired = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
if ($previous) {
    foreach ($file in @($previous.Files) + @($previous.RetiredFiles)) {
        if (-not $file -or ($Kind -eq 'Pack' -and
            (@($Include | Where-Object { $file.Path -like $_ }).Count -eq 0 -or -not (Test-PackReleaseFile $file.Path)))) {
            continue
        }
        $size = if ($null -ne $file.Size) { $file.Size } else { $file.bytes }
        $retired[$file.Path + ':' + $file.Sha256] = [ordered]@{ Path = $file.Path; Size = $size; Sha256 = $file.Sha256 }
    }
}
foreach ($file in $manifest.Files) {
    $retired.Remove($file.Path + ':' + $file.Sha256) | Out-Null
}
$manifest.RetiredFiles = @($retired.Values | Sort-Object Path, Sha256)
$name = if ($Kind -eq 'Launcher') { 'launcher-update.json' } else { 'pack-update.json' }
$path = Join-Path $output $name
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 12))
[IO.File]::WriteAllBytes($path, $bytes)
$signer = [Security.Cryptography.ECDsa]::Create()
try {
    $signer.ImportFromPem((Get-Content -LiteralPath $SigningKey -Raw))
    $channel = Get-Content -LiteralPath (Join-Path $launcherRoot 'release-channel.json') -Raw | ConvertFrom-Json
    if ([Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo()) -ne $channel.PublicKey) {
        throw 'The signing key does not match the public release channel.'
    }
    $signature = $signer.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    [IO.File]::WriteAllText($path + '.sig', [Convert]::ToBase64String($signature), [Text.UTF8Encoding]::new($false))
} finally {
    $signer.Dispose()
}
Write-Output $path

param(
    [Parameter(Mandatory)][ValidateSet('Launcher', 'Pack')][string]$Kind,
    [Parameter(Mandatory)][string]$Version,
    [string]$ContentRoot,
    [string[]]$Include = @('mods/*'),
    [string]$PreviousManifest,
    [string]$PackId = 'arys-eternal-dungeon',
    [string]$PackName = "The Maiden's Eternal Dungeon",
    [string]$MinecraftVersion = '1.20.1',
    [string]$ForgeVersion = '47.4.16',
    [string]$Repository = 'Aeracy/Arys-Launcher',
    [string]$Tag,
    [string]$SigningKey,
    [string]$ReleaseNotes = ''
)

$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$parsedVersion = $null
if (-not [version]::TryParse($Version, [ref]$parsedVersion)) {
    throw 'Version must contain numeric major, minor and patch components.'
}
if (-not $Tag) {
    $Tag = 'v' + $Version
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
$manifest = [ordered]@{
    SchemaVersion = 1
    Kind = $Kind
    Version = $Version
    MinimumLauncherVersion = '1.0.0'
    ReleaseNotes = $ReleaseNotes
    Files = @()
    RetiredFiles = @()
}
if ($Kind -eq 'Pack') {
    $manifest.Pack = [ordered]@{ Id = $PackId; Name = $PackName; MinecraftVersion = $MinecraftVersion; ForgeVersion = $ForgeVersion }
}
$records = [Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $ContentRoot -Recurse -File)) {
    $relative = [IO.Path]::GetRelativePath($ContentRoot, $file.FullName).Replace('\', '/')
    $selected = if ($Kind -eq 'Launcher') {
        $relative -in @('ArysLauncher.exe', 'release-channel.json', 'README.md') -or $relative.StartsWith('Licenses/')
    } else {
        @($Include | Where-Object { $relative -like $_ }).Count -gt 0
    }
    if (-not $selected) {
        continue
    }
    $folder = $relative.Split('/')[0]
    if ($Kind -eq 'Pack' -and $folder -notin @('mods', 'config', 'defaultconfigs', 'resourcepacks', 'shaderpacks', 'datapacks', 'resources', 'resource', 'kubejs', 'scripts', 'patchouli_books', 'openloader', 'global_packs', 'paxi', 'fancymenu')) {
        throw "The update path is outside the managed pack folders: $relative"
    }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "The release contains a linked file: $relative"
    }
    $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = $digest + $file.Extension.ToLowerInvariant()
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output $asset) -Force
    $records.Add([ordered]@{
        Path = $relative
        Size = $file.Length
        Sha256 = $digest
        Downloads = @("https://github.com/$Repository/releases/download/$Tag/$asset")
        PreserveExisting = $Kind -eq 'Pack' -and $folder -in @('config', 'kubejs', 'scripts')
    })
}
if ($records.Count -eq 0) {
    throw 'No matching release files were found.'
}
$manifest.Files = @($records | Sort-Object Path)
$retired = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
if ($previous) {
    foreach ($file in @($previous.Files) + @($previous.RetiredFiles)) {
        if (-not $file -or ($Kind -eq 'Pack' -and @($Include | Where-Object { $file.Path -like $_ }).Count -eq 0)) {
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

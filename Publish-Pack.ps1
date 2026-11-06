[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [string]$InstanceDirectory,
    [string]$Template,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Tag = "pack-v$Version",
    [string]$NotesFile,
    [switch]$NormalizeLocal,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$channel = Get-Content -LiteralPath (Join-Path $root 'release-channel.json') -Raw | ConvertFrom-Json
$repository = $channel.LauncherRepository
if ($repository -ne 'Aeracy/Arys-Launcher') {
    throw 'This tool publishes to the public Arys Launcher repository.'
}
if (-not $InstanceDirectory) {
    $minecraft = Split-Path -Parent (Split-Path -Parent $root)
    $InstanceDirectory = Join-Path $minecraft '.minecraft\versions\Arys Test - The Maidens Eternal Dungeon'
}
$instance = Get-Item -LiteralPath $InstanceDirectory
if (-not $instance.PSIsContainer) {
    throw 'Choose a Minecraft instance directory.'
}
if ($NormalizeLocal) {
    $active = Get-CimInstance Win32_Process -Filter "Name = 'javaw.exe' OR Name = 'java.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($instance.FullName, [StringComparison]::OrdinalIgnoreCase) }
    if ($active) {
        throw 'Close Minecraft before renaming local mods or moving old configs.'
    }
}

$output = Join-Path $root "artifacts\release-$Tag"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$bundle = Join-Path $output 'Arys-Modpack.mrpack'
if (Test-Path -LiteralPath $bundle) {
    if (-not $Publish) {
        throw "This pack has already been prepared at $output."
    }
    & (Join-Path $root 'Publish-GitHubRelease.ps1') -Tag $Tag -Title $Tag -Repository $repository -AssetsDirectory $output -NotesFile $NotesFile -Attachment $bundle
    return
}
$work = Join-Path $root ('.staging\pack-export-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $previous = Join-Path $work 'previous-pack.json'
    try {
        Invoke-WebRequest -Uri $channel.PackManifestUrl -OutFile $previous -TimeoutSec 40
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) {
            throw
        }
    }
    $previousArgument = @{}
    if (Test-Path -LiteralPath $previous) {
        $signaturePath = $previous + '.sig'
        Invoke-WebRequest -Uri ($channel.PackManifestUrl + '.sig') -OutFile $signaturePath -TimeoutSec 40
        $signature = [IO.File]::ReadAllText($signaturePath).Trim()
        $signer = [Security.Cryptography.ECDsa]::Create()
        try {
            $publicKey = [Convert]::FromBase64String($channel.PublicKey)
            [int]$read = 0
            $signer.ImportSubjectPublicKeyInfo($publicKey, [ref]$read)
            if (-not $signer.VerifyData([IO.File]::ReadAllBytes($previous), [Convert]::FromBase64String($signature),
                    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
                throw 'The previous pack manifest failed signature verification.'
            }
        } finally {
            $signer.Dispose()
        }
        $current = Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json
        if ($current.Kind -ne 'Pack' -or $current.Pack.Id -ne 'arys-eternal-dungeon' -or [version]$current.Version -ge [version]$Version) {
            throw 'Use a pack version newer than the current public pack.'
        }
        $previousArgument.PreviousManifest = $previous
    }
    if (-not $Template) {
        $Template = Join-Path $work 'previous.mrpack'
        Invoke-WebRequest -Uri "https://github.com/$repository/releases/latest/download/Arys-Modpack.mrpack" -OutFile $Template -TimeoutSec 60
    }
    $Template = (Get-Item -LiteralPath $Template).FullName
    $content = Join-Path $work 'content'
    $downloadIndex = Join-Path $work 'downloads.json'
    $preparedBundle = Join-Path $work 'Arys-Modpack.mrpack'
    $arguments = @($instance.FullName, $Template, $preparedBundle, '--version', $Version, '--release-tag', $Tag,
        '--repository', $repository, '--content-output', $content, '--download-index', $downloadIndex)
    if ($NormalizeLocal) {
        $arguments += '--normalize-local'
    }
    $env:PYTHONIOENCODING = 'utf-8'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    & python (Join-Path $root 'Prepare-Pack.py') @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Pack preparation failed.'
    }
    $notes = if ($NotesFile) { Get-Content -LiteralPath $NotesFile -Raw } else { '' }
    $releaseArguments = @{
        Kind = 'Pack'; Version = $Version; Tag = $Tag; Repository = $repository
        ContentRoot = $content; Include = '*'; DownloadIndex = $downloadIndex; ReleaseNotes = $notes
    }
    & (Join-Path $root 'New-UpdateRelease.ps1') @releaseArguments @previousArgument
    Copy-Item -LiteralPath $preparedBundle -Destination $bundle
} finally {
    $resolved = (Resolve-Path -LiteralPath $work).ProviderPath
    if ($resolved -ne $work -or -not $resolved.StartsWith((Join-Path $root '.staging\pack-export-'), [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint -or
        (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint)) {
        throw 'Unexpected pack staging directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output $bundle
if ($Publish) {
    & (Join-Path $root 'Publish-GitHubRelease.ps1') -Tag $Tag -Title $Tag -Repository $repository -AssetsDirectory $output -NotesFile $NotesFile -Attachment $bundle
}

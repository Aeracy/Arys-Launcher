[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Launcher', 'Pack')][string]$Kind,
    [Parameter(Mandatory)][string]$Tag,
    [string]$AssetsDirectory,
    [string]$Repository = 'Aeracy/Arys-Launcher'
)

$ErrorActionPreference = 'Stop'
if ($Repository -cne 'Aeracy/Arys-Launcher') {
    throw 'Publish update feeds to the public launcher repository only.'
}
if (-not $AssetsDirectory) {
    $AssetsDirectory = Join-Path $PSScriptRoot ('artifacts\release-' + $Tag)
}
$name = if ($Kind -eq 'Launcher') { 'launcher-update.json' } else { 'pack-update.json' }
$manifestPath = Join-Path $AssetsDirectory $name
$bytes = [IO.File]::ReadAllBytes($manifestPath)
if ($bytes.Length -gt 4MB) {
    throw 'The signed update manifest is too large.'
}
$signature = [IO.File]::ReadAllText($manifestPath + '.sig').Trim()
$channel = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-channel.json') -Raw | ConvertFrom-Json
$signer = [Security.Cryptography.ECDsa]::Create()
try {
    [int]$read = 0
    $signer.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($channel.PublicKey), [ref]$read)
    if (-not $signer.VerifyData($bytes, [Convert]::FromBase64String($signature),
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
        throw 'The update feed manifest failed signature verification.'
    }
} finally {
    $signer.Dispose()
}
$manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
$prefix = if ($Kind -eq 'Launcher') { $channel.LauncherTagPrefix } else { $channel.PackTagPrefix }
if ($manifest.Kind -cne $Kind -or $manifest.SchemaVersion -ne 1 -or $Tag -cne ($prefix + $manifest.Version)) {
    throw 'The update feed tag does not match its signed manifest.'
}
$feed = [ordered]@{ SchemaVersion = 1; Manifest = [Convert]::ToBase64String($bytes); Signature = $signature }
$feedBytes = [Text.UTF8Encoding]::new($false).GetBytes(($feed | ConvertTo-Json -Compress))
$filename = if ($Kind -eq 'Launcher') { 'launcher.json' } else { 'pack.json' }
$env:GCM_INTERACTIVE = 'Never'
$credentialInput = "protocol=https`nhost=github.com`nusername=Aeracy`n`n"
$credentialLines = $credentialInput | git -c credential.interactive=never credential fill
$credential = @{}
foreach ($line in $credentialLines) {
    $pair = $line.Split('=', 2)
    if ($pair.Length -eq 2) {
        $credential[$pair[0]] = $pair[1]
    }
}
if (-not $credential.ContainsKey('password')) {
    throw 'Sign in through Git Credential Manager before publishing the update feed.'
}
$headers = @{
    Authorization = 'Bearer ' + $credential['password']
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'ArysLauncher-Publisher'
}
$api = "https://api.github.com/repos/$Repository"
try {
    $repo = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 40
    if ($repo.private -or $repo.default_branch -cne 'main') {
        throw 'The public update feed requires the main branch of the launcher repository.'
    }
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $headers -TimeoutSec 40
    if ($release.draft -or $release.prerelease -or $release.tag_name -cne $Tag) {
        throw 'Publish the final release before updating its feed.'
    }
    foreach ($file in @(Get-Item -LiteralPath $manifestPath, ($manifestPath + '.sig'))) {
        $asset = $release.assets | Where-Object name -ceq $file.Name | Select-Object -First 1
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        if (-not $asset -or $asset.digest -cne $digest -or $asset.size -ne $file.Length) {
            throw 'The public release differs from the update feed manifest.'
        }
    }
    $address = "$api/contents/updates/$filename"
    $existing = $null
    try {
        $existing = Invoke-RestMethod -Uri ($address + '?ref=main') -Headers $headers -TimeoutSec 40
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) {
            throw
        }
    }
    if ($existing) {
        $currentBytes = [Convert]::FromBase64String($existing.content)
        $currentFeed = [Text.Encoding]::UTF8.GetString($currentBytes) | ConvertFrom-Json
        $current = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($currentFeed.Manifest)) | ConvertFrom-Json
        if ([version]$current.Version -gt [version]$manifest.Version) {
            throw 'A newer version is already selected by the update feed.'
        }
        if ([Convert]::ToBase64String($currentBytes) -ceq [Convert]::ToBase64String($feedBytes)) {
            Write-Output "Update feed already published: $Kind $($manifest.Version)"
            return
        }
    }
    $author = @{ name = 'Aeracy'; email = ($repo.owner.id.ToString() + '+Aeracy@users.noreply.github.com') }
    $definition = @{ message = $Tag; branch = 'main'; content = [Convert]::ToBase64String($feedBytes);
        author = $author; committer = $author }
    if ($existing) {
        $definition.sha = $existing.sha
    }
    $result = Invoke-RestMethod -Method Put -Uri $address -Headers $headers -ContentType 'application/json' -Body ($definition | ConvertTo-Json -Depth 4) -TimeoutSec 40
    Write-Output "Update feed published: $Kind $($manifest.Version) $($result.commit.sha)"
} finally {
    $credential.Clear()
    $credentialLines = $null
    $headers = $null
}

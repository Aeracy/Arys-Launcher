param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$AssetsDirectory,
    [string[]]$Attachment = @(),
    [string]$Title,
    [string]$NotesFile,
    [string]$TargetCommit,
    [ValidateSet('Launcher', 'Pack')][string]$Kind,
    [string]$Repository = 'Aeracy/Arys-Launcher'
)

$ErrorActionPreference = 'Stop'

function Confirm-PublicArchive([IO.FileInfo]$File, [bool]$DataPack) {
    if ($File.Extension -notin @('.zip', '.mrpack')) {
        return
    }
    $forbidden = '(?i)(?:^|/)(?:accounts\.json(?:\.[^/]*)?|launcher_accounts\.json|launcher_profiles\.json|cookies|login data|[^/]+\.pem)$|(?:^|/)(?:browser|startup-logs|\.tools|\.staging|\.git)/'
    $secrets = '(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|(?i:"(?:accessToken|refreshToken|protectedTokens)"\s*:\s*"[^"\s]{12,}")'
    $archive = [IO.Compression.ZipFile]::OpenRead($File.FullName)
    try {
        if ($DataPack -and $null -eq $archive.GetEntry('pack.mcmeta')) {
            throw "The datapack is missing pack.mcmeta: $($File.Name)"
        }
        foreach ($entry in $archive.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            if ($File.Extension -eq '.mrpack' -and $path -match '(?i)(?:^|/)(?:config|defaultconfigs|serverconfig)/arys') {
                throw "The public modpack contains ArysMod configuration: $path"
            }
            $packData = $DataPack -and $path.StartsWith('data/', [StringComparison]::Ordinal) -and
                $path -match '^(?:data/)(?:[a-z0-9_.-]+/)*[a-z0-9_.-]*$' -and $path -notmatch '(?:^|/)\.\.?(?:/|$)'
            if ($path -match $forbidden -or ($path -match '(?i)(?:^|/)data/' -and -not $packData)) {
                throw "The public archive contains private runtime data: $($File.Name)/$path"
            }
            if ($entry.Length -gt 4MB -or [IO.Path]::GetExtension($path) -notin @('.json', '.cs', '.ps1', '.txt', '.md', '.xml', '.config', '.ini')) {
                continue
            }
            $reader = [IO.StreamReader]::new($entry.Open())
            try {
                if ($reader.ReadToEnd() -match $secrets) {
                    throw "The public archive contains a possible saved credential: $($File.Name)/$path"
                }
            } finally {
                $reader.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Repository -eq 'Aeracy/arysmod') {
    throw 'Choose the separate public launcher repository.'
}
if (-not $AssetsDirectory) {
    $AssetsDirectory = Join-Path $PSScriptRoot ('artifacts\release-' + $Tag)
}
if (-not $Title) {
    $Title = $Tag
}
$manifestFiles = @(@('launcher-update.json', 'pack-update.json') | Where-Object {
    Test-Path -LiteralPath (Join-Path $AssetsDirectory $_)
})
if (@($manifestFiles).Count -ne 1) {
    throw 'Each release must contain exactly one launcher or modpack manifest.'
}
$releaseManifest = Get-Content -LiteralPath (Join-Path $AssetsDirectory $manifestFiles[0]) -Raw | ConvertFrom-Json
if ($releaseManifest.Kind -eq 'Pack') {
    $packRules = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\ArysLauncher\pack-content-policy.json') -Raw | ConvertFrom-Json
    foreach ($record in @($releaseManifest.Files) + @($releaseManifest.RetiredFiles)) {
        if ($record.Path -match '(?i)(?:^|/)(?:config|defaultconfigs|serverconfig)/arys' -or
            $record.Path -in $packRules.ExcludedPaths -or $record.Path.Split('/')[0] -in $packRules.ExcludedRoots) {
            throw "The modpack manifest contains excluded runtime data: $($record.Path)"
        }
        foreach ($prefix in $packRules.ExcludedPrefixes) {
            if ($record.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The modpack manifest contains excluded runtime data: $($record.Path)"
            }
        }
    }
}
if ($Kind -and $Kind -ne $releaseManifest.Kind) {
    throw 'The selected release channel does not match its manifest.'
}
$Kind = $releaseManifest.Kind
$channel = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-channel.json') -Raw | ConvertFrom-Json
$tagPrefix = if ($Kind -eq 'Launcher') { $channel.LauncherTagPrefix } else { $channel.PackTagPrefix }
if (-not $tagPrefix -or $Tag -cne ($tagPrefix + $releaseManifest.Version)) {
    throw 'Use the configured channel prefix followed by the manifest version for the release tag.'
}
$notes = if ($NotesFile) { Get-Content -LiteralPath $NotesFile -Raw } else { '' }
if ($TargetCommit -and $TargetCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Provide the full Git commit ID for the release tag.'
}
$env:GCM_INTERACTIVE = 'Never'
$credentialInput = "protocol=https`nhost=github.com`nusername=$($Repository.Split('/')[0])`n`n"
$credentialLines = $credentialInput | git -c credential.interactive=never credential fill
$credential = @{}
foreach ($line in $credentialLines) {
    $pair = $line.Split('=', 2)
    if ($pair.Length -eq 2) {
        $credential[$pair[0]] = $pair[1]
    }
}
if (-not $credential.ContainsKey('password')) {
    throw 'Sign in through Git Credential Manager before publishing.'
}
$headers = @{
    Authorization = 'Bearer ' + $credential['password']
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'ArysLauncher-Publisher'
}
$api = "https://api.github.com/repos/$Repository"
try {
    $repo = Invoke-RestMethod -Uri $api -Headers $headers
    if ($repo.private) {
        throw 'The launcher release repository must be public.'
    }
    $existing = $null
    try {
        $existing = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Headers $headers
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) {
            throw
        }
    }
    if (-not $existing) {
        $releases = Invoke-RestMethod -Uri "$api/releases?per_page=30" -Headers $headers
        $existing = $releases | Where-Object tag_name -eq $Tag | Select-Object -First 1
    }
    if ($existing -and -not $existing.draft) {
        throw 'This release is already public. Publish a new version instead of overwriting it.'
    }
    $assets = [Collections.Generic.List[IO.FileInfo]]::new()
    $assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $dataPacks = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($manifestName in @('launcher-update.json', 'pack-update.json')) {
        $manifestPath = Join-Path $AssetsDirectory $manifestName
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            continue
        }
        foreach ($name in @($manifestName, ($manifestName + '.sig'))) {
            $assets.Add((Get-Item -LiteralPath (Join-Path $AssetsDirectory $name)))
            $assetNames.Add($name) | Out-Null
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($record in $manifest.Files) {
            $prefix = "https://github.com/$Repository/releases/download/$Tag/"
            $address = $record.Downloads | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Select-Object -First 1
            if (-not $address) {
                continue
            }
            $name = [uri]::UnescapeDataString(([uri]$address).Segments[-1])
            if ([IO.Path]::GetFileName($name) -ne $name) {
                throw 'A release asset name contains a path.'
            }
            $file = Get-Item -LiteralPath (Join-Path $AssetsDirectory $name)
            if ($file.Length -ne $record.Size -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $record.Sha256) {
                throw "Release file does not match its manifest: $name"
            }
            if ($assetNames.Add($name)) {
                $assets.Add($file)
            }
            if ($manifestName -eq 'pack-update.json' -and $record.Path -match '^(?:moonlight-global-datapacks|datapacks)/.+\.zip$') {
                $dataPacks.Add($name) | Out-Null
            }
        }
    }
    foreach ($path in $Attachment) {
        $assets.Add((Get-Item -LiteralPath $path))
    }
    if ($assets.Name | Group-Object | Where-Object Count -gt 1) {
        throw 'Release attachments have duplicate names.'
    }
    if ($existing -and @($existing.assets | Where-Object { $_.name -notin $assets.Name }).Count -ne 0) {
        throw 'The draft contains attachments outside this verified release. Remove the stale attachments before publishing.'
    }
    foreach ($asset in $assets) {
        Confirm-PublicArchive $asset ($dataPacks.Contains($asset.Name))
    }
    if (-not $existing) {
        $releaseDefinition = @{ tag_name = $Tag; name = $Title; body = $notes; draft = $true; prerelease = $false }
        if ($TargetCommit) {
            $releaseDefinition.target_commitish = $TargetCommit
        }
        $body = $releaseDefinition | ConvertTo-Json
        $existing = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -Body $body
    }
    foreach ($file in $assets) {
        $uploaded = $existing.assets | Where-Object name -eq $file.Name | Select-Object -First 1
        if ($uploaded) {
            $expected = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            if ($uploaded.digest -eq $expected) {
                continue
            }
            Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($uploaded.id)" -Headers $headers | Out-Null
        }
        if ($file.Length -ge 2GB) {
            throw "Release asset exceeds the size limit: $($file.Name)"
        }
        $upload = $existing.upload_url.Split('{')[0] + '?name=' + [uri]::EscapeDataString($file.Name)
        $result = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType 'application/octet-stream' -InFile $file.FullName -TimeoutSec 1800
        if ($result.size -ne $file.Length) {
            throw "Release asset upload did not finish: $($file.Name)"
        }
        Write-Output $file.Name
    }
    $latestMode = if ($Kind -eq 'Launcher') { 'true' } else { 'false' }
    $finalRelease = @{ draft = $false; prerelease = $false; make_latest = $latestMode; name = $Title; body = $notes }
    if ($TargetCommit) {
        $finalRelease.target_commitish = $TargetCommit
    }
    $body = $finalRelease | ConvertTo-Json
    $release = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($existing.id)" -Headers $headers -ContentType 'application/json' -Body $body
    Write-Output $release.html_url
} finally {
    $credential.Clear()
    $credentialLines = $null
    $headers = $null
}

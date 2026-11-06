param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$AssetsDirectory,
    [string[]]$Attachment = @(),
    [string]$Title,
    [string]$NotesFile,
    [string]$Repository = 'Aeracy/Arys-Launcher'
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Repository -eq 'Aeracy/arysmod') {
    throw 'Choose the separate public launcher repository.'
}
if (-not $AssetsDirectory) {
    $AssetsDirectory = Join-Path $PSScriptRoot ('artifacts\release-' + $Tag)
}
if (-not $Title) {
    $Title = "Ary's Launcher $Tag"
}
$notes = if ($NotesFile) { Get-Content -LiteralPath $NotesFile -Raw } else { "Released by Aeracy." }
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
    if ($existing -and -not $existing.draft) {
        throw 'This release is already public. Publish a new version instead of overwriting it.'
    }
    $assets = [Collections.Generic.List[IO.FileInfo]]::new()
    $assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
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
        }
    }
    foreach ($path in $Attachment) {
        $assets.Add((Get-Item -LiteralPath $path))
    }
    $names = @($assets.Name)
    foreach ($name in @('launcher-update.json', 'pack-update.json')) {
        if ($names -contains $name) {
            continue
        }
        try {
            $latest = Invoke-RestMethod -Uri "$api/releases/latest" -Headers $headers
        } catch {
            if ([int]$_.Exception.Response.StatusCode -eq 404) {
                continue
            }
            throw
        }
        foreach ($assetName in @($name, ($name + '.sig'))) {
            $old = $latest.assets | Where-Object name -eq $assetName | Select-Object -First 1
            if ($old) {
                $path = Join-Path $AssetsDirectory $assetName
                Invoke-WebRequest -Uri $old.browser_download_url -OutFile $path
                $assets.Add((Get-Item -LiteralPath $path))
            }
        }
    }
    if ($assets.Name | Group-Object | Where-Object Count -gt 1) {
        throw 'Release attachments have duplicate names.'
    }
    if (-not $existing) {
        $body = @{ tag_name = $Tag; name = $Title; body = $notes; draft = $true } | ConvertTo-Json
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
    $body = @{ draft = $false; make_latest = 'true'; body = $notes } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($existing.id)" -Headers $headers -ContentType 'application/json' -Body $body
    Write-Output $release.html_url
} finally {
    $credential.Clear()
    $credentialLines = $null
    $headers = $null
}

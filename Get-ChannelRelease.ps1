[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Launcher', 'Pack')][string]$Kind,
    [string]$Repository = 'Aeracy/Arys-Launcher'
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Repository -eq 'Aeracy/arysmod') {
    throw 'Choose the separate public launcher repository.'
}
$channel = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-channel.json') -Raw | ConvertFrom-Json
$prefix = if ($Kind -eq 'Launcher') { $channel.LauncherTagPrefix } else { $channel.PackTagPrefix }
if (-not $prefix) {
    throw 'The release channel is missing its tag prefix.'
}
$manifestName = if ($Kind -eq 'Launcher') { 'launcher-update.json' } else { 'pack-update.json' }
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'ArysLauncher-Publisher' }
$selected = $null
$selectedVersion = $null
for ($page = 1; $page -le 10; $page++) {
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=100&page=$page" -Headers $headers -TimeoutSec 40
    $releases = @($response)
    foreach ($release in $releases) {
        $version = $null
        if ($release.draft -or $release.prerelease -or -not $release.tag_name.StartsWith($prefix, [StringComparison]::Ordinal) -or
            -not [version]::TryParse($release.tag_name.Substring($prefix.Length), [ref]$version)) {
            continue
        }
        $names = @($release.assets.name)
        if ($manifestName -notin $names -or ($manifestName + '.sig') -notin $names) {
            continue
        }
        if (-not $selected -or $version -gt $selectedVersion) {
            $selected = $release
            $selectedVersion = $version
        }
    }
    if ($releases.Count -lt 100) {
        break
    }
}
if (-not $selected) {
    throw "No signed $Kind release was found under $prefix."
}
return $selected

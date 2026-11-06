$ErrorActionPreference = 'Stop'
$runtimeVersion = '156.0.4314.58'
$runtimeName = "Microsoft.WebView2.FixedVersionRuntime.$runtimeVersion.x64"
$toolsRoot = Join-Path $PSScriptRoot '.tools'
$runtimeRoot = Join-Path $toolsRoot 'webview2'
$runtimeDirectory = Join-Path $runtimeRoot $runtimeName
$runtimeExecutable = Join-Path $runtimeDirectory 'msedgewebview2.exe'
$readyMarker = Join-Path $runtimeRoot "$runtimeName.ready"
$archivePath = Join-Path $toolsRoot "$runtimeName.cab"
$archiveHash = '857DCFD6F7035508A29645FA2978C3A6A841F220DF77CC366D318894E19F4792'
$source = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/b7919cc9-680d-4253-bf32-732988e68aa6/Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $runtimeExecutable) -or -not (Test-Path -LiteralPath $readyMarker)) {
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $source -OutFile "$archivePath.partial"
        if ((Get-FileHash -LiteralPath "$archivePath.partial" -Algorithm SHA256).Hash -ne $archiveHash) {
            throw 'The WebView2 archive checksum does not match.'
        }
        Move-Item -LiteralPath "$archivePath.partial" -Destination $archivePath -Force
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveHash) {
        throw 'The cached WebView2 archive checksum does not match.'
    }
    & "$env:SystemRoot\System32\expand.exe" $archivePath '-F:*' $runtimeRoot > (Join-Path $toolsRoot 'webview2-extract.log')
    if ($LASTEXITCODE -ne 0) {
        throw "WebView2 extraction failed with exit code $LASTEXITCODE."
    }
}
$signature = Get-AuthenticodeSignature -LiteralPath $runtimeExecutable
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
    throw 'The WebView2 executable must have a valid Microsoft signature.'
}
if ((Get-Item -LiteralPath $runtimeExecutable).VersionInfo.FileVersion -ne $runtimeVersion) {
    throw 'The bundled WebView2 runtime version does not match the build.'
}
Set-Content -LiteralPath $readyMarker -Value $archiveHash -Encoding ascii
Write-Output "WebView2 $runtimeVersion x64 is ready."

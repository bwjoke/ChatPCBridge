[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackagePath,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedThumbprint
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $PackagePath) {
    $build = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\latest-build.json') -Raw | ConvertFrom-Json
    $PackagePath = $build.PackagePath
}
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'The package has no AppxManifest.xml.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
if ($manifest.Package.Identity.Name -ne 'ChatPCBridge.Local' -or $manifest.Package.Identity.Publisher -ne 'CN=ChatPCBridge Local Development') {
    throw 'Package identity is not ChatPCBridge.Local with the expected local publisher.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -ne $manifest.Package.Identity.Publisher) { throw 'Package is unsigned or has an unexpected signer. Build and sign it locally before installation.' }
if ($ExpectedThumbprint -and $signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint) { throw 'Package signer does not match the expected certificate thumbprint.' }
Write-Host "Package: $PackagePath"
Write-Host "Version: $($manifest.Package.Identity.Version)"
Write-Host "Signer thumbprint: $($signature.SignerCertificate.Thumbprint)"
if ($signature.Status -ne 'Valid') {
    throw "Package signature is not trusted ($($signature.Status)). Trust only your own local certificate using Trust-LocalCertificate.ps1 and its expected thumbprint before installation."
}
if ($PSCmdlet.ShouldProcess('Current user', "Install ChatPCBridge $($manifest.Package.Identity.Version)")) {
    Add-AppxPackage -Path $PackagePath
    Get-AppxPackage -Name 'ChatPCBridge.Local' | Where-Object { $_.Name -eq 'ChatPCBridge.Local' -and $_.Publisher -eq 'CN=ChatPCBridge Local Development' } | Select-Object Name, Version, PackageFamilyName, InstallLocation
    Write-Host 'ChatPCBridge is installed. Close and reopen the source application forwarding dialog to refresh its target list.'
}

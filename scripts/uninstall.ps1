[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$RemoveCertificate,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($RemoveCertificate) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '-RemoveCertificate requires an elevated PowerShell. No package or certificate was removed.'
    }
}
$packages = @(Get-AppxPackage -Name 'ChatPCBridge.Local' | Where-Object { $_.Name -eq 'ChatPCBridge.Local' -and $_.Publisher -eq 'CN=ChatPCBridge Local Development' })
foreach ($package in $packages) {
    if ($PSCmdlet.ShouldProcess($package.PackageFullName, 'Uninstall ChatPCBridge for the current user')) { Remove-AppxPackage -Package $package.PackageFullName }
}
if ($RemoveCertificate) {
    if (-not $CertificateThumbprint) {
        $metadataPath = Join-Path $PSScriptRoot '..\artifacts\signing-certificate.json'
        if (-not (Test-Path -LiteralPath $metadataPath)) { throw 'Signing certificate metadata is missing; provide -CertificateThumbprint. No certificate was removed.' }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        if ($metadata.Subject -ne 'CN=ChatPCBridge Local Development' -or $metadata.Thumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid signing certificate metadata.' }
        $CertificateThumbprint = $metadata.Thumbprint
    }
    $certificatePath = "Cert:\LocalMachine\TrustedPeople\$CertificateThumbprint"
    if (Test-Path -LiteralPath $certificatePath) {
        $certificate = Get-Item -LiteralPath $certificatePath
        if ($certificate.Subject -ne 'CN=ChatPCBridge Local Development') { throw 'Certificate subject mismatch; no certificate was removed.' }
        if ($PSCmdlet.ShouldProcess($certificatePath, 'Remove this project signing certificate from TrustedPeople (requires administrator)')) { Remove-Item -LiteralPath $certificatePath }
    }
}
Write-Host 'ChatPCBridge uninstall command finished. Chat archives in %USERPROFILE%\ChatPCBridge\Inbox are retained; no chat data was deleted.'

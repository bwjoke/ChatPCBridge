[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedThumbprint,
    [string]$CertificatePath = (Join-Path $PSScriptRoot '..\artifacts\ChatPCBridge.Local.cer')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Trusting this local certificate requires an elevated PowerShell. No settings were changed.'
}
$certificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
try {
    if ($cert.Thumbprint -ne $ExpectedThumbprint -or $cert.Subject -ne 'CN=ChatPCBridge Local Development') { throw 'Certificate identity does not match the expected local publisher and thumbprint.' }
    if ($cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) { throw 'Certificate is outside its validity period.' }
    $eku = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($eku.Count -ne 1 -or -not ($eku[0].EnhancedKeyUsages.Value -contains '1.3.6.1.5.5.7.3.3')) { throw 'Certificate must have the Code Signing extended key usage.' }
    $constraints = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($constraints.Count -ne 1 -or $constraints[0].CertificateAuthority) { throw 'Only a leaf certificate may be trusted by this script.' }
    Write-Host "Publisher: $($cert.Subject)"
    Write-Host "Thumbprint: $($cert.Thumbprint)"
    if ($PSCmdlet.ShouldProcess("LocalMachine\TrustedPeople\$($cert.Thumbprint)", 'Trust this explicit local code-signing certificate')) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        Write-Host 'The selected leaf certificate is now trusted. No root CA or developer-mode setting was changed.'
    }
} finally { $cert.Dispose() }

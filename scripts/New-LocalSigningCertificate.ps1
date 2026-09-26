[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$subject = 'CN=ChatPCBridge Local Development'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$null = New-Item -ItemType Directory -Force -Path $artifacts
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
    $_.NotBefore -le (Get-Date) -and ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    if (-not $PSCmdlet.ShouldProcess('CurrentUser\My', 'Create a nonexportable local package-signing certificate')) { return }
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'ChatPCBridge local package signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 `
        -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
$publicPath = Join-Path $artifacts 'ChatPCBridge.Local.cer'
if (-not $PSCmdlet.ShouldProcess($artifacts, 'Export the public certificate and its local signing metadata')) { return }
Export-Certificate -Cert $certificate -FilePath $publicPath -Force | Out-Null
[ordered]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    CertificatePath = $publicPath
    Expires = $certificate.NotAfter.ToString('o')
    Store = 'CurrentUser\My'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'signing-certificate.json') -Encoding UTF8
Write-Host "Signing certificate: $($certificate.Thumbprint)"
Write-Host "Public certificate: $publicPath"
Write-Host 'The certificate has NOT been trusted. Installation can explicitly import this public certificate to LocalMachine\TrustedPeople.'
$certificate.Thumbprint

[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\ChatPCBridge\ChatPCBridge.csproj'),
    [string]$SdkToolsPath,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.2.1.0',
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$CertificateThumbprint,
    [switch]$NoRestore,
    [switch]$DownloadBuildTools
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $projectRoot '.tools'
$artifacts = Join-Path $projectRoot 'artifacts'
$null = New-Item -ItemType Directory -Force -Path $artifacts

if (-not $DotnetPath) {
    $DotnetPath = Join-Path $toolsRoot 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Get-Command $DotnetPath -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) { throw "Project not found: $ProjectPath" }
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$sdkVersion = '10.0.26100.9169'
$sdkPackageSha256 = '6000C971FC9155052A8359779B30B6682C39E091664D83B3852C4702AB6D238E'
if (-not $SdkToolsPath) {
    $sdkRoot = Join-Path $toolsRoot "windows-sdk-buildtools\$sdkVersion"
    $SdkToolsPath = Join-Path $sdkRoot 'bin\10.0.26100.0\x64'
    if (-not (Test-Path -LiteralPath (Join-Path $SdkToolsPath 'makeappx.exe'))) {
        if (-not $DownloadBuildTools) { throw 'Windows SDK tools missing. Supply -SdkToolsPath or -DownloadBuildTools.' }
        $sdkParent = Split-Path $sdkRoot -Parent
        $null = New-Item -ItemType Directory -Force -Path $sdkParent
        $archive = Join-Path $sdkParent "Microsoft.Windows.SDK.BuildTools.$sdkVersion.nupkg"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$sdkVersion/microsoft.windows.sdk.buildtools.$sdkVersion.nupkg" -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sdkPackageSha256) { throw 'Downloaded SDK BuildTools package does not match its pinned SHA256.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path -LiteralPath $sdkRoot) { throw "An incomplete SDK tools directory already exists: $sdkRoot. Choose -SdkToolsPath or inspect that directory before retrying." }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $sdkRoot)
    }
}
$makeAppx = Join-Path $SdkToolsPath 'makeappx.exe'
$signTool = Join-Path $SdkToolsPath 'signtool.exe'
foreach ($sdkTool in @($makeAppx, $signTool)) {
    if (-not (Test-Path -LiteralPath $sdkTool -PathType Leaf)) { throw "SDK tool missing: $sdkTool" }
    $toolSignature = Get-AuthenticodeSignature -LiteralPath $sdkTool
    if ($toolSignature.Status -ne 'Valid' -or $toolSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw "SDK tool does not have a valid Microsoft signature: $sdkTool ($($toolSignature.Status))"
    }
}
if ($CertificateThumbprint) {
    $CertificateThumbprint = $CertificateThumbprint.Replace(' ', '')
    if ($CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid signing certificate thumbprint.' }
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if ($cert.Subject -ne 'CN=ChatPCBridge Local Development' -or -not $cert.HasPrivateKey) { throw 'Certificate subject must match the package publisher and its private key must be available.' }
    if ($cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) { throw 'Signing certificate is outside its validity period.' }
    if (-not ($cert.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) { throw 'Signing certificate must have the Code Signing extended key usage.' }
}

# Each build uses a fresh directory. It never deletes an existing source/output tree.
$buildId = "${Version}_${Architecture}_" + (Get-Date -Format 'yyyyMMdd_HHmmss_fff')
$stage = Join-Path $artifacts "staging\$buildId"
$null = New-Item -ItemType Directory -Force -Path $stage
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $toolsRoot 'dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $toolsRoot 'nuget-packages' }
# Resolve global.json from this repository even when invoked from another cwd.
Push-Location $projectRoot
try {
    $publishArguments = @('publish', $ProjectPath, '-c', 'Release', '-r', "win-$Architecture", '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:RestoreLockedMode=true', '-o', $stage)
    if ($NoRestore) { $publishArguments += '--no-restore' }
    & $DotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
if (-not (Test-Path -LiteralPath (Join-Path $stage 'ChatPCBridge.exe'))) { throw 'Published application must be named ChatPCBridge.exe.' }

& (Join-Path $PSScriptRoot 'New-Assets.ps1') -OutputDirectory (Join-Path $stage 'Assets')
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.ProcessorArchitecture = $Architecture
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
$packagePath = Join-Path $artifacts "ChatPCBridge_${Version}_${Architecture}.msix"
& $makeAppx pack /d $stage /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE" }
if ($CertificateThumbprint) {
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $packagePath
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE" }
}
$result = [ordered]@{
    PackagePath = $packagePath
    StagingDirectory = $stage
    Version = $Version
    Architecture = $Architecture
    Signed = [bool]$CertificateThumbprint
    CertificateThumbprint = $CertificateThumbprint
    SHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'latest-build.json') -Encoding UTF8
($result.SHA256.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($packagePath)) | Set-Content -LiteralPath ($packagePath + '.sha256') -Encoding ASCII
$result | ConvertTo-Json
if (-not $CertificateThumbprint) { Write-Warning 'Package is unsigned. Sign it before installation.' }

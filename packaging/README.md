# Build and package ChatPCBridge

Use Windows PowerShell 5.1 or PowerShell 7 and the **.NET 10.0.401 SDK**. The application declares Windows 10 2004 as its minimum build/runtime baseline; the WeChat sharing menu has been tested on Windows 11, so the manifest baseline does not promise that WeChat exposes the same sharing flow on Windows 10. `global.json` permits later patches in the same SDK feature band. Visual Studio is not required. The public package identity is `ChatPCBridge.Local`, with publisher `CN=ChatPCBridge Local Development`.

Run these commands from the repository root. Download the SDK from [Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0) if `dotnet --version` cannot resolve `global.json`.

## Build an unsigned package

```powershell
./scripts/build.ps1 -DownloadBuildTools
```

The default version is `0.2.0.0`. This builds a self-contained x64 application, generates icons from the geometry in `New-Assets.ps1`, and creates:

```text
artifacts/ChatPCBridge_0.2.0.0_x64.msix
artifacts/ChatPCBridge_0.2.0.0_x64.msix.sha256
```

`-DownloadBuildTools` explicitly downloads the pinned official `Microsoft.Windows.SDK.BuildTools` NuGet package, version `10.0.26100.9169`, to this repository's `.tools` folder and verifies its pinned SHA256. Both MakeAppx and SignTool must also have valid Microsoft Authenticode signatures before execution. Subsequent builds can omit that switch. Alternatively pass `-SdkToolsPath` with the directory containing those two tools. `-DotnetPath` can point to a portable `dotnet.exe`; otherwise the script uses `.tools/dotnet/dotnet.exe` or the SDK on `PATH`.

The publish step uses locked NuGet restore. Use `-NoRestore` only after a successful restore of the same source and target runtime. CI verifies x64; experimenting with `-Architecture arm64` also requires intentionally regenerating and reviewing runtime-specific dependency lock files. Set a higher four-part `-Version` before updating an installed package. Each build uses a new staging directory under `artifacts/staging`; the script does not delete existing files or include anything from the user's archive directory.

The unsigned artifact is intended for build review. Windows installation requires signing and trust; the installer never bypasses signature validation or changes developer mode.

## Run the automated checks

```powershell
dotnet restore ChatPCBridge.sln --locked-mode
dotnet run --project tests/ArchiveInspector.Tests/ArchiveInspector.Tests.csproj --configuration Release --no-restore
dotnet run --project tests/InstanceCoordinator.Tests/InstanceCoordinator.Tests.csproj --configuration Release --no-restore
dotnet run --project tests/BridgeStore.Tests/BridgeStore.Tests.csproj --configuration Release --no-restore
```

The archive checks generate synthetic test data. The instance checks use child processes with random GUID scopes and do not interact with a running ChatPCBridge instance or its archives. They do not replace testing the Windows share UI and a real WeChat export on the target machine.

## Optional local signing

Build and inspect the source before creating a local certificate. This is for your own development machine; do not distribute its certificate or private key as a release credential.

```powershell
./scripts/New-LocalSigningCertificate.ps1
$signing = Get-Content ./artifacts/signing-certificate.json -Raw | ConvertFrom-Json
./scripts/build.ps1 -CertificateThumbprint $signing.Thumbprint
```

The private key is nonexportable and stays in `CurrentUser\My`. Only a public `.cer` and local metadata are written under ignored `artifacts`. Certificate creation does **not** grant trust, and the build script requires the exact certificate thumbprint, the expected publisher, a valid date range, and Code Signing usage.

Review the generated fingerprint. In an administrator PowerShell, using the same Windows user, explicitly trust that leaf certificate:

```powershell
$signing = Get-Content ./artifacts/signing-certificate.json -Raw | ConvertFrom-Json
./scripts/Trust-LocalCertificate.ps1 -ExpectedThumbprint $signing.Thumbprint
```

This one separate command imports the selected public certificate into `LocalMachine\TrustedPeople`. It validates the fingerprint and publisher and rejects CA certificates. Nothing invokes this command automatically. Then install from a normal PowerShell:

```powershell
$signing = Get-Content ./artifacts/signing-certificate.json -Raw | ConvertFrom-Json
./scripts/install.ps1 -ExpectedThumbprint $signing.Thumbprint
```

`install.ps1` uses `artifacts/latest-build.json` unless `-PackagePath` is supplied. It checks the package identity, signing certificate, optional fingerprint, and Windows trust status before calling `Add-AppxPackage`.

## Uninstall without deleting chat archives

```powershell
./scripts/uninstall.ps1
```

The script only removes the exact `ChatPCBridge.Local` identity with this project's publisher. It retains `%USERPROFILE%\ChatPCBridge\Inbox` and does not alter any older independently named prototypes.

To also remove the locally trusted certificate, run in an administrator PowerShell:

```powershell
./scripts/uninstall.ps1 -RemoveCertificate
```

This uses the precise fingerprint in `artifacts/signing-certificate.json` and checks the publisher again. If that metadata is unavailable, supply `-CertificateThumbprint` explicitly. Other trusted certificates and the development private key in `CurrentUser\My` remain untouched.

## CI and published files

The workflow runs the three console checks after locked restore, compiles the manual ShareProbe diagnostic, builds the unsigned package, and uploads **only** that `.msix` and its SHA256 file for seven days. It uses `contents: read`, no secrets, `pull_request` rather than `pull_request_target`, and full commit pins for official GitHub actions. It does not run installation, certificate generation/trust, or ShareProbe's interactive commands. Do not add `artifacts`, `.tools`, credentials, private archives, screenshots, or machine-specific logs to source control.

References: [Windows share targets](https://learn.microsoft.com/windows/apps/develop/windows-integration/integrate-sharesheet-receive), [MSIX signing certificates](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing), [pinned SDK BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.9169).

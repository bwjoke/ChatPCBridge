# ChatPCBridge

A Windows share target that brings chat archives exported by WeChat to ChatGPT.

[简体中文](README.md) · [Architecture](docs/architecture.md) · [Security](SECURITY.md)

Select messages in WeChat, forward them as a merged chat, and choose **ChatPCBridge**. The app saves the original ZIP locally, prepares optional text files, copies the files to the clipboard and opens ChatGPT. Paste with **Ctrl+V**, verify the attachment, then send your own analysis request.

It does not read or decrypt WeChat databases, inject into WeChat, submit chat messages, use private upload endpoints or require an API key. This independent Windows project is inspired by [WeChatBridge](https://github.com/freestylefly/WeChatBridge), and is not affiliated with Tencent or OpenAI.

## Status

Version **0.2.0** is the first public source release. A private prototype was tested end-to-end with Windows 11 25H2 and WeChat 4.1.15. Compatibility with other OS/application combinations remains to be verified. The UI is currently Chinese.

There is **no publicly trusted signed installer yet**. CI produces an unsigned MSIX for build verification; it is not a ready-to-install trusted release. Do not disable Windows security checks or trust unknown certificates.

## Build

On Windows x64, install PowerShell 7 and the .NET 10 SDK specified in `global.json`:

```powershell
git clone https://github.com/bwjoke/ChatPCBridge.git
cd ChatPCBridge
pwsh -File ./scripts/build.ps1 -DownloadBuildTools
dotnet run --project tests/ArchiveInspector.Tests -c Release
dotnet run --project tests/BridgeStore.Tests -c Release
dotnet run --project tests/InstanceCoordinator.Tests -c Release
```

Build tools are fetched from a pinned Microsoft SDK package and checked for Microsoft signatures. The MSIX includes the .NET runtime. See [packaging instructions](packaging/README.md) for explicit local signing, trust and uninstall steps.

## Behavior and data

- Supports ZIP/TXT import and drag-and-drop, plus Windows share activation.
- Keeps one main window; temporary receivers finish copying and notify it before exiting.
- Preserves original files in `%USERPROFILE%\ChatPCBridge\Inbox`. Updating or uninstalling the package does not delete that folder.
- Does not automatically delete or additionally encrypt local archives.
- Offers extracted TXT when the destination cannot accept ZIP. Media remains in the original archive.
- File copying and app activation do not prove upload success; check ChatGPT's actual attachment state.
- The desktop receiver runs with the user's normal permissions. It is not an AppContainer sandbox.

No real conversations, local certificates, credentials or machine diagnostics are included in this repository. Use synthetic examples in public reports. See [privacy](docs/privacy.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md) and [MIT license](LICENSE).

# Contributing

Small, reviewable changes and synthetic reproductions are welcome. Discuss major UI, platform or data-handling changes in an issue first.

## Development

Use Windows x64, PowerShell 7 and the SDK in `global.json`. See the [README](README.md) and [packaging guide](packaging/README.md).

```powershell
dotnet build src/ChatPCBridge/ChatPCBridge.csproj -c Release
dotnet run --project tests/ArchiveInspector.Tests -c Release
dotnet run --project tests/BridgeStore.Tests -c Release
dotnet run --project tests/InstanceCoordinator.Tests -c Release
pwsh -File scripts/build.ps1 -DownloadBuildTools
```

Tests must not require a personal account, real chats or a signed application installation. Keep Windows share integration tests manual and explicitly scoped to this app. Never automate sending messages or uploading real test conversations as part of CI.

## Pull requests

- Explain the user-visible problem and final behavior; include relevant validation.
- Test new security boundaries and lifecycle failure cases. Avoid tests that merely restate implementation details.
- Keep new dependencies justified and versioned. Pin GitHub Actions to full commit SHAs and use minimum permissions.
- Preserve original archives and report partial failure honestly. Do not claim an attachment uploaded merely because clipboard or app activation succeeded.
- Use generated synthetic examples. Do not commit `artifacts`, `bin`, `obj`, `.tools`, certificates, credentials, chat ZIPs, screenshots of real chats, batch manifests or local logs.
- Preserve third-party attribution when adapting code. Contributions are licensed under the repository's MIT license.

## Reports

For ordinary bugs, provide OS/app versions and minimal steps without private data. For vulnerabilities, use the private channel in [SECURITY.md](SECURITY.md).

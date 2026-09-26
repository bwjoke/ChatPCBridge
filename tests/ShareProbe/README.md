# Manual Windows share diagnostic

This optional WPF tool queries Windows share targets and can send a small **synthetic** ZIP only to the installed `ChatPCBridge.Local` package. It is not run by CI and does not upload anything directly to ChatGPT. Receiving may copy the file to the clipboard and open the configured app.

```powershell
dotnet run --project tests/ShareProbe -- --file <synthetic.zip> --output <local-result.json> --discover-only --max-targets 20
```

`--discover-only` enumerates targets and never transfers. Add `--inspect-icons` to write local icon diagnostics. To test actual share activation, omit `--discover-only`; the exact package identity and target label are checked before transfer. A current Windows 11 build supporting `TransferTargetWatcher` is required.

Use an existing synthetic ZIP smaller than 4 MiB. The tool cannot determine whether its contents are actually fictional. Result files can contain installed application IDs and local paths; keep them outside Git and redact them before reporting a problem. `--output` must be a dedicated `.json` path, not an existing document you want to keep.

Do not use real chats in automated tests. Do not interpret a successful Windows transfer as proof that ChatGPT accepted an attachment.

# Privacy and local data

ChatPCBridge consumes files you explicitly share or import. It does not scan WeChat databases, decrypt messages, monitor conversations, collect telemetry or access ChatGPT credentials.

## What is stored

By default `%USERPROFILE%\ChatPCBridge` contains original ZIP/TXT files, generated text derivatives, batch metadata, settings and a local diagnostic log. Metadata may include filenames, local paths, file sizes, hashes and failure details; filenames and errors can themselves be sensitive. Treat this entire directory as private.

There is no additional encryption or automatic retention cleanup. Updating or uninstalling the package does not delete this user-owned directory. Remove data manually when you no longer need it, and consider the behavior of any backup or synchronization software installed on your computer.

The public version uses its own data directory. It does not silently move, delete or publish data from earlier privately built prototypes.

## Clipboard and external applications

The app copies file references or an analysis prompt to the Windows clipboard. Other applications running in your session, including clipboard managers, may be able to read it. Copying a prompt replaces previously copied files.

The bridge itself does not upload archive contents. When you paste, drag or attach a file to ChatGPT or another service, that destination handles it according to its account settings and policies. Opening a browser target makes a normal request to the ChatGPT website.

## Sharing diagnostics

Use fictional messages and generated files for public bug reports. Do not attach real ZIPs, `batch.json`, logs, application lists, certificate files, personal paths or screenshots of real conversations. If detailed data is needed to investigate a vulnerability, first use the private reporting channel in [SECURITY.md](../SECURITY.md).

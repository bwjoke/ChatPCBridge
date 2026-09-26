# Security policy

Please report vulnerabilities through [GitHub private vulnerability reporting](https://github.com/bwjoke/ChatPCBridge/security/advisories/new). Do not put credentials, real conversations, archive contents, private file paths or local logs in a public issue. Include a minimal synthetic reproduction and affected version when possible.

The latest source on `main` is the supported development version. The project is early-stage and does not offer a guaranteed response time or long-term support commitment.

## Security boundaries

- Only user-shared/imported files are consumed. No database decryption, process injection or archive execution is implemented.
- Original ZIPs are preserved. Text extraction uses generated destination names, resource limits and integrity checks rather than trusting archive entry paths.
- Interprocess messages are scoped to the current Windows user/session and carry validated batch identifiers, not executable commands or arbitrary source paths.
- The app is a full-trust desktop process at normal user privilege, **not** a sandbox or a security boundary against software already running as the same user.
- Local archives, text, metadata and settings are not additionally encrypted and are not automatically deleted. Files intentionally pasted into an AI app are processed by that app under its own policies.
- The application does not use a ChatGPT API key or access the user's ChatGPT authentication data.

## Signing and distribution

Do not upload private signing keys, PFX files, local certificates, certificate metadata or signed private development installers. Generate a signing key on the machine that needs a local development build. Certificate trust is a separate, explicit administrative operation; never disable Windows signature validation to install a build.

CI builds are unsigned and contain no signing secrets. A successful CI run verifies the build/tests, not every Windows/WeChat combination or an external application's upload behavior.

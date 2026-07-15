# Security

Codex Installer is an independent community project. It is not an official OpenAI or Microsoft installer.

## Trust boundaries

- `store.rg-adguard.net` is used only to discover Microsoft Store delivery links.
- Downloads are restricted to HTTPS URLs under Microsoft's `delivery.mp.microsoft.com` domain.
- Downloaded MSIX files must pass Windows signature validation.
- The MSIX Manifest must match the expected `OpenAI.Codex` name, publisher, version, and architecture.
- The bundled WebView2 Evergreen Bootstrapper is pinned by SHA-256 and must have a valid Microsoft signature.
- The application runs package installation as the current Windows user and does not request elevation.

## Release verification

GitHub Actions builds and tests release executables and produces a GitHub artifact provenance attestation. The executable does not currently have an Authenticode code-signing certificate. Windows SmartScreen may therefore show an unknown-publisher warning.

## Reporting a vulnerability

Do not publish exploitable details in a public issue. Contact the repository owner through GitHub with a minimal reproduction and the affected commit or release hash.

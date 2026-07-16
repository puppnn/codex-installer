# Security

Codex Installer is an independent community project. It is not an official OpenAI or Microsoft installer.

## Trust boundaries

- `store.rg-adguard.net` is used only to discover Microsoft Store delivery links. Its page hash and HTML are not treated as package identity authority.
- Advanced mode accepts only HTTPS Microsoft Store URLs and verifies structured metadata from `apps.microsoft.com` before searching.
- Store metadata must match the requested ProductId, use the `WindowsUpdate` installer type, support `Windows.Desktop`, and provide at least one valid Package Family Name.
- Downloads are restricted to `delivery.mp.microsoft.com` and its subdomains on the default HTTP/HTTPS ports.
- HTTPS is attempted first. HTTP is used only when the original rg-adguard link already uses HTTP on the trusted Microsoft delivery domain and HTTPS fails. Certificate validation is never disabled.
- HTTP fallback does not establish package trust. Every downloaded package must pass WinVerifyTrust and strict Manifest identity validation before it can be installed.
- Main packages must match a Package Family Name returned by Microsoft Store metadata. Dependencies must match the required name, publisher, minimum version, and architecture.
- The Package Family Name is recalculated with the Windows `PackageFamilyNameFromId` API and compared exactly.
- A previously downloaded file is reused only when its rg-adguard page hash matches exactly and its signature and Manifest identity pass validation again.
- The bundled WebView2 Evergreen Bootstrapper is pinned by SHA-256 and must have a valid Microsoft signature before execution.
- Package installation runs as the current Windows user through an absolute system PowerShell path and an encoded command. The application does not request elevation.
- Generic mode does not automatically terminate applications. A package-in-use error is shown verbatim so the user can close the relevant application and retry.

## Unsupported content

- WPM, traditional Win32 EXE/MSI installers, encrypted packages, and large game installers are outside the supported scope.
- The tool does not bypass purchase requirements, account licensing, regional restrictions, or DRM.

## Release verification

GitHub Actions builds and tests release executables and produces a GitHub artifact provenance attestation. Release tags are protected and must not be moved or reused.

The executable does not currently have an Authenticode code-signing certificate. Windows SmartScreen may therefore show an unknown-publisher warning. Verify the release SHA-256 and GitHub attestation before running it.

## Reporting a vulnerability

Do not publish exploitable details in a public issue. Contact the repository owner through GitHub with a minimal reproduction and the affected commit or release hash.

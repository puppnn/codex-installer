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
- Package installation runs as the current Windows user through an absolute system PowerShell path and an encoded command. In v1.2.2, only target-package service control uses a separate UAC helper; the installer and Add-AppxPackage are not elevated.
- Generic mode does not automatically terminate applications. A package-in-use error is shown verbatim so the user can close the relevant application and retry.
- Local-package mode requires explicit file selection and installation confirmation. It validates Windows signatures, manifest identity, host architecture, minimum Windows version, dependencies, and the installed application version. It does not claim Microsoft Store metadata verification for user-selected local files.
- Bundle container versions are used to match downloaded artifacts; the selected payload's application version is used to prevent downgrades. Bundle payload selection for installation follows the host architecture.
- Network requests validate each redirect before connecting to the next host. Downloads enforce a total deadline, a read-idle timeout, and the remaining installation-plan byte limit.
- Required local dependencies are matched by package family, publisher, architecture, and application version. Optional framework dependencies do not block installation.
- Codex process shutdown requires user confirmation and restricts selection to the current desktop session and the installed package directory, or older directories in the same package store with the verified Codex package family. Process handles are retained before shutdown; unrelated process names and child processes are not selected by name.

The embedded WebView2 installer pin is stored in `src/CodexUpdater.App/Vendor/WebView2Bootstrapper.json`. Build and runtime verification read the same pin. Its URL identifies a specific Microsoft file, rather than a moving latest-version redirect.

## Download routes and recovery

Installer-only route preferences select IPv4 direct connections, the existing system/environment proxy, or automatic fallback. No system proxy, DNS, Clash configuration, or certificate policy is modified. The direct connector resolves addresses through the OS and retains the original hostname for TLS verification.

Version 1.2.2 stores partial downloads and bounded, validated JSON metadata under the download directory's `.codex-installer-partials` subdirectory. A per-file exclusive lease covers download, validation, promotion and cleanup. Cleanup only removes expired pairs with valid ownership metadata and expected hashed filenames in that directory, and skips active leases and reparse points. Unrelated files and legacy root-level partials are not cleaned up. A resumed response must match the strong ETag, range offset and complete length; final hash (when supplied), signature and identity checks remain mandatory. Invalid metadata is ignored. Recoverable network failure/cancellation preserves only a usable recorded state; data validation and disk failures discard partials.

## Service lifecycle

Only originally running services under the installed package directory are candidates for temporary suspension. Confirmation names the services. The elevated helper restricts module resolution to Windows system modules and rechecks exact service names and paths. It uses single-service SCM requests through system `sc.exe`, never recursive `Stop-Service -Force`. Active dependent services cause refusal. Pending or paused states block preflight rather than being silently changed.

All stop attempts are paired with recovery checks in a finally path, including partial failures, cancellation and installation exceptions. Restoration waits until the elevated helper exits, then verifies current package service identity; it never overlaps an in-flight stop helper. A recovery failure is preserved alongside the installation failure or shown as a separate warning after successful deployment. An application crash or forced termination cannot guarantee recovery; users may need to inspect the named services manually. No claim is made that a reboot always restores manual-start services.

## Unsupported content

- WPM, traditional Win32 EXE/MSI installers, encrypted packages, and large game installers are outside the supported scope.
- The tool does not bypass purchase requirements, account licensing, regional restrictions, or DRM.

## Release verification

GitHub Actions builds and tests release executables and produces a GitHub artifact provenance attestation. Release tags are protected and must not be moved or reused.

The executable does not currently have an Authenticode code-signing certificate. Windows SmartScreen may therefore show an unknown-publisher warning. Verify the release SHA-256 and GitHub attestation before running it.

## Reporting a vulnerability

Do not publish exploitable details in a public issue. Contact the repository owner through GitHub with a minimal reproduction and the affected commit or release hash.

namespace CodexUpdater.Core;

public sealed record InstalledCodex(
    string Name,
    string PackageFullName,
    Version Version,
    string Architecture,
    string InstallLocation);

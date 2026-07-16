namespace CodexUpdater.Core;

public sealed record InstalledStorePackage(
    string Name,
    string PackageFullName,
    string PackageFamilyName,
    string Publisher,
    Version Version,
    string Architecture,
    string InstallLocation);

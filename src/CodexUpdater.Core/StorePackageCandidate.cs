using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexUpdater.Core;

public enum StorePackageFormat
{
    Msix,
    Appx,
    MsixBundle,
    AppxBundle,
}

public enum StorePackageRole
{
    Main,
    Dependency,
    Resource,
}

public sealed record StorePackageCandidate(
    string FileName,
    string Url,
    string IdentityName,
    string PublisherId,
    string ResourceId,
    Version Version,
    string Architecture,
    StorePackageFormat Format,
    StorePackageRole Role,
    DateTimeOffset? ExpiresAt,
    string? PageHash)
{
    public string PackageFamilyName => $"{IdentityName}_{PublisherId}";
    public bool IsExpired => ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow;
}

public sealed record PackageLinkRow(
    string Href,
    string Text,
    string? Expires,
    string? PageHash);

public static partial class StorePackageCandidateParser
{
    public static IReadOnlyList<StorePackageCandidate> Parse(
        IEnumerable<PackageLinkRow> rows,
        IReadOnlyCollection<string> mainPackageFamilyNames)
    {
        var expectedFamilies = new HashSet<string>(mainPackageFamilyNames, StringComparer.OrdinalIgnoreCase);
        return rows
            .Select(row => TryParse(row, expectedFamilies, out var candidate) ? candidate : null)
            .Where(candidate => candidate is not null)
            .Cast<StorePackageCandidate>()
            .DistinctBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryParse(
        PackageLinkRow row,
        IReadOnlySet<string> mainPackageFamilyNames,
        out StorePackageCandidate candidate)
    {
        candidate = default!;
        if (!CodexPackage.TryNormalizeTrustedDownloadUrl(row.Href, out var trustedUrl))
        {
            return false;
        }

        var fileName = ExtractFileName(row.Text, trustedUrl);
        if (fileName.Contains(".BlockMap", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".eappx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".emsix", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = InstallablePackageRegex().Match(fileName);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
        {
            return false;
        }

        var format = match.Groups["extension"].Value.ToLowerInvariant() switch
        {
            "msix" => StorePackageFormat.Msix,
            "appx" => StorePackageFormat.Appx,
            "msixbundle" => StorePackageFormat.MsixBundle,
            "appxbundle" => StorePackageFormat.AppxBundle,
            _ => throw new UnreachableException(),
        };
        var name = match.Groups["name"].Value;
        var publisherId = match.Groups["publisher"].Value;
        var resourceId = match.Groups["resource"].Value;
        var familyName = $"{name}_{publisherId}";
        var role = !string.IsNullOrEmpty(resourceId) && resourceId != "~"
            ? StorePackageRole.Resource
            : mainPackageFamilyNames.Contains(familyName)
                ? StorePackageRole.Main
                : StorePackageRole.Dependency;

        candidate = new StorePackageCandidate(
            fileName,
            trustedUrl,
            name,
            publisherId,
            resourceId,
            version,
            match.Groups["architecture"].Value.ToLowerInvariant(),
            format,
            role,
            ParseExpiration(row.Expires),
            string.IsNullOrWhiteSpace(row.PageHash) ? null : row.PageHash.Trim());
        return true;
    }

    private static string ExtractFileName(string text, string url)
    {
        var candidate = text.Trim();
        if (!candidate.Contains('.', StringComparison.Ordinal))
        {
            candidate = Path.GetFileName(new Uri(url).LocalPath);
        }

        return Uri.UnescapeDataString(Path.GetFileName(candidate));
    }

    private static DateTimeOffset? ParseExpiration(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var expiration)
            ? expiration.ToUniversalTime()
            : null;
    }

    [GeneratedRegex(
        @"^(?<name>[A-Za-z0-9.-]+)_(?<version>\d+(?:\.\d+){3})_(?<architecture>x64|x86|arm64|arm|neutral)_(?<resource>[^_]*)_(?<publisher>[A-Za-z0-9]{13})\.(?<extension>msix|appx|msixbundle|appxbundle)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallablePackageRegex();
}

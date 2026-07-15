using System.Text.RegularExpressions;

namespace CodexUpdater.Core;

public static partial class CodexPackage
{
    public const string ProductId = "9PLM9XGG6VKS";
    public const string PackagePrefix = "OpenAI.Codex";
    public const string Publisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    public const string PublisherId = "2p2nqsd0c76g0";
    public const string DefaultArchitecture = "x64";
    public const string RgAdguardUrl = "https://store.rg-adguard.net/";

    public static bool IsExpectedFileName(string fileName)
    {
        return IsExpectedFileName(fileName, DefaultArchitecture);
    }

    public static bool IsExpectedFileName(string fileName, string targetArchitecture)
    {
        return TryParse(fileName, null, targetArchitecture, out _);
    }

    public static bool TryParse(string fileNameOrUrl, string? url, out PackageCandidate candidate)
    {
        return TryParse(fileNameOrUrl, url, DefaultArchitecture, out candidate);
    }

    public static bool TryParse(
        string fileNameOrUrl,
        string? url,
        string targetArchitecture,
        out PackageCandidate candidate)
    {
        candidate = default!;

        var fileName = ExtractFileName(fileNameOrUrl);
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.Contains(".BlockMap", StringComparison.OrdinalIgnoreCase)) return false;

        var match = CodexMsixRegex().Match(fileName);
        if (!match.Success) return false;

        var architecture = match.Groups["architecture"].Value;
        if (!architecture.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase)) return false;

        if (!Version.TryParse(match.Groups["version"].Value, out var version)) return false;

        candidate = new PackageCandidate(
            fileName,
            url ?? fileNameOrUrl,
            version,
            architecture.ToLowerInvariant());
        return true;
    }

    public static PackageCandidate? SelectNewest(IEnumerable<PackageLink> links)
    {
        return SelectNewest(links, DefaultArchitecture);
    }

    public static PackageCandidate? SelectNewest(IEnumerable<PackageLink> links, string targetArchitecture)
    {
        return links
            .SelectMany(link => ParseLink(link, targetArchitecture))
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();
    }

    public static bool IsTrustedDownloadUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            IsTrustedMicrosoftHost(uri.IdnHost);
    }

    public static bool TryNormalizeTrustedDownloadUrl(string url, out string normalizedUrl)
    {
        normalizedUrl = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !IsTrustedMicrosoftHost(uri.IdnHost) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
        };
        normalizedUrl = builder.Uri.AbsoluteUri;
        return true;
    }

    private static IEnumerable<PackageCandidate> ParseLink(PackageLink link, string targetArchitecture)
    {
        if (!TryNormalizeTrustedDownloadUrl(link.Href, out var trustedUrl))
        {
            yield break;
        }

        if (TryParse(link.Text, trustedUrl, targetArchitecture, out var fromText))
        {
            yield return fromText;
        }

        if (!string.Equals(link.Text, link.Href, StringComparison.Ordinal) &&
            TryParse(trustedUrl, trustedUrl, targetArchitecture, out var fromHref))
        {
            yield return fromHref;
        }
    }

    private static bool IsTrustedMicrosoftHost(string host)
    {
        var normalizedHost = host.TrimEnd('.');
        return normalizedHost.Equals("delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            normalizedHost.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFileName(string fileNameOrUrl)
    {
        var value = fileNameOrUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = Path.GetFileName(uri.LocalPath);
        }
        else
        {
            value = Path.GetFileName(value);
        }

        return Uri.UnescapeDataString(value);
    }

    [GeneratedRegex(
        @"^OpenAI\.Codex_(?<version>\d+\.\d+\.\d+\.\d+)_(?<architecture>x64|arm64)__2p2nqsd0c76g0\.msix$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodexMsixRegex();
}

public sealed record PackageCandidate(
    string FileName,
    string Url,
    Version Version,
    string Architecture);

public sealed record PackageLink(string Href, string Text);

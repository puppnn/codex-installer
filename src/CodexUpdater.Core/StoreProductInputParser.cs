using System.Text.RegularExpressions;

namespace CodexUpdater.Core;

public static partial class StoreProductInputParser
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "apps.microsoft.com",
        "microsoft.com",
        "www.microsoft.com",
    };

    public static string Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请输入 Microsoft Store ProductId 或应用网页地址。");
        }

        var value = input.Trim();
        if (TryNormalizeProductId(value, out var directProductId))
        {
            return directProductId;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !AllowedHosts.Contains(uri.IdnHost.TrimEnd('.')) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !IsStorePath(uri))
        {
            throw new InvalidOperationException("仅支持 ProductId 或 Microsoft Store 官方 HTTPS 网页地址。");
        }

        var queryProductId = ParseQuery(uri.Query, "productid");
        if (queryProductId is not null && TryNormalizeProductId(queryProductId, out var normalizedQueryId))
        {
            return normalizedQueryId;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse();
        foreach (var segment in segments)
        {
            if (TryNormalizeProductId(Uri.UnescapeDataString(segment), out var productId))
            {
                return productId;
            }
        }

        throw new InvalidOperationException("无法从 Microsoft Store 地址中解析 ProductId。");
    }

    public static bool TryNormalizeProductId(string value, out string productId)
    {
        productId = value.Trim().ToUpperInvariant();
        return ProductIdRegex().IsMatch(productId) && productId.Any(char.IsDigit);
    }

    private static string? ParseQuery(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator >= 0 ? pair[..separator] : pair;
            if (!Uri.UnescapeDataString(name).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return separator >= 0 ? Uri.UnescapeDataString(pair[(separator + 1)..]) : "";
        }

        return null;
    }

    private static bool IsStorePath(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (uri.IdnHost.TrimEnd('.').Equals("apps.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Any(segment =>
                segment.Equals("detail", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("store", StringComparison.OrdinalIgnoreCase));
        }

        return segments.Any(segment =>
            segment.Equals("store", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("p", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^[A-Z0-9]{10,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductIdRegex();
}

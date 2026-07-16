using System.Text;
using System.Text.Json;

namespace CodexUpdater.Core;

public sealed record StoreProductMetadata(
    string ProductId,
    string DisplayName,
    string InstallerType,
    IReadOnlyList<string> PackageFamilyNames,
    IReadOnlyList<string> AllowedPlatforms,
    string ProductType,
    bool? IsFree);

public static class StoreProductMetadataParser
{
    private const string MetadataMarker = "window.pageMetadata = ";

    public static StoreProductMetadata Parse(string html, string expectedProductId)
    {
        var markerIndex = html.IndexOf(MetadataMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("Microsoft Store 页面中缺少 pageMetadata。");
        }

        var jsonStart = markerIndex + MetadataMarker.Length;
        var bytes = Encoding.UTF8.GetBytes(html[jsonStart..]);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("Microsoft Store pageMetadata 格式无效。");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var installer = RequiredObject(root, "installer");
        var productId = RequiredString(installer, "id").ToUpperInvariant();
        var installerType = RequiredString(installer, "type");
        if (!productId.Equals(expectedProductId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store 页面 ProductId 不匹配：预期 {expectedProductId}，实际 {productId}。");
        }

        if (!installerType.Equals("WindowsUpdate", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"此应用使用 {installerType} 安装器，不是可下载的 APPX/MSIX 包。");
        }

        var platforms = ReadStringArray(root, "allowedPlatforms");
        if (!platforms.Contains("Windows.Desktop", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("此 Store 应用不支持 Windows Desktop。");
        }

        var packageFamilies = ReadStringArray(root, "packageFamilyNames")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packageFamilies.Length == 0 || packageFamilies.Any(value => !PackageFamilyIdentity.TryParse(value, out _)))
        {
            throw new InvalidOperationException("Microsoft Store 页面没有提供有效的 packageFamilyNames。");
        }

        var displayName = OptionalString(root, "title") ?? productId;
        var productType = OptionalString(root, "productType") ?? "Application";
        bool? isFree = root.TryGetProperty("price", out var price) && price.TryGetDecimal(out var amount)
            ? amount == 0
            : null;

        return new StoreProductMetadata(
            productId,
            displayName,
            installerType,
            packageFamilies,
            platforms,
            productType,
            isFree);
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException($"Microsoft Store 元数据缺少 {name}。");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        return OptionalString(element, name)
            ?? throw new InvalidOperationException($"Microsoft Store 元数据缺少 {name}。");
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;
    }

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }
}

public sealed record PackageFamilyIdentity(string Name, string PublisherId)
{
    public static bool TryParse(string packageFamilyName, out PackageFamilyIdentity identity)
    {
        identity = default!;
        var separator = packageFamilyName.LastIndexOf('_');
        if (separator <= 0 || separator == packageFamilyName.Length - 1)
        {
            return false;
        }

        var name = packageFamilyName[..separator];
        var publisherId = packageFamilyName[(separator + 1)..];
        if (name.Length is < 3 or > 50 ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-') ||
            publisherId.Length != 13 ||
            publisherId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        identity = new PackageFamilyIdentity(name, publisherId);
        return true;
    }

    public override string ToString() => $"{Name}_{PublisherId}";
}

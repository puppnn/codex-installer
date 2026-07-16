using System.IO;
using System.Net.Http;
using System.Text;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class MicrosoftStoreMetadataService
{
    private const int MaxPageBytes = 5 * 1024 * 1024;

    public static async Task<StoreProductMetadata> ResolveAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        if (!StoreProductInputParser.TryNormalizeProductId(productId, out var normalizedProductId))
        {
            throw new InvalidOperationException("Microsoft Store ProductId 格式无效。");
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CodexUpdater/1.1");

        var url = $"https://apps.microsoft.com/detail/{Uri.EscapeDataString(normalizedProductId)}?hl=zh-CN&gl=US";
        using var response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !finalUri.IdnHost.TrimEnd('.').Equals("apps.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Microsoft Store 页面重定向到了非官方地址。");
        }

        if (response.Content.Headers.ContentLength > MaxPageBytes)
        {
            throw new InvalidOperationException("Microsoft Store 页面大小异常。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > MaxPageBytes)
            {
                throw new InvalidOperationException("Microsoft Store 页面超过大小上限。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var html = Encoding.UTF8.GetString(destination.ToArray());
        return StoreProductMetadataParser.Parse(html, normalizedProductId);
    }
}
